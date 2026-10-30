using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Keel.Api.Infrastructure;
using Keel.Audit;
using Keel.Bom;
using Keel.Cad;
using Keel.Core;
using Keel.Parts;
using Keel.Security;
using Keel.TestSupport;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Assembly placements and the Viewer tab's data against real PostgreSQL, vault and host (ADR-0052): what is stored with a structure proposal, the routes of the CAD API,
/// and that none of it reaches a caller the markings keep out. The oracle is SQL: the same read filter the whole system uses, evaluated by the database.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CadPlacementsTests(PostgresFixture pg)
{
    private static readonly string[] Secrets = ["KA-3000", "KA-3001", "KF-2001", "KP-1002", "Frame assembly", "Bracket assembly", "Mounting plate", "Screw M5x16"];

    private static byte[] Frame => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cad", "frame-asm-ap214.step"));

    private static async Task<string> CreateProposal(HttpClient c, string markings, string title = "Frame placements")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent($$"""{"title":"{{title}}","format":"step","markings":{{markings}}}""", Encoding.UTF8, "application/json"), "metadata" },
        };
        var file = new ByteArrayContent(Frame);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "frame.step");
        var res = await c.PostAsync("/api/v1/cad/bom-proposals", form);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await CadApiTests.Json(res)).GetProperty("number").GetString()!;
    }

    [Fact]
    public async Task The_placements_of_a_proposal_are_stored_with_its_structure_and_served_expanded()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(CadApiTests.Alice);
        var number = await CreateProposal(alice, CadApiTests.Open);

        var detail = await CadApiTests.Json(await alice.GetAsync($"/api/v1/cad/bom-proposals/{number}"));
        Assert.Equal(14, detail.GetProperty("occurrences").GetInt32());
        Assert.Equal(14, detail.GetProperty("placedOccurrences").GetInt32());
        Assert.Equal("mm", detail.GetProperty("lengthUnit").GetString());
        var plate = detail.GetProperty("parts").EnumerateArray().Single(p => p.GetProperty("cadNumber").GetString() == "KP-1002");
        Assert.Equal(120d, plate.GetProperty("box").GetProperty("sizeX").GetDouble(), 6);

        var res = await alice.GetAsync($"/api/v1/cad/bom-proposals/{number}/placements");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var placements = await CadApiTests.Json(res);
        Assert.Equal(number, placements.GetProperty("number").GetString());
        Assert.Equal(24, placements.GetProperty("instances").GetArrayLength());
        Assert.Equal(14, placements.GetProperty("occurrences").GetArrayLength());
        Assert.Equal(0, placements.GetProperty("unplaced").GetInt32());
        Assert.False(placements.GetProperty("truncated").GetBoolean());
        var world = placements.GetProperty("worldBox");
        Assert.Equal(120d, world.GetProperty("sizeX").GetDouble(), 6);
        Assert.Equal(28d, world.GetProperty("sizeZ").GetDouble(), 6);
        var second = placements.GetProperty("instances").EnumerateArray().Single(i => i.GetProperty("path").GetString() == "0/2");
        var matrix = second.GetProperty("world").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        Assert.Equal((40d, 4d), (matrix[7], matrix[11]));
        Assert.Equal(16, matrix.Length);

        // the rows are the numbers the file stated; the application role can add them but never change or remove them
        await using var conn = await api.Database.AppDataSource().OpenConnectionAsync();
        await using (var count = new NpgsqlCommand("SELECT count(*), count(transform) FROM cad.bom_proposal_occurrence o JOIN cad.bom_proposal p ON p.id = o.proposal_id WHERE p.number = @n", conn))
        {
            count.Parameters.AddWithValue("n", number);
            await using var r = await count.ExecuteReaderAsync();
            await r.ReadAsync();
            Assert.Equal((14L, 14L), (r.GetInt64(0), r.GetInt64(1)));
        }
        foreach (var sql in new[] { "UPDATE cad.bom_proposal_occurrence SET occurrence_id = 'x'", "DELETE FROM cad.bom_proposal_occurrence" })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState); // no UPDATE or DELETE grant
        }
        await using (var audit = new NpgsqlCommand("SELECT count(*) FROM audit.event WHERE action = 'cad.bom_proposal.placements' AND outcome = 'Success' AND object_id = @n", conn))
        {
            audit.Parameters.AddWithValue("n", number);
            Assert.Equal(1L, await audit.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task A_frozen_proposal_cannot_gain_or_lose_placements_even_for_the_owner_of_the_tables()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(CadApiTests.Alice);
        var number = await CreateProposal(alice, CadApiTests.Open);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync($"/api/v1/cad/bom-proposals/{number}/discard", null)).StatusCode);
        await using var conn = new NpgsqlConnection(api.Database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE cad.bom_proposal_occurrence SET name = 'changed' WHERE proposal_id = (SELECT id FROM cad.bom_proposal WHERE number = @n)", conn);
        cmd.Parameters.AddWithValue("n", number);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Contains("no longer a draft", ex.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Placement_routes_agree_with_the_read_filter_for_every_subject_and_leak_nothing_when_they_refuse()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(CadApiTests.Alice);
        var itarProposal = await CreateProposal(alice, CadApiTests.Itar, "Secret frame");
        var openProposal = await CreateProposal(alice, CadApiTests.Open, "Open frame");
        var itarDoc = await CadApiTests.CreateDocument(alice, "Secret frame model", "CAD", Frame, "frame.step", CadApiTests.Itar);
        var openDoc = await CadApiTests.CreateDocument(alice, "Open frame model", "CAD", Frame, "frame-open.step", CadApiTests.Open);

        // (token, the same person as a Subject for the SQL oracle)
        var people = new (string Label, string Token, Subject Subject)[]
        {
            ("alice", CadApiTests.Alice, WorkflowEnv.User("alice", [Roles.Viewer, Roles.Author])),
            ("lena (not a US person)", CadApiTests.Lena, WorkflowEnv.User("lena", [Roles.Viewer, Roles.Author], us: false)),
            ("vic (no program)", ApiFactory.Token(sub: "u-vic", programs: [], roles: ["plm-viewer"]), WorkflowEnv.User("vic", [Roles.Viewer], programs: [])),
            ("nocui (no CUI category)", ApiFactory.Token(sub: "u-nocui", cui: [], roles: ["plm-viewer"]), WorkflowEnv.User("nocui", [Roles.Viewer], cui: [])),
        };

        await using var conn = await api.Database.AppDataSource().OpenConnectionAsync();
        async Task<bool> Visible(Subject s, string table, string numberColumn, string number)
        {
            var filter = AccessPolicy.BuildReadFilter(s, "t");
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table} t WHERE t.{numberColumn} = @n AND {filter.Sql}", conn);
            foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("n", number);
            return (long)(await cmd.ExecuteScalarAsync())! == 1;
        }

        var denials = 0;
        foreach (var (label, token, subject) in people)
        {
            var client = api.ClientFor(token);
            foreach (var number in new[] { itarProposal, openProposal })
            {
                var expected = await Visible(subject, "cad.bom_proposal", "number", number);
                var res = await client.GetAsync($"/api/v1/cad/bom-proposals/{number}/placements");
                var body = await res.Content.ReadAsStringAsync();
                Assert.True(expected == (res.StatusCode == HttpStatusCode.OK), $"{label} / proposal {number}: oracle says {expected}, route said {(int)res.StatusCode}");
                if (!expected) { denials++; Assert.Equal(HttpStatusCode.NotFound, res.StatusCode); Assert.All(Secrets, s => Assert.DoesNotContain(s, body, StringComparison.Ordinal)); }
            }
            foreach (var number in new[] { itarDoc, openDoc })
            {
                var expected = await Visible(subject, "plm.document_master", "number", number);
                var res = await client.GetAsync($"/api/v1/cad/documents/{number}/placements");
                var body = await res.Content.ReadAsStringAsync();
                Assert.True(expected == (res.StatusCode == HttpStatusCode.OK), $"{label} / document {number}: oracle says {expected}, route said {(int)res.StatusCode}");
                if (!expected) { denials++; Assert.Equal(HttpStatusCode.NotFound, res.StatusCode); Assert.All(Secrets, s => Assert.DoesNotContain(s, body, StringComparison.Ordinal)); }
            }
        }
        Assert.True(denials >= 4, "the scenario must contain refusals, or the test proves nothing");

        // a refused document view is audited as a denial and says nothing else; an allowed one is a delivered, audited view of the STEP file
        await using var denied = new NpgsqlCommand("SELECT count(*) FROM audit.event WHERE action = 'document.view' AND outcome = 'Denied' AND object_id = @n", conn);
        denied.Parameters.AddWithValue("n", itarDoc);
        Assert.True((long)(await denied.ExecuteScalarAsync())! >= 1);
        await using var delivered = new NpgsqlCommand("SELECT count(*) FROM audit.event WHERE action = 'document.view' AND outcome = 'Success' AND object_id = @n AND payload LIKE '%assembly%'", conn);
        delivered.Parameters.AddWithValue("n", openDoc);
        Assert.True((long)(await delivered.ExecuteScalarAsync())! >= 1);
    }

    [Fact]
    public async Task The_document_route_reads_the_verified_step_file_and_refuses_what_is_not_an_assembly()
    {
        await using var api = await ApiFactory.CreateAsync(pg);
        var alice = api.ClientFor(CadApiTests.Alice);
        var step = await CadApiTests.CreateDocument(alice, "Frame model", "CAD", Frame, "frame.step", CadApiTests.Open);
        var res = await alice.GetAsync($"/api/v1/cad/documents/{step}/placements");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var doc = await CadApiTests.Json(res);
        Assert.Equal("frame.step", doc.GetProperty("fileName").GetString());
        Assert.Equal("STEP AP214", doc.GetProperty("sourceSystem").GetString());
        Assert.Equal(24, doc.GetProperty("instances").GetArrayLength());
        Assert.Equal("KA-3000", doc.GetProperty("parts").EnumerateArray().Single(p => p.GetProperty("ref").GetString() == doc.GetProperty("rootRef").GetString()).GetProperty("cadNumber").GetString());

        var stl = await CadApiTests.CreateDocument(alice, "Cube", "CAD", ViewerSamples.AsciiStlCube(), "cube.stl", CadApiTests.Open);
        var notStep = await alice.GetAsync($"/api/v1/cad/documents/{stl}/placements");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notStep.StatusCode);
        Assert.Equal("cad.no_placements", (await CadApiTests.Json(notStep)).GetProperty("code").GetString());

        var broken = await CadApiTests.CreateDocument(alice, "Broken", "CAD", Encoding.ASCII.GetBytes("ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\n#1=NOTHING();\nENDSEC;\nEND-ISO-10303-21;\n"), "broken.step", CadApiTests.Open);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await alice.GetAsync($"/api/v1/cad/documents/{broken}/placements")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/cad/documents/D-999999/placements")).StatusCode);
    }
}

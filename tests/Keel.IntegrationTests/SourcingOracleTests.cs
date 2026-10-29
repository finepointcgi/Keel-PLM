using Keel.Core;
using Keel.Security;
using Keel.Sourcing;

namespace Keel.IntegrationTests;

/// <summary>
/// The SQL read filters of the sourcing module agree with the in-memory access policy for every subject and every marking variant (the SQL oracle of ADR-0004):
/// what a list returns, what a count counts and what a single read allows are exactly what <see cref="AccessPolicy.Evaluate"/> permits, and nothing else leaks.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SourcingOracleTests(PostgresFixture pg)
{
    private static readonly Subject Lena = WorkflowEnv.User("lena", [Roles.Viewer], us: false);
    private static readonly Subject Farid = WorkflowEnv.User("farid", [Roles.Viewer], us: false, auths: ["TAA-0042"]);
    private static readonly Subject Erp = WorkflowEnv.User("erp", [Roles.Integration]);
    private static readonly Subject Kim = WorkflowEnv.User("kim", [Roles.Viewer, Roles.Author], programs: ["ORCA", "KRAKEN"]);

    private static Markings Make(ExportJurisdiction j, DistributionStatement d, string? usml = null, string? eccn = null, string[]? cui = null, string? program = null, string[]? auths = null) =>
        Markings.Create(j, d, "ENG", eccn, usml, cui, d == DistributionStatement.A ? null : "ACME", d == DistributionStatement.A ? null : "Reason", program, auths).Value;

    private static readonly Markings[] Variants =
    [
        SourcingEnv.Open,
        SourcingEnv.Cui(),
        SourcingEnv.Itar(),
        Make(ExportJurisdiction.Itar, DistributionStatement.D, usml: "XI(d)", cui: ["EXPT"], program: "ORCA", auths: ["TAA-0042"]),
        Make(ExportJurisdiction.Ear, DistributionStatement.C, eccn: "9A610", cui: ["EXPT"]),
        Make(ExportJurisdiction.Ear, DistributionStatement.C, eccn: "EAR99"),
        Make(ExportJurisdiction.None, DistributionStatement.B, program: "ORCA"),
        Make(ExportJurisdiction.None, DistributionStatement.B, cui: ["CTI", "EXPT"]),
    ];

    private static readonly Subject[] Subjects =
    [
        SourcingEnv.Alice, SourcingEnv.Bob, SourcingEnv.Ada, SourcingEnv.Mallory, SourcingEnv.Audrey, SourcingEnv.Eve, SourcingEnv.Vic, Lena, Farid, Erp, Kim,
        SourcingEnv.Sup1,
    ];

    [Fact]
    public async Task Supplier_and_request_lists_counts_and_single_reads_agree_with_the_policy_for_every_subject()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var openPart = await env.NewPartAsync("Washer");
        var suppliers = new Dictionary<string, Markings>();
        var requests = new Dictionary<string, Markings>();
        foreach (var m in Variants)
        {
            var s = await env.NewSupplierAsync($"Supplier {suppliers.Count}", markings: m);
            suppliers[s] = m;
            var r = await env.Requests.CreateAsync(SourcingEnv.Alice, SourcingEnv.Request($"Request {requests.Count}", m, [openPart], [await env.NewSupplierAsync($"Recipient {requests.Count}")]), null);
            Assert.True(r.IsOk, string.Join("; ", r.Errors));
            requests[r.Value!.Summary.Number] = m;
        }
        // Recipients created above are suppliers too (open): expected sets include them.
        var openSuppliers = (await env.Suppliers.SearchAsync(SourcingEnv.Alice, new SupplierSearch(Limit: 200), null)).Value!.Items.Where(i => i.Markings.Equals(SourcingEnv.Open)).Select(i => i.Number).ToList();
        foreach (var n in openSuppliers) suppliers.TryAdd(n, SourcingEnv.Open);

        foreach (var subject in Subjects)
        {
            var expectedSuppliers = suppliers.Where(kv => AccessPolicy.Evaluate(subject, kv.Value, PlmAction.Read).Permitted).Select(kv => kv.Key).Order().ToList();
            var page = (await env.Suppliers.SearchAsync(subject, new SupplierSearch(Limit: 200), null)).Value!;
            Assert.True(expectedSuppliers.SequenceEqual(page.Items.Select(i => i.Number).Order()), $"suppliers of {subject.Username}");
            Assert.Equal(expectedSuppliers.Count, page.Total);
            foreach (var (number, m) in suppliers)
            {
                var detail = await env.Suppliers.GetAsync(subject, number, null);
                Assert.Equal(AccessPolicy.Evaluate(subject, m, PlmAction.Read).Permitted, detail.IsOk);
                if (!detail.IsOk) Assert.Equal(OutcomeKind.NotFound, detail.Kind);
            }

            var expectedRequests = requests.Where(kv => AccessPolicy.Evaluate(subject, kv.Value, PlmAction.Read).Permitted).Select(kv => kv.Key).Order().ToList();
            var rpage = (await env.Requests.SearchAsync(subject, new RequestSearch(Limit: 200), null)).Value!;
            Assert.True(expectedRequests.SequenceEqual(rpage.Items.Select(i => i.Number).Order()), $"requests of {subject.Username}");
            Assert.Equal(expectedRequests.Count, rpage.Total);
            foreach (var (number, m) in requests)
                Assert.Equal(AccessPolicy.Evaluate(subject, m, PlmAction.Read).Permitted, (await env.Requests.GetAsync(subject, number, null)).IsOk);
        }
    }

    [Fact]
    public async Task Part_sourcing_where_sourced_and_coverage_agree_with_the_policy_for_every_subject()
    {
        var env = await SourcingEnv.CreateAsync(pg);
        var supplier = await env.NewSupplierAsync("Open Supplier");
        await env.ApproveSupplierAsync(supplier);
        var parts = new Dictionary<string, Markings>();
        foreach (var m in Variants)
        {
            var part = await env.NewPartAsync($"Part {parts.Count}", m);
            parts[part] = m;
            await env.ActiveEntryAsync(SourceKinds.Avl, part, supplier, EntryStatuses.Approved, null);
        }
        foreach (var subject in Subjects)
        {
            var expected = parts.Where(kv => AccessPolicy.Evaluate(subject, kv.Value, PlmAction.Read).Permitted).Select(kv => kv.Key).Order().ToList();
            var where = (await env.Sourcing.WhereSourcedAsync(subject, new WhereSourcedQuery(SupplierNumber: supplier, Limit: 200), null)).Value!;
            Assert.True(expected.SequenceEqual(where.Rows.Select(r => r.PartNumber).Order()), $"where-sourced of {subject.Username}");
            Assert.Equal(expected.Count, where.Total);
            var coverage = (await env.Sourcing.CoverageAsync(subject, parts.Keys.ToDictionary(k => k, _ => (string?)"A"), null)).Value!;
            Assert.True(expected.SequenceEqual(coverage.Keys.Order()), $"coverage of {subject.Username}");
            foreach (var (number, m) in parts)
            {
                var view = await env.Sourcing.GetPartSourcingAsync(subject, number, null);
                var readable = AccessPolicy.Evaluate(subject, m, PlmAction.Read).Permitted;
                Assert.Equal(readable, view.IsOk);
                if (!readable) Assert.Equal(OutcomeKind.NotFound, view.Kind);
            }
        }
    }
}

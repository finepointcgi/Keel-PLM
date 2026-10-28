using System.Collections.Immutable;
using Keel.Audit;
using Keel.Bom;
using Keel.Core;
using Keel.Parts;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>Shared setup for BOM tests: subjects, markings, real services over a fresh database, and part creation helpers.</summary>
internal static class BomTestKit
{
    public static Subject User(string name, bool usPerson = true, string[]? roles = null, string[]? programs = null, string[]? auths = null, string org = "ENG") => new()
    {
        Id = "sub-" + name,
        Username = name,
        DisplayName = name,
        UsPerson = usPerson,
        Org = org,
        Programs = (programs ?? ["ORCA"]).ToImmutableSortedSet(StringComparer.Ordinal),
        CuiCategories = ImmutableSortedSet.Create(StringComparer.Ordinal, "EXPT", "CTI"),
        ExportAuthorizations = (auths ?? []).ToImmutableSortedSet(StringComparer.Ordinal),
        Roles = (roles ?? [Roles.Viewer, Roles.Author]).ToImmutableSortedSet(StringComparer.Ordinal),
    };

    public static readonly Subject Alice = User("alice");
    public static readonly Subject Bob = User("bob");
    /// <summary>Non-U.S. person: cannot read ITAR or non-EAR99 EAR data.</summary>
    public static readonly Subject Lena = User("lena", usPerson: false);
    /// <summary>U.S. person without program ORCA: cannot read ORCA-program data.</summary>
    public static readonly Subject Carmen = User("carmen", programs: [], org: "MFG");
    public static readonly Subject Viewer = User("vic", roles: [Roles.Viewer]);

    public static Markings Itar(string? program = "ORCA") => Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Critical technology", program: program).Value;

    public static readonly Markings Open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;

    public static Markings Ear(string eccn = "9A610.x") => Markings.Create(ExportJurisdiction.Ear, DistributionStatement.D, "ENG", eccn: eccn,
        cuiCategories: ["EXPT"], controllingOffice: "ACME", distributionReason: "Export controlled").Value;

    public sealed record Env(PartService Parts, BomService Bom, TestDatabase Db, NpgsqlDataSource Ds)
    {
        public async Task<string> CreatePart(Subject by, string name, string type = "Component", Markings? markings = null, string unit = "EA")
        {
            var created = await Parts.CreateAsync(by, new CreatePartCommand(name, type, unit, "Make", markings ?? Open), null);
            return created.Value!.Number;
        }

        public async Task Checkout(Subject by, string number) => Assert.True((await Parts.CheckOutAsync(by, number, null)).IsOk);
        public async Task CheckIn(Subject by, string number, string? comment = null)
        {
            var r = await Parts.CheckInAsync(by, number, comment, null);
            Assert.True(r.IsOk, string.Join(" ", r.Errors));
        }

        public async Task<WorkingBomLine> Add(Subject by, string parent, string child, decimal qty = 1, string? unit = null, int? find = null, string[]? refs = null)
        {
            var r = await Bom.AddUsageAsync(by, parent, new AddUsageCommand(child, qty, unit, find, refs), null);
            Assert.True(r.IsOk, string.Join(" ", r.Errors));
            return r.Value!;
        }

        /// <summary>Creates an assembly with the given children (each child is checked in as a committed structure).</summary>
        public async Task<string> Assembly(Subject by, string name, Markings? markings = null, params (string Child, decimal Qty)[] children)
        {
            var number = await CreatePart(by, name, "Assembly", markings);
            if (children.Length == 0) return number;
            await Checkout(by, number);
            foreach (var (child, qty) in children) await Add(by, number, child, qty);
            await CheckIn(by, number, "structure");
            return number;
        }

        public async Task<List<AuditRecord>> Audit() => [.. await new AuditQueries(Ds).ListAsync(new AuditQuery(Limit: 5000))];
    }

    public static async Task<Env> Setup(PostgresFixture pg)
    {
        var db = await pg.CreateDatabaseAsync();
        var ds = db.AppDataSource();
        var log = new PostgresAuditLog(ds, TimeProvider.System);
        var parts = new PartService(ds, log, TimeProvider.System, [new BomParticipant()]);
        return new Env(parts, new BomService(ds, log, TimeProvider.System), db, ds);
    }
}

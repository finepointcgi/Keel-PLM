using Keel.Audit;
using Keel.Bom;
using Keel.Core;

namespace Keel.IntegrationTests;

/// <summary>GAP-A-2: the services behind the MBOM markings-change tab and the supply-type administration page (reason, audit, fail closed).</summary>
[Collection(PostgresCollection.Name)]
public sealed class MbomAdminTests(PostgresFixture pg)
{
    private static Markings WithCti() => Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)",
        cuiCategories: ["EXPT", "CTI"], controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    private async Task<(MbomEnv Env, string Part, string Mbom)> OpenMbomAsync()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var part = await env.C.NewPartAsync("Pump assembly", "Assembly");
        var created = await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(part, MbomEnv.Plant), "c");
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        return (env, part, created.Value!.Summary.Number);
    }

    // ---------------------------------------------------------------- markings change

    [Fact]
    public async Task Changing_the_markings_of_an_mbom_needs_a_reason_and_is_audited_with_before_and_after()
    {
        var (env, _, mbom) = await OpenMbomAsync();
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.ChangeMarkingsAsync(MbomEnv.Alice, mbom, ConfigEnv.Itar(), "", "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.ChangeMarkingsAsync(MbomEnv.Alice, mbom, ConfigEnv.Itar(), "   ", "c")).Kind);
        Assert.Equal(ConfigEnv.Open.BannerText(), (await env.GetAsync(mbom)).Summary.Markings.BannerText());

        var changed = await env.Mbom.ChangeMarkingsAsync(MbomEnv.Alice, mbom, WithCti(), "Contains export-controlled process data", "corr-mk");
        Assert.True(changed.IsOk, string.Join("; ", changed.Errors));
        Assert.Contains("CTI", changed.Value!.Summary.Banner, StringComparison.Ordinal);
        Assert.Equal(WithCti().BannerText(), changed.Value.Summary.Markings.BannerText());

        var e = Assert.Single(await env.AuditAsync(), a => a.Action == "mbom.markings_change" && a.Outcome == AuditOutcome.Success);
        Assert.Equal((mbom, "corr-mk"), (e.ObjectId, e.CorrelationId));
        Assert.Contains("Contains export-controlled process data", e.Payload, StringComparison.Ordinal);
        Assert.Contains("before", e.Payload, StringComparison.Ordinal);
        Assert.Contains("after", e.Payload, StringComparison.Ordinal);

        // Nothing changes when the markings are the same.
        Assert.Equal(OutcomeKind.Conflict, (await env.Mbom.ChangeMarkingsAsync(MbomEnv.Alice, mbom, WithCti(), "again", "c")).Kind);
    }

    [Fact]
    public async Task The_markings_of_an_mbom_can_never_be_less_restrictive_than_its_part_and_unauthorized_callers_learn_nothing()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var part = await env.C.NewPartAsync("Valve assembly", "Assembly", ConfigEnv.Itar());
        var created = await env.Mbom.CreateAsync(MbomEnv.Alice, new CreateMbomCommand(part, MbomEnv.Plant), "c");
        var mbom = created.Value!.Summary.Number;

        var lower = await env.Mbom.ChangeMarkingsAsync(MbomEnv.Alice, mbom, ConfigEnv.Open, "Lower them", "c");
        Assert.Equal(OutcomeKind.Invalid, lower.Kind);
        Assert.Contains(part, lower.Errors[0], StringComparison.Ordinal);

        // A caller who cannot read the MBOM gets NotFound and a denial in the audit log; a viewer who can read it may not change it.
        Assert.Equal(OutcomeKind.NotFound, (await env.Mbom.ChangeMarkingsAsync(MbomEnv.Eve, mbom, WithCti(), "x", "c")).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await env.Mbom.ChangeMarkingsAsync(ConfigEnv.Mallory, mbom, WithCti(), "x", "c")).Kind);
        var audit = await env.AuditAsync();
        Assert.Contains(audit, a => a.Action == "mbom.markings_change" && a.Outcome == AuditOutcome.Denied && a.Actor == MbomEnv.Eve.Id);
        Assert.DoesNotContain(audit, a => a.Action == "mbom.markings_change" && a.Outcome == AuditOutcome.Success);
        Assert.Equal(ConfigEnv.Itar().BannerText(), (await env.GetAsync(mbom)).Summary.Markings.BannerText());
    }

    [Fact]
    public async Task A_released_mbom_cannot_have_its_markings_changed()
    {
        var env = await MbomEnv.CreateAsync(pg);
        var (_, top) = await env.ReconciledProductAsync();
        await env.ReleaseAsync(top);
        var blocked = await env.Mbom.ChangeMarkingsAsync(MbomEnv.Alice, top, WithCti(), "Too late", "c");
        Assert.Equal(OutcomeKind.Conflict, blocked.Kind);
        Assert.Equal(ConfigEnv.Open.BannerText(), (await env.GetAsync(top)).Summary.Markings.BannerText());
    }

    [Fact]
    public async Task Raising_the_markings_can_not_lock_the_author_out()
    {
        var (env, _, mbom) = await OpenMbomAsync();
        var lockout = await env.Mbom.ChangeMarkingsAsync(MbomEnv.Alice, mbom, ConfigEnv.Itar("KRAKEN"), "Program transfer", "c");
        Assert.Equal(OutcomeKind.Invalid, lockout.Kind);
        Assert.Equal(ConfigEnv.Open.BannerText(), (await env.GetAsync(mbom)).Summary.Markings.BannerText());
    }

    // ---------------------------------------------------------------- supply types

    [Fact]
    public async Task Supply_types_are_administrator_data_and_every_change_is_audited()
    {
        var env = await MbomEnv.CreateAsync(pg);
        Assert.Equal(["Buy", "Consume", "Make"], (await env.Mbom.ListSupplyTypesAsync()).Where(t => t.IsSystem).Select(t => t.Code).Order(StringComparer.Ordinal));

        var denied = await env.Mbom.SaveSupplyTypeAsync(ConfigEnv.Alice, new SaveSupplyTypeCommand("Kit", "Kitted"), "c");
        Assert.Equal(OutcomeKind.Forbidden, denied.Kind);
        Assert.DoesNotContain(await env.Mbom.ListSupplyTypesAsync(true), t => t.Code == "Kit");

        var created = await env.Mbom.SaveSupplyTypeAsync(ConfigEnv.Ada, new SaveSupplyTypeCommand("Kit", "Kitted", true, 70), "corr-st");
        Assert.True(created.IsOk, string.Join("; ", created.Errors));
        Assert.Equal(("Kit", "Kitted", true, 70, false), (created.Value!.Code, created.Value.Label, created.Value.IsActive, created.Value.SortOrder, created.Value.IsSystem));

        // A label change needs no reason; retiring and restoring do, and nothing changes without one.
        Assert.True((await env.Mbom.SaveSupplyTypeAsync(ConfigEnv.Ada, new SaveSupplyTypeCommand("Kit", "Kitted from stock", true, 70), "c")).IsOk);
        var noReason = await env.Mbom.SaveSupplyTypeAsync(ConfigEnv.Ada, new SaveSupplyTypeCommand("Kit", "Kitted from stock", false, 70), "c");
        Assert.Equal(OutcomeKind.Invalid, noReason.Kind);
        Assert.Contains("reason", noReason.Errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.True((await env.Mbom.ListSupplyTypesAsync()).Single(t => t.Code == "Kit").IsActive);
        var retired = await env.Mbom.SaveSupplyTypeAsync(ConfigEnv.Ada, new SaveSupplyTypeCommand("Kit", "Kitted from stock", false, 70, "Replaced by Buy"), "c");
        Assert.False(retired.Value!.IsActive);
        Assert.DoesNotContain(await env.Mbom.ListSupplyTypesAsync(), t => t.Code == "Kit");
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.SaveSupplyTypeAsync(ConfigEnv.Ada, new SaveSupplyTypeCommand("Kit", "Kitted from stock", true, 70), "c")).Kind);
        Assert.True((await env.Mbom.SaveSupplyTypeAsync(ConfigEnv.Ada, new SaveSupplyTypeCommand("Kit", "Kitted from stock", true, 70, "Needed again"), "c")).Value!.IsActive);

        // The built-in types are never retired, with or without a reason.
        var system = await env.Mbom.SaveSupplyTypeAsync(ConfigEnv.Ada, new SaveSupplyTypeCommand("Make", "Make", false, 10, "No longer needed"), "c");
        Assert.Equal(OutcomeKind.Invalid, system.Kind);
        Assert.Equal(OutcomeKind.Invalid, (await env.Mbom.SaveSupplyTypeAsync(ConfigEnv.Ada, new SaveSupplyTypeCommand("bad code!", "x"), "c")).Kind);

        var audit = (await env.AuditAsync()).Where(a => a.Action == "mbom.supply_type_save").ToList();
        Assert.Contains(audit, a => a.Outcome == AuditOutcome.Denied && a.Actor == ConfigEnv.Alice.Id);
        var retire = Assert.Single(audit, a => a.Outcome == AuditOutcome.Success && a.Payload.Contains("Replaced by Buy", StringComparison.Ordinal));
        Assert.Contains("activeBefore", retire.Payload, StringComparison.Ordinal);
        Assert.Contains("Kitted from stock", retire.Payload, StringComparison.Ordinal);
        Assert.Contains(audit, a => a.Payload.Contains("Needed again", StringComparison.Ordinal));
    }
}

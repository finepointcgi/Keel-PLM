using System.Text.Json;
using Keel.Audit;
using Keel.Core;
using Keel.Programs;
using Keel.Security;
using Npgsql;

namespace Keel.IntegrationTests;

/// <summary>
/// Resources, load and cost of programs (9.5, ADR-0049) over a real database: pools, roles, resources, assignments, over-allocation, rates over time, budget against actual against estimate to
/// complete, the program cost permission, immutability of recorded costs, audit without amounts, and the SQL oracle over probe users (every number agrees with the in-memory policy).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProgramResourceTests(PostgresFixture pg)
{
    /// <summary>A holder of the program cost permission who is a member of ORCA and may modify.</summary>
    private static readonly Subject Carla = WorkflowEnv.User("carla", [Roles.Viewer, Roles.Author, Roles.ProgramCost]);

    private static Subject WithCost(Subject s) => s with { Roles = s.Roles.Add(Roles.ProgramCost) };

    private static readonly DateOnly Mon = new(2027, 3, 1);
    private static DateOnly D(int days) => Mon.AddDays(days);

    private sealed record Seed(ProgramEnv Env, string Pool, string Role, string Res1, string Res2, string Task1, string Task2);

    private async Task<Seed> SeedAsync(bool rate = true)
    {
        var env = await ProgramEnv.CreateAsync(pg);
        await env.NewProgramAsync(ProgramEnv.Alice);
        var pool = await env.P.AddPoolAsync(ProgramEnv.Alice, "ORCA", new AddPoolCommand("Mechanical engineering", "Synthetic pool", null), "c");
        Assert.True(pool.IsOk, string.Join("; ", pool.Errors));
        var role = await env.P.AddRoleAsync(Carla, "ORCA", new AddRoleCommand(pool.Value!.Key, "Design engineer", rate ? 100m : null, null, null), "c");
        Assert.True(role.IsOk, string.Join("; ", role.Errors));
        var r1 = await env.P.AddResourceAsync(ProgramEnv.Alice, "ORCA", new AddResourceCommand(role.Value!.Key, "Crew A", 8, null, null, null), "c");
        var r2 = await env.P.AddResourceAsync(ProgramEnv.Alice, "ORCA", new AddResourceCommand(role.Value.Key, "Crew B", 8, null, null, null), "c");
        Assert.True(r1.IsOk && r2.IsOk);
        var t1 = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Task", "Concept design", 0, 11);
        var t2 = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Task", "Detail design", 7, 18);
        return new Seed(env, pool.Value.Key, role.Value.Key, r1.Value!.Key, r2.Value!.Key, t1.Key, t2.Key);
    }

    private static async Task<AssignmentRow> AssignAsync(ProgramEnv env, Subject by, string task, string resource, int start, int end, decimal hours)
    {
        var r = await env.P.AddAssignmentAsync(by, "ORCA", new AddAssignmentCommand(task, resource, D(start), D(end), hours), "c");
        Assert.True(r.IsOk, $"{r.Kind}: {string.Join("; ", r.Errors)}");
        return r.Value!;
    }

    [Fact]
    public async Task Pools_roles_resources_and_assignments_get_keys_and_the_programs_markings_and_a_closed_program_takes_no_changes()
    {
        var s = await SeedAsync();
        Assert.Equal(["RP-001", "RR-001", "RS-001", "RS-002"], [s.Pool, s.Role, s.Res1, s.Res2]);
        var a = await AssignAsync(s.Env, ProgramEnv.Alice, s.Task1, s.Res1, 0, 11, 6);
        Assert.Equal("AS-001", a.Key);
        Assert.Equal(60m, a.TotalHours);
        Assert.Equal(ProgramEnv.Prg().BannerText(), a.Markings.BannerText());
        var catalog = (await s.Env.P.GetResourcesAsync(ProgramEnv.Alice, "ORCA", "c")).Value!;
        Assert.Equal((1, 1, 2, 1), (catalog.Pools.Count, catalog.Roles.Count, catalog.Resources.Count, catalog.Assignments.Count));
        Assert.Equal(2, catalog.Pools.Single().ResourceCount);

        // names are unique within the program; assigning to a milestone or a missing task is refused with a reason
        Assert.Equal(OutcomeKind.Conflict, (await s.Env.P.AddResourceAsync(ProgramEnv.Alice, "ORCA", new AddResourceCommand(s.Role, "crew a", 8, null, null, null), "c")).Kind);
        var ms = await s.Env.AddAsync(ProgramEnv.Alice, "ORCA", "Milestone", "Review", 20, 20);
        Assert.Equal(OutcomeKind.Invalid, (await s.Env.P.AddAssignmentAsync(ProgramEnv.Alice, "ORCA", new AddAssignmentCommand(ms.Key, s.Res1, D(20), D(20), 4), "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await s.Env.P.AddAssignmentAsync(ProgramEnv.Alice, "ORCA", new AddAssignmentCommand("TK-999", s.Res1, D(0), D(1), 4), "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await s.Env.P.AddAssignmentAsync(ProgramEnv.Alice, "ORCA", new AddAssignmentCommand(s.Task1, s.Res1, D(5), D(1), 4), "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await s.Env.P.AddAssignmentAsync(ProgramEnv.Alice, "ORCA", new AddAssignmentCommand(s.Task1, s.Res1, D(0), D(1), 25), "c")).Kind);
        // a viewer may read but not change
        Assert.Equal(OutcomeKind.Forbidden, (await s.Env.P.AddPoolAsync(ProgramEnv.Mallory, "ORCA", new AddPoolCommand("Nope", null, null), "c")).Kind);
        // update and remove
        var u = await s.Env.P.UpdateAssignmentAsync(ProgramEnv.Alice, "ORCA", a.Key, new UpdateAssignmentCommand(D(0), D(4), 8), "c");
        Assert.Equal(40m, u.Value!.TotalHours);
        Assert.True((await s.Env.P.RemoveAssignmentAsync(ProgramEnv.Alice, "ORCA", a.Key, "c")).IsOk);
        Assert.Empty((await s.Env.P.GetResourcesAsync(ProgramEnv.Alice, "ORCA", "c")).Value!.Assignments);
        Assert.Equal(OutcomeKind.NotFound, (await s.Env.P.RemoveAssignmentAsync(ProgramEnv.Alice, "ORCA", a.Key, "c")).Kind);

        var closed = await s.Env.P.GetAsync(ProgramEnv.Alice, "ORCA", "c");
        var up = await s.Env.P.UpdateAsync(ProgramEnv.Alice, "ORCA", new UpdateProgramCommand(closed.Value!.Name, null, null, null, "Complete", "alice", closed.Value.Start, closed.Value.End, D(300)), "c");
        Assert.True(up.IsOk, string.Join("; ", up.Errors));
        Assert.Equal(OutcomeKind.Conflict, (await s.Env.P.AddPoolAsync(ProgramEnv.Alice, "ORCA", new AddPoolCommand("Late pool", null, null), "c")).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await s.Env.P.SetBudgetAsync(Carla, "ORCA", new SetBudgetCommand(CostTypes.Labor, D(0), 1m), "c")).Kind);
    }

    [Fact]
    public async Task Over_allocation_is_found_per_resource_and_period_and_rows_and_weeks_are_computed_over_working_days()
    {
        var s = await SeedAsync();
        await AssignAsync(s.Env, ProgramEnv.Alice, s.Task1, s.Res1, 0, 11, 6);
        await AssignAsync(s.Env, ProgramEnv.Alice, s.Task2, s.Res1, 7, 18, 4); // second week: 10 hours a day on Res1
        await AssignAsync(s.Env, ProgramEnv.Alice, s.Task2, s.Res2, 7, 18, 8); // exactly full: not over
        var load = (await s.Env.P.GetLoadAsync(ProgramEnv.Alice, "ORCA", null, null, "c")).Value!;
        var p = Assert.Single(load.OverAllocations);
        Assert.Equal((s.Res1, D(7), D(11), 10m, 8m, 10m), (p.ResourceKey, p.From, p.To, p.PeakHours, p.CapacityHoursPerDay, p.ExcessHours));
        Assert.Equal(["AS-001", "AS-002"], p.AssignmentKeys);
        Assert.Equal(D(0), load.From);
        var row = load.Rows.Single(r => r.ResourceKey == s.Res1);
        Assert.Equal([30m, 50m, 20m], row.Weeks.Select(w => w.Allocated));
        Assert.Equal([40m, 40m, 40m], row.Weeks.Select(w => w.Capacity));
        Assert.Equal([false, true, false], row.Weeks.Select(w => w.Over));
        // a viewer in the program sees hours and the over-allocation, never a rate
        var viewer = (await s.Env.P.GetLoadAsync(ProgramEnv.Mallory, "ORCA", null, null, "c")).Value!;
        Assert.Single(viewer.OverAllocations);
        // an outsider gets 404, a window is clamped to the maximum number of weeks
        Assert.Equal(OutcomeKind.NotFound, (await s.Env.P.GetLoadAsync(ProgramEnv.Eve, "ORCA", null, null, "c")).Kind);
        var wide = (await s.Env.P.GetLoadAsync(ProgramEnv.Alice, "ORCA", D(0), D(5000), "c")).Value!;
        Assert.Equal(ProgramService.MaxLoadWeeks, wide.Weeks.Count);
    }

    [Fact]
    public async Task Rates_and_every_amount_need_the_cost_permission_and_the_refusals_are_audited()
    {
        var s = await SeedAsync();
        var alice = ProgramEnv.Alice;
        // without the permission: no rate in the catalog, no cost view, no budget, no actual, no rate, no priced role
        var plain = (await s.Env.P.GetResourcesAsync(alice, "ORCA", "c")).Value!;
        Assert.False(plain.CanViewCost);
        Assert.True(plain.Roles.Single().RateHidden);
        Assert.Null(plain.Roles.Single().CurrentRate);
        Assert.Empty(plain.Roles.Single().Rates);
        var cost = await s.Env.P.GetCostAsync(alice, "ORCA", null, "c");
        Assert.Equal(OutcomeKind.Forbidden, cost.Kind);
        Assert.Equal([ProgramService.CostRoleMissing], cost.Errors);
        Assert.Equal(OutcomeKind.Forbidden, (await s.Env.P.SetBudgetAsync(alice, "ORCA", new SetBudgetCommand(CostTypes.Labor, D(0), 100m), "c")).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await s.Env.P.AddActualAsync(alice, "ORCA", new AddActualCommand(CostTypes.Other, D(0), 10m, null, null, null, null, null), "c")).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await s.Env.P.SetRoleRateAsync(alice, "ORCA", s.Role, new SetRoleRateCommand(50m, D(0)), "c")).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await s.Env.P.AddRoleAsync(alice, "ORCA", new AddRoleCommand(s.Pool, "Priced", 10m, null, null), "c")).Kind);
        Assert.Equal(OutcomeKind.Forbidden, (await s.Env.P.AddMaterialLineAsync(alice, "ORCA", new AddMaterialLineCommand("P-000001", 1, D(5), null), "c")).Kind);
        // the program's own manager role (author) and even an administrator do not hold it
        Assert.Equal(OutcomeKind.Forbidden, (await s.Env.P.GetCostAsync(ProgramEnv.Ada, "ORCA", null, "c")).Kind);
        // the holder sees the rate; an outsider with the permission still gets 404
        var rated = (await s.Env.P.GetResourcesAsync(Carla, "ORCA", "c")).Value!;
        Assert.Equal(100m, rated.Roles.Single().CurrentRate);
        Assert.Equal(OutcomeKind.NotFound, (await s.Env.P.GetCostAsync(WithCost(ProgramEnv.Eve), "ORCA", null, "c")).Kind);
        // the refusals are in the audit log with the reason code, and nothing carries an amount
        var audit = await s.Env.AuditAsync();
        Assert.True(audit.Count(a => a.Action == "program.cost" && a.Outcome == AuditOutcome.Denied) >= 2);
        Assert.Contains(audit, a => a.Action == "program.budget_set" && a.Outcome == AuditOutcome.Denied && a.Payload.Contains(ProgramService.CostRoleMissing, StringComparison.Ordinal));
        Assert.DoesNotContain(audit, a => a.Payload.Contains("100.00", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Budget_actual_and_estimate_to_complete_roll_up_with_rates_that_change_over_time()
    {
        var s = await SeedAsync();
        var task = s.Task1;
        await AssignAsync(s.Env, ProgramEnv.Alice, task, s.Res1, 0, 11, 8); // 80 hours over two weeks of March 2027
        var raise = await s.Env.P.SetRoleRateAsync(Carla, "ORCA", s.Role, new SetRoleRateCommand(200m, D(7)), "c"); // from the second week: 200 an hour
        Assert.True(raise.IsOk);
        Assert.Equal(OutcomeKind.Conflict, (await s.Env.P.SetRoleRateAsync(Carla, "ORCA", s.Role, new SetRoleRateCommand(250m, D(7)), "c")).Kind); // rates are never changed
        Assert.True((await s.Env.P.SetBudgetAsync(Carla, "ORCA", new SetBudgetCommand(CostTypes.Labor, D(3), 20000m), "c")).IsOk);
        Assert.True((await s.Env.P.SetBudgetAsync(Carla, "ORCA", new SetBudgetCommand(CostTypes.Other, D(10), 1000m), "c")).IsOk);
        var changed = await s.Env.P.SetBudgetAsync(Carla, "ORCA", new SetBudgetCommand(CostTypes.Labor, D(20), 22000m), "c"); // any day names the month: the same line
        Assert.Equal("BL-001", changed.Value!.Key);
        Assert.Equal(OutcomeKind.Conflict, (await s.Env.P.SetBudgetAsync(Carla, "ORCA", new SetBudgetCommand(CostTypes.Labor, D(20), 22000m), "c")).Kind); // no change
        Assert.Equal(OutcomeKind.Invalid, (await s.Env.P.SetBudgetAsync(Carla, "ORCA", new SetBudgetCommand("Travel", D(20), 1m), "c")).Kind);
        Assert.Equal("2", await s.Env.TextAsync("SELECT count(*) FROM prg.budget_log WHERE line_id = (SELECT id FROM prg.budget_line WHERE key = 'BL-001')"));

        // labor actual: 10 hours in the first week at 100; an hour entry before any rate would be refused (none: the rate starts at the epoch)
        var labor = await s.Env.P.AddActualAsync(Carla, "ORCA", new AddActualCommand(CostTypes.Labor, D(2), null, 10m, s.Res1, task, "Week one", null), "c");
        Assert.True(labor.IsOk, string.Join("; ", labor.Errors));
        Assert.Equal(1000m, labor.Value!.Amount);
        var second = await s.Env.P.AddActualAsync(Carla, "ORCA", new AddActualCommand(CostTypes.Labor, D(8), null, 5m, s.Res1, null, null, null), "c");
        Assert.Equal(1000m, second.Value!.Amount); // 5 hours at 200
        var other = await s.Env.P.AddActualAsync(Carla, "ORCA", new AddActualCommand(CostTypes.Other, D(10), 400m, null, null, null, "Test fixture", null), "c");
        Assert.True(other.IsOk);
        Assert.Equal(OutcomeKind.Invalid, (await s.Env.P.AddActualAsync(Carla, "ORCA", new AddActualCommand(CostTypes.Labor, D(2), null, null, s.Res1, null, null, null), "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await s.Env.P.AddActualAsync(Carla, "ORCA", new AddActualCommand(CostTypes.Other, D(2), 0m, null, null, null, null, null), "c")).Kind);
        // the correction of a mistake is a reversal, once
        var reversal = await s.Env.P.AddActualAsync(Carla, "ORCA", new AddActualCommand(CostTypes.Other, D(11), null, null, null, null, "Booked twice", other.Value!.Key), "c");
        Assert.Equal(-400m, reversal.Value!.Amount);
        Assert.Equal(other.Value.Key, reversal.Value.ReversesKey);
        Assert.Equal(OutcomeKind.Conflict, (await s.Env.P.AddActualAsync(Carla, "ORCA", new AddActualCommand(CostTypes.Other, D(11), null, null, null, null, null, other.Value.Key), "c")).Kind);
        Assert.Equal(OutcomeKind.Invalid, (await s.Env.P.AddActualAsync(Carla, "ORCA", new AddActualCommand(CostTypes.Other, D(11), null, null, null, null, null, reversal.Value.Key), "c")).Kind);

        // 50 percent done at the end of week one: half of the 80 hours remain, spread over the five working days of week two at 200 an hour
        var p = await s.Env.P.SetProgressAsync(ProgramEnv.Alice, "ORCA", task, new SetProgressCommand("InProgress", 50, D(0), null), "c");
        Assert.True(p.IsOk, string.Join("; ", p.Errors));
        var view = (await s.Env.P.GetCostAsync(Carla, "ORCA", D(4), "c")).Value!;
        var labor2 = view.ByType[CostTypes.Labor];
        Assert.Equal(22000m, labor2.Budget);
        Assert.Equal(8m * 5 * 100m + 8m * 5 * 200m, labor2.Planned);
        Assert.Equal(2000m, labor2.Actual);
        Assert.Equal(40m * 200m, labor2.Etc);
        Assert.Equal(10000m, labor2.Eac);
        Assert.Equal(12000m, labor2.Variance);
        var other2 = view.ByType[CostTypes.Other];
        Assert.Equal((1000m, 0m, 0m, 0m), (other2.Budget, other2.Planned, other2.Actual, other2.Etc)); // booked and reversed
        Assert.Equal(23000m, view.Total.Budget);
        Assert.Equal(view.Total.Actual + view.Total.Etc, view.Total.Eac);
        Assert.False(view.OverBudget);
        Assert.Equal(0m, view.UnpricedHours);
        Assert.Equal(0, view.ResourcesWithoutRate);
        Assert.Single(view.Roles);
        Assert.Equal(80m, view.Roles.Single().Hours);
        Assert.Equal(40m, view.Roles.Single().RemainingHours);
        Assert.Equal("USD", view.Currency);
        Assert.Equal(4, view.Actuals.Count);

        // recorded costs, rates and the budget log are immutable, even for the application role
        await Assert.ThrowsAsync<PostgresException>(() => s.Env.AppSqlAsync("UPDATE prg.actual_cost SET amount = 1"));
        await Assert.ThrowsAsync<PostgresException>(() => s.Env.AppSqlAsync("DELETE FROM prg.actual_cost"));
        await Assert.ThrowsAsync<PostgresException>(() => s.Env.AppSqlAsync("UPDATE prg.role_rate SET hourly_rate = 1"));
        await Assert.ThrowsAsync<PostgresException>(() => s.Env.AppSqlAsync("DELETE FROM prg.budget_log"));
        await Assert.ThrowsAsync<PostgresException>(() => s.Env.AppSqlAsync("TRUNCATE prg.actual_cost"));
        // a reversal that is not the exact opposite of an entry of the same program is refused by the database
        await Assert.ThrowsAsync<PostgresException>(() => s.Env.AppSqlAsync(
            "INSERT INTO prg.actual_cost (id, program_id, key, cost_type, incurred_on, amount, reverses_id, export_jurisdiction, cui_categories, distribution_statement, program, owning_org, export_authorizations, created_at, created_by) " +
            "SELECT gen_random_uuid(), program_id, 'AC-9999', cost_type, incurred_on, 1, id, export_jurisdiction, cui_categories, distribution_statement, program, owning_org, export_authorizations, now(), 'x' FROM prg.actual_cost WHERE key = 'AC-0001'"));

        // the audit log names keys, types and counts and never an amount
        var audit = await s.Env.AuditAsync();
        var money = audit.Where(a => a.Action is "program.budget_set" or "program.actual_add" or "program.role_rate_set" or "program.cost").ToList();
        Assert.True(money.Count >= 8);
        foreach (var a in money)
        {
            // a timestamp inside the payload can contain digit runs such as "400"; the amounts are looked for in the details only
            var details = System.Text.RegularExpressions.Regex.Replace(a.Payload, @"\d{4}-\d{2}-\d{2}T[\d:.]+Z", "").Replace("1000-", "", StringComparison.Ordinal);
            foreach (var amount in new[] { "22000", "20000", "1000", "400", "200" }) Assert.DoesNotContain(amount, details, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Hours_without_a_rate_are_counted_not_priced_and_a_labor_entry_needs_a_rate()
    {
        var s = await SeedAsync(rate: false);
        await AssignAsync(s.Env, ProgramEnv.Alice, s.Task1, s.Res1, 0, 4, 8);
        var view = (await s.Env.P.GetCostAsync(Carla, "ORCA", D(0), "c")).Value!;
        Assert.Equal(40m, view.UnpricedHours);
        Assert.Equal(2, view.ResourcesWithoutRate); // both resources of the unpriced role
        Assert.Equal(0m, view.ByType[CostTypes.Labor].Planned);
        var refused = await s.Env.P.AddActualAsync(Carla, "ORCA", new AddActualCommand(CostTypes.Labor, D(1), null, 8m, s.Res1, null, null, null), "c");
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("no rate", refused.Errors.Single(), StringComparison.Ordinal);
        Assert.True((await s.Env.P.SetRoleRateAsync(Carla, "ORCA", s.Role, new SetRoleRateCommand(50m, D(0)), "c")).IsOk);
        Assert.Equal(40m * 50m, (await s.Env.P.GetCostAsync(Carla, "ORCA", D(0), "c")).Value!.ByType[CostTypes.Labor].Planned);
    }

    [Fact]
    public async Task Material_lines_need_a_material_source_and_never_store_a_price()
    {
        var s = await SeedAsync();
        // the service of this environment has no BOM and price lists wired in, so a material line is refused instead of being faked
        var refused = await s.Env.P.AddMaterialLineAsync(Carla, "ORCA", new AddMaterialLineCommand("P-000001", 2, D(30), null), "c");
        Assert.Equal(OutcomeKind.Invalid, refused.Kind);
        Assert.Contains("No material cost source", refused.Errors.Single(), StringComparison.Ordinal);
        var columns = await s.Env.TextAsync("SELECT string_agg(column_name, ',') FROM information_schema.columns WHERE table_schema = 'prg' AND table_name = 'material_line'");
        Assert.DoesNotContain("price", columns, StringComparison.Ordinal);
        Assert.DoesNotContain("cost", columns, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Costs_are_still_booked_and_reversed_on_a_closed_program_and_a_reversal_carries_the_markings_of_its_entry()
    {
        var s = await SeedAsync();
        var itar = await s.Env.P.AddResourceAsync(ProgramEnv.Alice, "ORCA", new AddResourceCommand(s.Role, "Itar crew", 8, null, null, ProgramEnv.Itar()), "c");
        Assert.True(itar.IsOk, string.Join("; ", itar.Errors));
        var booked = await s.Env.P.AddActualAsync(Carla, "ORCA", new AddActualCommand(CostTypes.Labor, D(2), null, 10m, itar.Value!.Key, null, null, null), "c");
        Assert.True(booked.IsOk, string.Join("; ", booked.Errors));
        var program = (await s.Env.P.GetAsync(ProgramEnv.Alice, "ORCA", "c")).Value!;
        Assert.True((await s.Env.P.UpdateAsync(ProgramEnv.Alice, "ORCA", new UpdateProgramCommand(program.Name, null, null, null, "Complete", "alice", program.Start, program.End, D(300)), "c")).IsOk);

        // a closed program takes a late invoice and the correction of an entry, and nothing else of the cost plan
        Assert.True((await s.Env.P.AddActualAsync(Carla, "ORCA", new AddActualCommand(CostTypes.Other, D(400), 75m, null, null, null, "Final invoice", null), "c")).IsOk);
        var reversal = await s.Env.P.AddActualAsync(Carla, "ORCA", new AddActualCommand(CostTypes.Labor, D(500), null, null, null, null, "Wrong resource", booked.Value!.Key), "c");
        Assert.True(reversal.IsOk, string.Join("; ", reversal.Errors));
        Assert.Equal(D(500), reversal.Value!.IncurredOn); // booked on the date given (the screen offers the date of the entry)
        Assert.Equal(ProgramEnv.Itar().BannerText(), reversal.Value.Markings.BannerText());
        Assert.Equal(booked.Value.Markings.BannerText(), reversal.Value.Markings.BannerText());
        Assert.Equal(OutcomeKind.Conflict, (await s.Env.P.SetBudgetAsync(Carla, "ORCA", new SetBudgetCommand(CostTypes.Labor, D(0), 1m), "c")).Kind);
        Assert.Equal(OutcomeKind.Conflict, (await s.Env.P.AddMaterialLineAsync(Carla, "ORCA", new AddMaterialLineCommand("P-000001", 1, D(5), null), "c")).Kind);

        // the entry and its reversal are both ITAR: a reader who cannot read ITAR sees neither, so their sums never disagree
        var open = (await s.Env.P.GetCostAsync(WithCost(WorkflowEnv.Lena), "ORCA", D(0), "c")).Value!;
        Assert.Equal(75m, open.Total.Actual);
        Assert.DoesNotContain(open.Actuals, a => a.ResourceKey is not null || a.ReversesKey is not null);
        var full = (await s.Env.P.GetCostAsync(Carla, "ORCA", D(0), "c")).Value!;
        Assert.Equal(75m, full.Total.Actual); // 1000 booked and reversed, 75 invoiced
        Assert.Equal(3, full.Actuals.Count);
    }

    // ---------------------------------------------------------------- the SQL oracle

    private static readonly (string Name, Subject Who)[] Probes =
    [
        ("alice (author, ORCA)", ProgramEnv.Alice), ("eve (no program)", ProgramEnv.Eve), ("kim (ORCA and KRAKEN)", ProgramEnv.Kim), ("mallory (viewer)", ProgramEnv.Mallory),
        ("lena (foreign, no authorization)", WorkflowEnv.Lena), ("farid (foreign, TAA-0042)", WorkflowEnv.Farid), ("audrey (auditor, no program)", ProgramEnv.Audrey),
        ("ada (administrator)", ProgramEnv.Ada), ("a US user without CUI clearance", WorkflowEnv.User("nocui", [Roles.Viewer], cui: [])),
    ];

    private static Markings Ear99() => Markings.Create(ExportJurisdiction.Ear, DistributionStatement.B, "ENG", eccn: Markings.Ear99, controllingOffice: "ACME", distributionReason: "Administrative", program: "ORCA").Value;

    private static int WorkingDays(DateOnly from, DateOnly to)
    {
        var n = 0;
        for (var d = from; d <= to; d = d.AddDays(1)) if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) n++;
        return n;
    }

    [Fact]
    public async Task Catalog_load_and_cost_agree_with_the_policy_for_every_probe_user_and_reveal_nothing_unreadable()
    {
        var env = await ProgramEnv.CreateAsync(pg);
        await env.NewProgramAsync(ProgramEnv.Alice);
        var pool = (await env.P.AddPoolAsync(ProgramEnv.Alice, "ORCA", new AddPoolCommand("Pool secret-pool", null, null), "c")).Value!;
        var role = (await env.P.AddRoleAsync(Carla, "ORCA", new AddRoleCommand(pool.Key, "Role secret-role", 100m, null, null), "c")).Value!;
        var resOpen = (await env.P.AddResourceAsync(ProgramEnv.Alice, "ORCA", new AddResourceCommand(role.Key, "Open crew", 8, null, null, null), "c")).Value!;
        var resItar = (await env.P.AddResourceAsync(ProgramEnv.Alice, "ORCA", new AddResourceCommand(role.Key, "Itar crew secret-resource", 8, null, null, ProgramEnv.Itar()), "c")).Value!;
        var variants = new[] { ProgramEnv.Prg(), ProgramEnv.Itar(), Ear99(), ProgramEnv.Prg(), ProgramEnv.Itar(), ProgramEnv.Prg() };
        var planned = new List<(string Key, Markings M, int Hours)>();
        for (var i = 0; i < variants.Length; i++)
        {
            var item = await env.AddAsync(ProgramEnv.Alice, "ORCA", "Task", $"Task {i} secret-title-{i}", i * 7, i * 7 + 4, markings: variants[i]);
            var resource = i % 2 == 0 ? resOpen : resItar;
            var r = await env.P.AddAssignmentAsync(ProgramEnv.Alice, "ORCA", new AddAssignmentCommand(item.Key, resource.Key, ProgramEnv.D(i * 7), ProgramEnv.D(i * 7 + 4), 8), "c");
            Assert.True(r.IsOk, $"{i}: {r.Kind} {string.Join("; ", r.Errors)}");
            planned.Add((r.Value!.Key, r.Value.Markings, 40));
        }
        // budget and actuals, each with the program's markings plus one actual tied to the ITAR resource (so its markings are ITAR)
        Assert.True((await env.P.SetBudgetAsync(Carla, "ORCA", new SetBudgetCommand(CostTypes.Other, ProgramEnv.D(0), 5000m), "c")).IsOk);
        Assert.True((await env.P.AddActualAsync(Carla, "ORCA", new AddActualCommand(CostTypes.Other, ProgramEnv.D(1), 300m, null, null, null, null, null), "c")).IsOk);
        var itarActual = await env.P.AddActualAsync(Carla, "ORCA", new AddActualCommand(CostTypes.Labor, ProgramEnv.D(1), null, 10m, resItar.Key, null, null, null), "c");
        Assert.True(itarActual.IsOk, string.Join("; ", itarActual.Errors));

        foreach (var (name, who) in Probes)
        {
            var programReadable = AccessPolicy.Evaluate(who, ProgramEnv.Prg(), PlmAction.Read).Permitted;
            var catalog = await env.P.GetResourcesAsync(who, "ORCA", "c");
            var load = await env.P.GetLoadAsync(who, "ORCA", null, null, "c");
            var cost = await env.P.GetCostAsync(WithCost(who), "ORCA", ProgramEnv.D(0), "c");
            if (!programReadable)
            {
                Assert.Equal(OutcomeKind.NotFound, catalog.Kind);
                Assert.Equal(OutcomeKind.NotFound, load.Kind);
                Assert.Equal(OutcomeKind.NotFound, cost.Kind);
                continue;
            }
            bool Can(Markings m) => AccessPolicy.Evaluate(who, m, PlmAction.Read).Permitted;
            var readable = planned.Where(p => Can(p.M)).ToList();
            var openOk = Can(ProgramEnv.Prg());
            var itarOk = Can(ProgramEnv.Itar());
            Assert.True(catalog.IsOk, name);
            Assert.Equal(readable.Select(r => r.Key).Order(StringComparer.Ordinal), catalog.Value!.Assignments.Select(a => a.Key).Order(StringComparer.Ordinal));
            Assert.Equal((openOk ? 1 : 0) + (itarOk ? 1 : 0), catalog.Value.Resources.Count);
            // the SQL oracle: the same filter the services use, run by hand over every marked table
            foreach (var (table, expected) in new[] { ("prg.assignment", readable.Count), ("prg.resource", (openOk ? 1 : 0) + (itarOk ? 1 : 0)) })
            {
                var filter = AccessPolicy.BuildReadFilter(who, "t");
                await using var conn = new NpgsqlConnection(env.Db.SuperuserConnectionString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table} t WHERE t.program_id = (SELECT id FROM prg.program WHERE code = 'ORCA') AND {filter.Sql}", conn);
                foreach (var (k, v) in filter.Parameters) cmd.Parameters.AddWithValue(k, v);
                Assert.Equal(expected, (int)(long)(await cmd.ExecuteScalarAsync())!);
            }
            // load: hours of the readable assignments only
            var loadHours = load.Value!.Rows.Sum(r => r.Allocated);
            Assert.Equal(readable.Sum(r => r.Hours), loadHours);
            // cost: labor planned is hours times 100 over the readable assignments; actuals count the entries the user may read
            Assert.True(cost.IsOk, name);
            Assert.Equal(readable.Sum(r => r.Hours) * 100m, cost.Value!.ByType[CostTypes.Labor].Planned);
            var expectedActual = (openOk ? 300m : 0m) + (itarOk ? 1000m : 0m);
            Assert.Equal(expectedActual, cost.Value.Total.Actual);
            Assert.Equal(openOk ? 5000m : 0m, cost.Value.Total.Budget);
            // nothing the user may not read shows in anything they are given, banners included
            var json = JsonSerializer.Serialize(new object[] { catalog.Value, load.Value, cost.Value });
            for (var i = 0; i < variants.Length; i++)
                if (!Can(variants[i])) Assert.DoesNotContain($"secret-title-{i}", json, StringComparison.Ordinal);
            if (!itarOk) { Assert.DoesNotContain("secret-resource", json, StringComparison.Ordinal); Assert.DoesNotContain("ITAR", catalog.Value.Banner + load.Value.Banner + cost.Value.Banner, StringComparison.Ordinal); }
            else Assert.Contains("ITAR", cost.Value.Banner, StringComparison.Ordinal);
            // the same user without the cost permission: no rate anywhere in the catalog, a refusal for the cost view
            if (!who.Roles.Contains(Roles.ProgramCost))
            {
                Assert.All(catalog.Value.Roles, r => Assert.Null(r.CurrentRate));
                Assert.DoesNotContain("100.00", JsonSerializer.Serialize(catalog.Value), StringComparison.Ordinal);
                Assert.Equal(OutcomeKind.Forbidden, (await env.P.GetCostAsync(who, "ORCA", null, "c")).Kind);
            }
        }
        // the rows of the restricted resource exist for the user who may read them, and they are the only ones that do
        Assert.Equal(2, (await env.P.GetResourcesAsync(ProgramEnv.Alice, "ORCA", "c")).Value!.Resources.Count);
        Assert.Single((await env.P.GetResourcesAsync(WorkflowEnv.Lena, "ORCA", "c")).Value!.Resources);
    }
}

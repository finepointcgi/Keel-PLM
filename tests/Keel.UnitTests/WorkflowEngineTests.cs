using Keel.Workflow;

namespace Keel.UnitTests;

/// <summary>Vote rules and progression of the pure workflow state machine (ADR-0006).</summary>
public sealed class WorkflowEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static Participant P(string name) => new("sub-" + name, name, name.ToUpperInvariant());

    private static Dictionary<string, IReadOnlyList<Participant>> People(string slot, params string[] names) =>
        new Dictionary<string, IReadOnlyList<Participant>> { [slot] = [.. names.Select(P)] };

    private static string FanTemplate(string rule, int slotMin = 1, int slotMax = 10) => $$"""
        { "id": "t", "name": "T", "objectType": "part",
          "slots": [ { "name": "voters", "label": "Voters", "role": "plm-approver", "min": {{slotMin}}, "max": {{slotMax}} } ],
          "start": "flow",
          "nodes": [
            { "id": "flow", "type": "Serial", "steps": ["vote", "done"] },
            { "id": "vote", "type": "Parallel", "rule": {{rule}}, "fanOut": { "slot": "voters", "kind": "approval" } },
            { "id": "done", "type": "End", "outcome": "Release" } ] }
        """;

    private static WorkflowTemplate Parse(string json)
    {
        var r = TemplateParser.Parse(json);
        Assert.True(r.IsSuccess, string.Join("; ", r.Errors));
        return r.Value;
    }

    private static (WorkflowTemplate T, ExecutionState S, IReadOnlyDictionary<string, IReadOnlyList<Participant>> People) Start(string rule, params string[] voters)
    {
        var template = Parse(FanTemplate(rule, voters.Length, 10));
        var people = People("voters", voters);
        var (state, advance) = WorkflowEngine.Start(template, people, Now);
        Assert.Equal(voters.Length, advance.Created.Count);
        Assert.Null(advance.Outcome);
        return (template, state, people);
    }

    private static Advance Decide(WorkflowTemplate t, ExecutionState s, IReadOnlyDictionary<string, IReadOnlyList<Participant>> people, string who, bool approve) =>
        WorkflowEngine.Complete(t, s, people, s.Tasks.Single(x => x.AssigneeUsername == who && x.Status == WorkTaskStatus.Open).Id, approve, null, Now);

    [Fact]
    public void All_rule_waits_for_every_approval()
    {
        var (t, s, people) = Start("""{ "type": "All" }""", "a", "b", "c");
        Assert.Null(Decide(t, s, people, "a", true).Outcome);
        Assert.Null(Decide(t, s, people, "b", true).Outcome);
        var last = Decide(t, s, people, "c", true);
        Assert.Equal(InstanceOutcome.Approved, last.Outcome);
        Assert.Empty(last.Skipped);
        Assert.All(s.Tasks, x => Assert.Equal(WorkTaskStatus.Completed, x.Status));
    }

    [Fact]
    public void All_rule_rejects_on_the_first_rejection_and_skips_the_rest()
    {
        var (t, s, people) = Start("""{ "type": "All" }""", "a", "b", "c");
        Decide(t, s, people, "a", true);
        var result = Decide(t, s, people, "b", false);
        Assert.Equal(InstanceOutcome.Rejected, result.Outcome);
        Assert.Equal(["c"], result.Skipped.Select(x => x.AssigneeUsername));
        Assert.Equal(WorkTaskStatus.Skipped, s.Tasks.Single(x => x.AssigneeUsername == "c").Status);
        Assert.DoesNotContain(s.Tasks, x => x.Status == WorkTaskStatus.Open);
    }

    [Fact]
    public void Any_rule_completes_on_the_first_approval_and_skips_the_others()
    {
        var (t, s, people) = Start("""{ "type": "Any" }""", "a", "b", "c");
        var result = Decide(t, s, people, "b", true);
        Assert.Equal(InstanceOutcome.Approved, result.Outcome);
        Assert.Equal(2, result.Skipped.Count);
        Assert.Equal(1, s.Tasks.Count(x => x.Status == WorkTaskStatus.Completed));
    }

    [Fact]
    public void Any_rule_survives_rejections_until_everyone_has_rejected()
    {
        var (t, s, people) = Start("""{ "type": "Any" }""", "a", "b", "c");
        Assert.Null(Decide(t, s, people, "a", false).Outcome);
        Assert.Null(Decide(t, s, people, "b", false).Outcome);
        Assert.Equal(InstanceOutcome.Rejected, Decide(t, s, people, "c", false).Outcome);
    }

    [Fact]
    public void Any_rule_with_a_rejection_then_an_approval_is_approved()
    {
        var (t, s, people) = Start("""{ "type": "Any" }""", "a", "b");
        Assert.Null(Decide(t, s, people, "a", false).Outcome);
        Assert.Equal(InstanceOutcome.Approved, Decide(t, s, people, "b", true).Outcome);
    }

    [Fact]
    public void N_of_M_short_circuits_on_the_nth_approval()
    {
        var (t, s, people) = Start("""{ "type": "NofM", "n": 2 }""", "a", "b", "c");
        Assert.Null(Decide(t, s, people, "a", true).Outcome);
        var result = Decide(t, s, people, "c", true);
        Assert.Equal(InstanceOutcome.Approved, result.Outcome);
        Assert.Equal(["b"], result.Skipped.Select(x => x.AssigneeUsername));
    }

    [Fact]
    public void N_of_M_is_rejected_once_n_approvals_become_unreachable()
    {
        var (t, s, people) = Start("""{ "type": "NofM", "n": 2 }""", "a", "b", "c");
        Assert.Null(Decide(t, s, people, "a", false).Outcome);   // 2 of the remaining 2 must approve: still possible
        var result = Decide(t, s, people, "b", false);            // now at most 1 approval can arrive
        Assert.Equal(InstanceOutcome.Rejected, result.Outcome);
        Assert.Equal(["c"], result.Skipped.Select(x => x.AssigneeUsername));
    }

    [Fact]
    public void N_of_M_mixed_votes_continue_until_decided()
    {
        var (t, s, people) = Start("""{ "type": "NofM", "n": 2 }""", "a", "b", "c");
        Assert.Null(Decide(t, s, people, "a", false).Outcome);
        Assert.Null(Decide(t, s, people, "b", true).Outcome);
        Assert.Equal(InstanceOutcome.Approved, Decide(t, s, people, "c", true).Outcome);
    }

    [Fact]
    public void N_larger_than_the_voters_actually_picked_needs_everyone()
    {
        Assert.Equal(2, new VoteRule(VoteKind.NofM, 5).Required(2));
        Assert.Equal(3, new VoteRule(VoteKind.All).Required(3));
        Assert.Equal(1, new VoteRule(VoteKind.Any).Required(7));
    }

    [Fact]
    public void Serial_sequence_activates_steps_one_at_a_time_and_persists_position()
    {
        var seeded = Parse(SeededTemplateJson());
        var people = new Dictionary<string, IReadOnlyList<Participant>> { ["reviewer"] = [P("rita")], ["approvers"] = [P("ann"), P("bob")] };
        var (s, start) = WorkflowEngine.Start(seeded, people, Now);

        var first = Assert.Single(start.Created);
        Assert.Equal(("Technical review", "review", "sub-rita", "plm-viewer"), (first.Name, first.Kind, first.AssigneeSub, first.RequiredRole));
        Assert.Equal(Now.AddDays(5), first.DueAt);
        Assert.True(first.IsNew);
        var serial = s.Tokens.Single(t => t.NodeId == "flow");
        Assert.Equal(0, serial.StepIndex);

        var afterReview = WorkflowEngine.Complete(seeded, s, people, first.Id, true, null, Now);
        Assert.Null(afterReview.Outcome);
        Assert.Equal(["ann", "bob"], afterReview.Created.Select(x => x.AssigneeUsername).Order());
        Assert.All(afterReview.Created, x => Assert.Equal(("Approval", "approval", "plm-approver"), (x.Name, x.Kind, x.RequiredRole)));
        Assert.Equal(1, serial.StepIndex);
        Assert.True(serial.IsDirty);

        Assert.Null(Decide(seeded, s, people, "ann", true).Outcome);
        var done = Decide(seeded, s, people, "bob", true);
        Assert.Equal(InstanceOutcome.Approved, done.Outcome);
        Assert.Equal(TokenStatus.Completed, serial.Status);
        Assert.All(s.Tokens, t => Assert.Equal(TokenStatus.Completed, t.Status));
    }

    [Fact]
    public void Rejection_at_an_early_serial_step_never_creates_later_tasks()
    {
        var seeded = Parse(SeededTemplateJson());
        var people = new Dictionary<string, IReadOnlyList<Participant>> { ["reviewer"] = [P("rita")], ["approvers"] = [P("ann")] };
        var (s, start) = WorkflowEngine.Start(seeded, people, Now);
        var result = WorkflowEngine.Complete(seeded, s, people, start.Created[0].Id, false, null, Now);
        Assert.Equal(InstanceOutcome.Rejected, result.Outcome);
        Assert.Empty(result.Created);
        Assert.Single(s.Tasks);
    }

    [Fact]
    public void Role_assignment_creates_a_pool_task()
    {
        var t = Parse("""
            { "id": "pool", "name": "Pool", "objectType": "part", "start": "flow",
              "nodes": [ { "id": "flow", "type": "Serial", "steps": ["sign", "done"] },
                         { "id": "sign", "type": "Task", "kind": "approval", "assignment": { "mode": "Role", "role": "plm-approver" } },
                         { "id": "done", "type": "End", "outcome": "Release" } ] }
            """);
        var (s, advance) = WorkflowEngine.Start(t, new Dictionary<string, IReadOnlyList<Participant>>(), Now);
        var task = Assert.Single(advance.Created);
        Assert.Null(task.AssigneeSub);
        Assert.Equal("plm-approver", task.RequiredRole);
        Assert.Equal(InstanceOutcome.Approved, WorkflowEngine.Complete(t, s, new Dictionary<string, IReadOnlyList<Participant>>(), task.Id, true, null, Now).Outcome);
    }

    [Fact]
    public void Parallel_branches_can_be_whole_sequences()
    {
        var t = Parse("""
            { "id": "mix", "name": "Mixed", "objectType": "part",
              "slots": [ { "name": "x", "label": "X", "role": "plm-viewer", "min": 1, "max": 1 }, { "name": "y", "label": "Y", "role": "plm-approver", "min": 1, "max": 1 },
                         { "name": "z", "label": "Z", "role": "plm-approver", "min": 1, "max": 1 } ],
              "start": "flow",
              "nodes": [ { "id": "flow", "type": "Serial", "steps": ["both", "done"] },
                         { "id": "both", "type": "Parallel", "rule": { "type": "All" }, "branches": ["lane1", "lane2"] },
                         { "id": "lane1", "type": "Serial", "steps": ["l1a", "l1b"] },
                         { "id": "l1a", "type": "Task", "kind": "review", "assignment": { "mode": "Slot", "slot": "x" } },
                         { "id": "l1b", "type": "Task", "kind": "approval", "assignment": { "mode": "Slot", "slot": "y" } },
                         { "id": "lane2", "type": "Task", "kind": "approval", "assignment": { "mode": "Slot", "slot": "z" } },
                         { "id": "done", "type": "End", "outcome": "Release" } ] }
            """);
        var people = new Dictionary<string, IReadOnlyList<Participant>> { ["x"] = [P("x")], ["y"] = [P("y")], ["z"] = [P("z")] };
        var (s, start) = WorkflowEngine.Start(t, people, Now);
        Assert.Equal(["x", "z"], start.Created.Select(c => c.AssigneeUsername).Order());

        Assert.Null(Decide(t, s, people, "z", true).Outcome);
        var step = WorkflowEngine.Complete(t, s, people, s.Tasks.Single(x => x.AssigneeUsername == "x").Id, true, null, Now);
        Assert.Equal(["y"], step.Created.Select(c => c.AssigneeUsername));
        Assert.Equal(InstanceOutcome.Approved, Decide(t, s, people, "y", true).Outcome);
    }

    [Fact]
    public void A_rejected_lane_rejects_an_All_vote_and_skips_work_inside_other_lanes()
    {
        var t = Parse("""
            { "id": "mix", "name": "Mixed", "objectType": "part",
              "slots": [ { "name": "x", "label": "X", "role": "plm-viewer", "min": 1, "max": 1 }, { "name": "y", "label": "Y", "role": "plm-approver", "min": 1, "max": 1 },
                         { "name": "z", "label": "Z", "role": "plm-approver", "min": 1, "max": 1 } ],
              "start": "flow",
              "nodes": [ { "id": "flow", "type": "Serial", "steps": ["both", "done"] },
                         { "id": "both", "type": "Parallel", "rule": { "type": "All" }, "branches": ["lane1", "lane2"] },
                         { "id": "lane1", "type": "Serial", "steps": ["l1a", "l1b"] },
                         { "id": "l1a", "type": "Task", "kind": "review", "assignment": { "mode": "Slot", "slot": "x" } },
                         { "id": "l1b", "type": "Task", "kind": "approval", "assignment": { "mode": "Slot", "slot": "y" } },
                         { "id": "lane2", "type": "Task", "kind": "approval", "assignment": { "mode": "Slot", "slot": "z" } },
                         { "id": "done", "type": "End", "outcome": "Release" } ] }
            """);
        var people = new Dictionary<string, IReadOnlyList<Participant>> { ["x"] = [P("x")], ["y"] = [P("y")], ["z"] = [P("z")] };
        var (s, _) = WorkflowEngine.Start(t, people, Now);
        var result = Decide(t, s, people, "z", false);
        Assert.Equal(InstanceOutcome.Rejected, result.Outcome);
        Assert.Equal(["x"], result.Skipped.Select(c => c.AssigneeUsername));
        Assert.DoesNotContain(s.Tasks, x => x.Status == WorkTaskStatus.Open);
        Assert.DoesNotContain(s.Tokens, x => x.Status == TokenStatus.Active);
    }

    [Fact]
    public void Completing_a_finished_task_or_finished_instance_is_a_programming_error()
    {
        var (t, s, people) = Start("""{ "type": "Any" }""", "a", "b");
        var id = s.Tasks.Single(x => x.AssigneeUsername == "a").Id;
        WorkflowEngine.Complete(t, s, people, id, true, null, Now);
        Assert.Throws<InvalidOperationException>(() => WorkflowEngine.Complete(t, s, people, id, true, null, Now));
        Assert.Throws<InvalidOperationException>(() => WorkflowEngine.Complete(t, s, people, s.Tasks.Single(x => x.AssigneeUsername == "b").Id, true, null, Now));
    }

    // ---------------------------------------------------------------- template validation

    internal static string SeededTemplateJson()
    {
        var sql = File.ReadAllText(Path.Combine(FindRepoRoot(), "db", "migrations", "0006_workflow.sql"));
        var start = sql.IndexOf("$tpl$", StringComparison.Ordinal) + "$tpl$".Length;
        return sql[start..sql.IndexOf("$tpl$", start, StringComparison.Ordinal)];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void The_seeded_template_is_valid_and_has_the_specified_shape()
    {
        var t = Parse(SeededTemplateJson());
        Assert.Equal("part-release-review", t.Id);
        Assert.Equal(["reviewer", "approvers"], t.Slots.Select(s => s.Name));
        var approval = Assert.IsType<ParallelNode>(t.Node("approval"));
        Assert.Equal(VoteKind.All, approval.Rule.Kind);
        Assert.Equal("approvers", approval.FanOut!.Slot);
        Assert.IsType<EndNode>(t.Node("release"));
    }

    private static IReadOnlyList<string> Errors(string json)
    {
        var r = TemplateParser.Parse(json);
        Assert.False(r.IsSuccess);
        return r.Errors;
    }

    private static string Nodes(string nodes, string slots = "[]", string start = "flow") =>
        $$"""{ "id": "t", "name": "T", "objectType": "part", "slots": {{slots}}, "start": "{{start}}", "nodes": [ {{nodes}} ] }""";

    private const string OneSlot = """[ { "name": "s", "label": "S", "role": "plm-approver", "min": 1, "max": 3 } ]""";
    private const string SingleSlot = """[ { "name": "s", "label": "S", "role": "plm-approver", "min": 1, "max": 1 } ]""";
    private const string End = """{ "id": "done", "type": "End", "outcome": "Release" }""";

    [Fact]
    public void Invalid_json_is_reported_not_thrown()
    {
        Assert.Contains("not valid JSON", Errors("{ nope")[0], StringComparison.Ordinal);
        Assert.Equal(["The template must be a JSON object."], Errors("[]"));
    }

    [Fact]
    public void Orphan_nodes_are_reported()
    {
        var errors = Errors(Nodes($$"""
            { "id": "flow", "type": "Serial", "steps": ["a", "done"] },
            { "id": "a", "type": "Task", "kind": "review", "assignment": { "mode": "Role", "role": "plm-approver" } },
            { "id": "lost", "type": "Task", "kind": "review", "assignment": { "mode": "Role", "role": "plm-approver" } },
            {{End}}
            """));
        Assert.Contains(errors, e => e.Contains("'lost'", StringComparison.Ordinal) && e.Contains("orphan", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reachable_End_is_required()
    {
        var errors = Errors(Nodes("""
            { "id": "flow", "type": "Serial", "steps": ["a"] },
            { "id": "a", "type": "Task", "kind": "review", "assignment": { "mode": "Role", "role": "plm-approver" } }
            """));
        Assert.Contains(errors, e => e.Contains("must end with an End node", StringComparison.Ordinal));
    }

    [Fact]
    public void End_must_be_the_final_step_of_the_start_sequence()
    {
        var errors = Errors(Nodes($$"""
            { "id": "flow", "type": "Serial", "steps": ["done", "a"] },
            { "id": "a", "type": "Task", "kind": "review", "assignment": { "mode": "Role", "role": "plm-approver" } },
            {{End}}
            """));
        Assert.Contains(errors, e => e.Contains("End node 'done' must be the final step", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("must end with an End node", StringComparison.Ordinal));
    }

    [Fact]
    public void Shared_nodes_and_cycles_are_rejected()
    {
        var errors = Errors(Nodes($$"""
            { "id": "flow", "type": "Serial", "steps": ["a", "a", "done"] },
            { "id": "a", "type": "Task", "kind": "review", "assignment": { "mode": "Role", "role": "plm-approver" } },
            {{End}}
            """));
        Assert.Contains(errors, e => e.Contains("referenced more than once or forms a cycle", StringComparison.Ordinal));

        var cycle = Errors(Nodes($$"""
            { "id": "flow", "type": "Serial", "steps": ["inner", "done"] },
            { "id": "inner", "type": "Serial", "steps": ["flow"] },
            {{End}}
            """));
        Assert.Contains(cycle, e => e.Contains("cycle", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "type": "NofM" }""", "'n' is required")]
    [InlineData("""{ "type": "NofM", "n": 0 }""", "n of at least 1")]
    [InlineData("""{ "type": "NofM", "n": 4 }""", "needs 4 approvals but slot 's' may hold as few as 1")]
    [InlineData("""{ "type": "Most" }""", "unknown vote rule 'Most'")]
    [InlineData("""{ }""", "'type' is required")]
    public void Vote_rules_are_validated(string rule, string expected)
    {
        var errors = Errors(Nodes($$"""
            { "id": "flow", "type": "Serial", "steps": ["vote", "done"] },
            { "id": "vote", "type": "Parallel", "rule": {{rule}}, "fanOut": { "slot": "s", "kind": "approval" } },
            {{End}}
            """, OneSlot));
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void N_of_M_over_fixed_branches_cannot_exceed_the_branch_count()
    {
        var errors = Errors(Nodes($$"""
            { "id": "flow", "type": "Serial", "steps": ["vote", "done"] },
            { "id": "vote", "type": "Parallel", "rule": { "type": "NofM", "n": 3 }, "branches": ["a", "b"] },
            { "id": "a", "type": "Task", "kind": "review", "assignment": { "mode": "Role", "role": "plm-approver" } },
            { "id": "b", "type": "Task", "kind": "review", "assignment": { "mode": "Role", "role": "plm-approver" } },
            {{End}}
            """));
        Assert.Contains(errors, e => e.Contains("needs 3 approvals but there are only 2 branches", StringComparison.Ordinal));
    }

    [Fact]
    public void Slots_and_assignments_must_be_consistent()
    {
        var errors = Errors(Nodes($$"""
            { "id": "flow", "type": "Serial", "steps": ["a", "b", "done"] },
            { "id": "a", "type": "Task", "kind": "review", "assignment": { "mode": "Slot", "slot": "ghost" } },
            { "id": "b", "type": "Task", "kind": "review", "assignment": { "mode": "Slot", "slot": "s" } },
            {{End}}
            """, OneSlot));
        Assert.Contains(errors, e => e.Contains("slot 'ghost' is not declared", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("exactly one user", StringComparison.Ordinal));

        var unused = Errors(Nodes($$"""
            { "id": "flow", "type": "Serial", "steps": ["a", "done"] },
            { "id": "a", "type": "Task", "kind": "review", "assignment": { "mode": "Role", "role": "plm-approver" } },
            {{End}}
            """, SingleSlot));
        Assert.Contains(unused, e => e.Contains("Slot 's' is not used", StringComparison.Ordinal));
    }

    [Fact]
    public void Parallel_needs_exactly_one_of_branches_or_fan_out()
    {
        var errors = Errors(Nodes($$"""
            { "id": "flow", "type": "Serial", "steps": ["vote", "done"] },
            { "id": "vote", "type": "Parallel", "rule": { "type": "All" } },
            {{End}}
            """));
        Assert.Contains(errors, e => e.Contains("exactly one of 'branches' or 'fanOut'", StringComparison.Ordinal));
    }

    [Fact]
    public void Phase_two_node_types_and_unknown_types_are_named_in_the_error()
    {
        var errors = Errors(Nodes($$"""
            { "id": "flow", "type": "Serial", "steps": ["c", "done"] },
            { "id": "c", "type": "Condition" },
            { "id": "w", "type": "Wait" },
            {{End}}
            """));
        Assert.Contains(errors, e => e.Contains("'Condition' is not supported yet", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("unknown node type 'Wait'", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_problem_is_reported_in_one_pass_and_duplicates_are_caught()
    {
        var errors = Errors("""
            { "id": "Bad Id", "name": "T", "objectType": "part", "start": "nowhere", "extra": 1,
              "slots": [ { "name": "s", "label": "S", "role": "plm-auditor", "min": 0, "max": 99 } ],
              "nodes": [ { "id": "a", "type": "Task", "kind": "review", "assignment": { "mode": "Role", "role": "plm-approver" } }, { "id": "a", "type": "End", "outcome": "Release" },
                         { "id": "b", "type": "Task", "kind": "nap", "assignment": { "mode": "Pigeon" } } ] }
            """);
        Assert.True(errors.Count >= 6, string.Join("\n", errors));
        Assert.Contains(errors, e => e.Contains("Unknown template property 'extra'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Template id must be", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("role 'plm-auditor'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("used twice", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("kind must be", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("assignment mode 'Pigeon'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_template_without_any_review_work_is_rejected()
    {
        var errors = Errors(Nodes($$"""{ "id": "flow", "type": "Serial", "steps": ["done"] }, {{End}}"""));
        Assert.Contains(errors, e => e.Contains("at least one Task or Parallel", StringComparison.Ordinal));
    }

    [Fact]
    public void Role_semantics_treat_viewer_as_viewer_or_higher_and_exclude_auditors()
    {
        Assert.True(WorkflowRoles.Satisfies(["plm-author"], "plm-viewer"));
        Assert.True(WorkflowRoles.Satisfies(["plm-approver"], "plm-viewer"));
        Assert.False(WorkflowRoles.Satisfies(["plm-auditor"], "plm-viewer"));
        Assert.False(WorkflowRoles.Satisfies(["plm-viewer"], "plm-approver"));
        Assert.True(WorkflowRoles.Satisfies(["plm-viewer", "plm-approver"], "plm-approver"));
    }
}

using Keel.Core;
using Keel.Process;
using Keel.Web.Services.Visuals;

namespace Keel.UnitTests;

public sealed class ProcessUnitTests
{
    private static readonly Guid Part = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static OperationDef Op(int n, string name = "Op", IReadOnlyList<ComponentAssignment>? comps = null, bool inspection = false, string? wc = null) =>
        new(Guid.NewGuid(), n, name, wc, 5m, 1.5m, null, inspection, inspection ? "Check the bore" : null, comps ?? [], [], []);
    private static PlanContent Plan(params OperationDef[] ops) => new([new RoutingDef("PRIMARY", "Primary", true, null, null, ops)]);
    private static readonly IReadOnlySet<string> NoCenters = new HashSet<string>();

    [Fact]
    public void Content_has_one_text_and_one_digest_whatever_order_it_was_written_in()
    {
        var a = Plan(Op(20), Op(10));
        var b = Plan(a.Routings[0].Operations[1], a.Routings[0].Operations[0]);
        Assert.Equal(ProcessRules.Serialize(a), ProcessRules.Serialize(b));
        Assert.Equal(ProcessRules.Hash(a), ProcessRules.Hash(b));
        Assert.Matches("^[0-9a-f]{64}$", ProcessRules.Hash(a));
        Assert.Equal(ProcessRules.Hash(a), ProcessRules.Hash(ProcessRules.Deserialize(ProcessRules.Serialize(a))));
        Assert.NotEqual(ProcessRules.Hash(a), ProcessRules.Hash(Plan(Op(10), Op(20, "Other"))));
    }

    [Fact]
    public void A_changed_mbom_binding_changes_the_review_digest()
    {
        var content = ProcessRules.Hash(Plan(Op(10)));
        Assert.NotEqual(ProcessRules.ReviewHash(content, "A", "x"), ProcessRules.ReviewHash(content, "B", "x"));
        Assert.NotEqual(ProcessRules.ReviewHash(content, "A", "x"), ProcessRules.ReviewHash(content, "A", "y"));
        Assert.Equal(ProcessRules.ReviewHash(content, "A", "x"), ProcessRules.ReviewHash(content, "A", "x"));
    }

    [Fact]
    public void Validation_reports_each_structural_problem_in_plain_words()
    {
        Assert.Empty(ProcessRules.Validate(Plan(Op(10, wc: "WC-1")), new HashSet<string> { "WC-1" }));
        var errors = string.Join("\n", ProcessRules.Validate(new PlanContent(
        [
            new RoutingDef("PRIMARY", "A", true, null, null, [Op(10, wc: "NOPE"), Op(10), Op(20, inspection: false) with { SetupMinutes = -1 }, Op(30) with { Inspection = true, InspectionText = null }]),
            new RoutingDef("primary", "B", true, null, null, []),
        ]), NoCenters));
        Assert.Contains("exactly one routing is the primary", errors, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not an active work center", errors, StringComparison.Ordinal);
        Assert.Contains("used twice", errors, StringComparison.Ordinal);
        Assert.Contains("times are 0 to", errors, StringComparison.Ordinal);
        Assert.Contains("needs a text", errors, StringComparison.Ordinal);
        Assert.Contains("must be 1 to 20 capital letters", errors, StringComparison.Ordinal);
    }

    [Fact]
    public void Steps_need_text_a_known_kind_and_attachments_need_a_caption()
    {
        var step = new StepDef(Guid.NewGuid(), "bogus", "", [], [new AttachmentDef("D-000001", Guid.NewGuid(), "")]);
        var errors = string.Join("\n", ProcessRules.Validate(Plan(Op(10) with { Steps = [step] }), NoCenters));
        Assert.Contains("unknown step kind", errors, StringComparison.Ordinal);
        Assert.Contains("needs text", errors, StringComparison.Ordinal);
        Assert.Contains("needs a caption", errors, StringComparison.Ordinal);
    }

    private static MbomLineRef Line(int find, decimal qty, bool phantom = false, bool redacted = false, Guid? master = null) =>
        new(find, 0, redacted, redacted ? null : master ?? Part, redacted ? null : "P-" + find, redacted ? null : "Part " + find, qty, "EA", phantom, "Buy");

    [Fact]
    public void Coverage_finds_unassigned_over_assigned_and_orphaned_lines()
    {
        var lines = new[] { Line(10, 4), Line(20, 1), Line(30, 2, phantom: true) };
        var plan = Plan(Op(10, comps: [new ComponentAssignment(Part, 10, 0, 3m)]), Op(20, comps: [new ComponentAssignment(Part, 10, 0, 3m), new ComponentAssignment(Part, 99, 0, null)]));
        var f = ProcessRules.Coverage(plan, lines);
        Assert.Contains(f, x => x.Code == "line.over" && x.Severity == Severities.Error);
        Assert.Contains(f, x => x.Code == "assignment.orphan" && x.Operation == 20);
        Assert.Contains(f, x => x.Code == "line.unassigned" && x.Message.Contains("Line 20", StringComparison.Ordinal));
        Assert.DoesNotContain(f, x => x.Message.Contains("Line 30", StringComparison.Ordinal)); // a phantom line is not consumed
        // An exactly covered plan has no findings.
        var ok = Plan(Op(10, comps: [new ComponentAssignment(Part, 10, 0, null), new ComponentAssignment(Part, 20, 0, null)]));
        Assert.Empty(ProcessRules.Coverage(ok, lines));
    }

    [Fact]
    public void A_line_whose_component_changed_is_a_warning_and_a_redacted_line_still_matches_by_find_number()
    {
        var plan = Plan(Op(10, comps: [new ComponentAssignment(Part, 10, 0, null), new ComponentAssignment(Guid.NewGuid(), 20, 0, null)]));
        var f = ProcessRules.Coverage(plan, [Line(10, 1, master: Guid.NewGuid()), Line(20, 1, redacted: true)]);
        Assert.Contains(f, x => x.Code == "assignment.part_changed");
        Assert.DoesNotContain(f, x => x.Code is "assignment.orphan" or "line.unassigned");
    }

    [Fact]
    public void Staleness_follows_the_released_mbom_revision()
    {
        var released = new MbomRevisionRef("B", "Released", 1, "sha-b");
        Assert.Equal(Staleness.Current, ProcessRules.StalenessOf("B", "sha-b", released));
        Assert.Equal(Staleness.Stale, ProcessRules.StalenessOf("A", "sha-a", released));
        Assert.Equal(Staleness.Stale, ProcessRules.StalenessOf("B", "other", released));
        Assert.Equal(Staleness.NoReleasedMbom, ProcessRules.StalenessOf("A", "sha-a", null));
    }

    [Theory]
    [InlineData(-1, "overdue")]
    [InlineData(0, "d7")]
    [InlineData(7, "d7")]
    [InlineData(8, "d14")]
    [InlineData(14, "d14")]
    [InlineData(15, "d30")]
    [InlineData(30, "d30")]
    [InlineData(31, null)]
    public void Calibration_notices_are_staged_by_days_to_the_due_date(int days, string? stage)
    {
        var today = new DateOnly(2026, 10, 29);
        Assert.Equal(stage, ProcessRules.NoticeStage(today.AddDays(days), today));
    }

    [Fact]
    public void Calibration_state_distinguishes_not_required_never_overdue_due_soon_and_valid()
    {
        var today = new DateOnly(2026, 10, 29);
        Assert.Equal("not_required", ProcessRules.CalibrationState(false, null, today));
        Assert.Equal("never", ProcessRules.CalibrationState(true, null, today));
        Assert.Equal("overdue", ProcessRules.CalibrationState(true, today.AddDays(-1), today));
        Assert.Equal("due_soon", ProcessRules.CalibrationState(true, today, today));
        Assert.Equal("valid", ProcessRules.CalibrationState(true, today.AddDays(31), today));
    }

    [Fact]
    public void The_next_operation_number_steps_by_ten()
    {
        Assert.Equal(10, ProcessRules.NextOperationNumber([]));
        Assert.Equal(30, ProcessRules.NextOperationNumber([10, 20]));
        Assert.Equal(30, ProcessRules.NextOperationNumber([25]));
        Assert.Equal(9999, ProcessRules.NextOperationNumber([9995]));
    }

    private static RoutingView RoutingOf(params OperationView[] ops) => new("PRIMARY", "Primary", true, null, null, ops);
    private static OperationView OpView(int n, bool inspection = false, params ComponentView[] comps) =>
        new(Guid.NewGuid(), n, "Op " + n, "WC-1", "Center", 5m, 1m, null, inspection, inspection ? "Check" : null, comps, [], []);

    [Fact]
    public void The_matrix_has_one_row_per_line_and_says_coverage_in_words()
    {
        var lines = new[]
        {
            new BoundLineView(10, 0, false, "P-1", "Screw", 4m, "EA", false, "Buy"), new BoundLineView(20, 0, false, "P-2", "Plate", 1m, "EA", false, "Buy"),
            new BoundLineView(30, 0, true, null, null, 2m, "EA", false, "Buy"), new BoundLineView(40, 0, false, "P-4", "Bracket", 1m, "EA", true, "Make"),
        };
        var routing = RoutingOf(
            OpView(10, false, new ComponentView(10, 0, false, "P-1", "Screw", 4m, "EA", 3m, false, null, false)),
            OpView(20, false, new ComponentView(10, 0, false, "P-1", "Screw", 4m, "EA", 1m, false, null, false), new ComponentView(20, 0, false, "P-2", "Plate", 1m, "EA", 2m, false, null, false)));
        var m = PlanMatrix.Build(routing, lines);
        Assert.Equal([10, 20], m.Operations.Select(o => o.Number));
        Assert.Equal(4, m.Rows.Count);
        Assert.Equal("full", m.Rows[0].Coverage);
        Assert.Equal("over", m.Rows[1].Coverage);
        Assert.Equal("none", m.Rows[2].Coverage);
        Assert.True(m.Rows[2].Line.Redacted);
        Assert.Equal("phantom", m.Rows[3].Coverage);
        Assert.Equal([3m, 1m], m.Rows[0].Cells.Select(c => c!.Quantity));
        Assert.Null(m.Rows[2].Cells[0]);
        Assert.Equal("Over-assigned", PlanMatrix.CoverageLabel("over"));
    }

    [Fact]
    public void The_routing_flow_is_a_chain_per_routing_with_inspection_points_told_apart_by_shape()
    {
        var alt = new RoutingView("ALT-1", "Alternate", false, "Engine = B", null, [OpView(10)]);
        var graph = RoutingFlowGraphBuilder.Build([alt, RoutingOf(OpView(10), OpView(20, inspection: true), OpView(30))], "PP-000001", "UNCLASSIFIED");
        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(2, graph.Edges.Count);
        Assert.All(graph.Edges, e => Assert.StartsWith("PRIMARY:", e.From, StringComparison.Ordinal));
        Assert.Equal(GraphNodeKind.Mbom, graph.Nodes.Single(n => n.Id == "PRIMARY:20").Kind);
        Assert.Equal(GraphNodeKind.Parent, graph.Nodes.Single(n => n.Id == "PRIMARY:10").Kind);
        Assert.Equal([0, 1, 2], graph.Nodes.Where(n => n.Id.StartsWith("PRIMARY", StringComparison.Ordinal)).Select(n => n.Layer));
        var layout = LayeredLayout.Compute([.. graph.Nodes.Select(n => new LayoutNodeInput(n.Id, n.Layer, 176, 56))], [.. graph.Edges.Select(e => new LayoutEdgeInput(e.From, e.To))]);
        Assert.Equal(4, layout.Nodes.Count);
        // The picture names no component or tool: only operation facts.
        Assert.DoesNotContain(graph.Nodes.SelectMany(n => n.Facts ?? []), f => f.Key is "Component" or "Tool");
    }

    [Fact]
    public void Step_kinds_say_what_they_are_in_words_and_with_a_symbol()
    {
        foreach (var k in StepKinds.All) Assert.False(string.IsNullOrWhiteSpace(StepKinds.Label(k)));
        Assert.True(StepKinds.IsCallout(StepKinds.Warning));
        Assert.True(StepKinds.IsCallout(StepKinds.Quality));
        Assert.False(StepKinds.IsCallout(StepKinds.Step));
        Assert.Contains("safety", StepKinds.Label(StepKinds.Caution), StringComparison.Ordinal);
        Assert.NotEqual(StepKinds.Glyph(StepKinds.Warning), StepKinds.Glyph(StepKinds.Caution));
    }

    [Fact]
    public void Process_kinds_are_awareness_kinds_with_routes_and_labels()
    {
        foreach (var kind in new[] { AwarenessKinds.ProcessPlan, AwarenessKinds.Tool })
        {
            Assert.True(AwarenessKinds.IsKnown(kind));
            Assert.StartsWith("process/", AwarenessKinds.Path(kind, "X-1"), StringComparison.Ordinal);
            Assert.NotEqual(kind, AwarenessKinds.Label(kind));
        }
        Assert.Equal(AwarenessKinds.ProcessPlan, AwarenessKinds.FromAuditObjectType("process_plan"));
        Assert.True(Keel.Workspace.ObjectKinds.IsKnown("process_plan") && Keel.Workspace.ObjectKinds.IsKnown("tool"));
    }
}

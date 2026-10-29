using System.Text;
using Keel.Requirements;

namespace Keel.UnitTests;

public sealed class RequirementsIoUnitTests
{
    private static RequirementRow Row(string key, string title, string text, string? parent = null, string status = "approved") =>
        new(false, "1", 0, key, parent, title, text, "functional", "high", "test", "why", status, "sha", "UNCLASSIFIED", null);

    [Fact]
    public void Csv_reads_quotes_embedded_newlines_comments_and_header_aliases()
    {
        var csv = "# UNCLASSIFIED\n\"Requirement ID\",Name,Description,Category,Priority,Verification\n" +
                  "R-1,\"Depth, rating\",\"Line one\nLine \"\"two\"\"\",Performance,HIGH,Test\n" +
                  ",Second,Plain,,,\n\n";
        var p = RequirementIo.ParseCsv(csv, false);
        Assert.Empty(p.Errors);
        Assert.Equal(2, p.Rows.Count);
        Assert.Equal(("R-1", "Depth, rating", "Line one\nLine \"two\"", "performance", "high", "test"), (p.Rows[0].SourceId, p.Rows[0].Title, p.Rows[0].Text, p.Rows[0].Type, p.Rows[0].Priority, p.Rows[0].Method));
        Assert.Null(p.Rows[1].SourceId);
        Assert.Null(p.Rows[1].Type);
    }

    [Fact]
    public void Spreadsheet_csv_detects_the_delimiter_and_ignores_a_byte_order_mark()
    {
        foreach (var d in new[] { ';', '\t', ',' })
        {
            var text = $"﻿title{d}text{d}status\r\nA{d}Text A{d}draft\r\nB{d}Text B{d}approved\r\n";
            var p = RequirementIo.ParseCsv(text, true);
            Assert.Empty(p.Errors);
            Assert.Equal(["A", "B"], p.Rows.Select(r => r.Title));
            Assert.Equal(["draft", "approved"], p.Rows.Select(r => r.Status));
        }
    }

    [Fact]
    public void Csv_reports_what_is_wrong()
    {
        Assert.Contains("title column and a text column", RequirementIo.ParseCsv("name,priority\nA,high\n", false).Errors[0], StringComparison.Ordinal);
        Assert.Contains("quoted value", RequirementIo.ParseCsv("title,text\n\"open,x\n", false).Errors[0], StringComparison.Ordinal);
        Assert.NotEmpty(RequirementIo.ParseCsv("", false).Errors);
        Assert.NotEmpty(RequirementIo.ParseCsv("title,name,text\nA,B,C\n", false).Errors); // two columns mean the same
    }

    [Fact]
    public void Exports_guard_against_formula_injection_and_read_back()
    {
        var rows = new[] { Row("R-0001", "=cmd|' /C calc'!A1", "+1 and @SUM"), Row("R-0002", "-2", "Normal, \"quoted\"\nline", "R-0001") };
        foreach (var spreadsheet in new[] { false, true })
        {
            var text = RequirementIo.ExportCsv(rows, "CUI // EXPT", spreadsheet);
            Assert.StartsWith(spreadsheet ? "﻿# CUI // EXPT" : "# CUI // EXPT", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\n=cmd", text, StringComparison.Ordinal);
            Assert.Contains("'=cmd", text, StringComparison.Ordinal);
            var back = RequirementIo.ParseCsv(text, spreadsheet);
            Assert.Empty(back.Errors);
            Assert.Equal(["=cmd|' /C calc'!A1", "-2"], back.Rows.Select(r => r.Title));
            Assert.Equal(["+1 and @SUM", "Normal, \"quoted\"\nline"], back.Rows.Select(r => r.Text));
            Assert.Equal("R-0001", back.Rows[1].ParentRef);
        }
    }

    [Fact]
    public void ReqIf_round_trips_hierarchy_and_escapes_markup()
    {
        var rows = new[] { Row("R-0001", "Parent <b>", "Text & more"), Row("R-0002", "Child", "Child text", "R-0001"), Row("R-0003", "Other", "Third") };
        var xml = RequirementIo.ExportReqIf(rows, "RS-000001", "UNCLASSIFIED");
        Assert.Contains("SPEC-OBJECT", xml, StringComparison.Ordinal);
        var p = RequirementIo.ParseReqIf(xml);
        Assert.Empty(p.Errors);
        Assert.Equal(["R-0001", "R-0002", "R-0003"], p.Rows.Select(r => r.SourceId));
        Assert.Equal([null, "R-0001", null], p.Rows.Select(r => r.ParentRef));
        Assert.Equal("Parent <b>", p.Rows[0].Title);
        Assert.Equal("Text & more", p.Rows[0].Text);
    }

    [Fact]
    public void ReqIf_refuses_dtds_and_reports_structure_errors()
    {
        Assert.Contains("cannot be read", RequirementIo.ParseReqIf("<!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><r>&x;</r>").Errors[0], StringComparison.Ordinal);
        Assert.Contains("cannot be read", RequirementIo.ParseReqIf("<a><b></a>").Errors[0], StringComparison.Ordinal);
        Assert.NotEmpty(RequirementIo.ParseReqIf("<REQ-IF/>").Errors);
        Assert.Contains(RequirementIo.ParseReqIf("<R><SPEC-OBJECT/></R>").Errors, e => e.Contains("no identifier", StringComparison.Ordinal));
        Assert.Contains(RequirementIo.ParseReqIf("<R><SPEC-OBJECT IDENTIFIER=\"a\"/><SPEC-OBJECT IDENTIFIER=\"a\"/></R>").Errors, e => e.Contains("twice", StringComparison.Ordinal));
    }

    [Fact]
    public void Exports_leave_out_restricted_rows()
    {
        var restricted = new RequirementRow(true, "2", 0, null, null, null, null, null, null, null, null, null, null, null, null);
        var csv = RequirementIo.ExportCsv([Row("R-0001", "A", "a"), restricted], "UNCLASSIFIED", false);
        Assert.Equal(3, csv.TrimEnd().Split('\n').Length); // banner, header, one row
        Assert.Equal(Encoding.UTF8.GetByteCount(csv), csv.Length + csv.Count(c => c > 127));
    }

    // ---------------------------------------------------------------- the plan

    private static IReadOnlyList<RequirementRow> Existing() =>
    [
        Row("R-0001", "Parent", "Parent text") with { Markings = null },
        Row("R-0002", "Child", "Child text", "R-0001"),
    ];

    private static ImportPlanner.Planned Plan(IReadOnlyList<ImportRow> rows, Func<RequirementRow, bool>? canModify = null) => ImportPlanner.Plan(rows, Existing(), canModify ?? (_ => true));

    [Fact]
    public void The_plan_separates_new_changed_unchanged_and_wrong_rows()
    {
        var rows = new[]
        {
            new ImportRow(2, "R-0001", null, null, "Parent text changed", null, null, null, null, null),
            new ImportRow(3, "R-0002", null, "Child", "Child text", null, null, null, "why", null),
            new ImportRow(4, "NEW-1", null, "Brand new", "Text", "safety", "low", "analysis", null, "draft"),
            new ImportRow(5, null, null, "Bad", "x", "nonsense", null, null, null, null),
            new ImportRow(6, null, null, null, null, null, null, null, null, null),
        };
        var plan = Plan(rows);
        Assert.Equal(["update", "unchanged", "create", "error", "error"], plan.Rows.Select(r => r.Action));
        Assert.Equal("R-0001", plan.Rows[0].Key);
        Assert.Contains(plan.Rows[3].Problems, p => p.Contains("type must be", StringComparison.Ordinal));
        Assert.Equal(["update", "add"], plan.Ops.Select(o => o.Op));
        Assert.Matches("^[0-9a-f]{64}$", plan.Digest);
    }

    [Fact]
    public void Blank_fields_keep_the_old_value_on_an_update_and_use_defaults_on_a_new_row()
    {
        var plan = Plan([new ImportRow(2, "R-0001", null, "Renamed", null, null, null, null, null, null), new ImportRow(3, null, null, "New", "Text", null, null, null, null, null)]);
        var update = plan.Ops[0].Input!;
        Assert.Equal(("Renamed", "Parent text", "functional", "high", "test"), (update.Title, update.Text, update.Type, update.Priority, update.Method));
        var add = plan.Ops[1].Input!;
        Assert.Equal(("functional", "medium", "test", "draft"), (add.Type, add.Priority, add.Method, add.Status));
    }

    [Fact]
    public void New_rows_are_created_parents_first_and_may_name_each_other()
    {
        var rows = new[]
        {
            new ImportRow(2, "c", "b", "Child", "t", null, null, null, null, null),
            new ImportRow(3, "b", "a", "Middle", "t", null, null, null, null, null),
            new ImportRow(4, "a", null, "Top", "t", null, null, null, null, null),
            new ImportRow(5, "d", "R-0002", "Under existing", "t", null, null, null, null, null),
        };
        var plan = Plan(rows);
        Assert.Equal(["Top", "Middle", "Child", "Under existing"], plan.Ops.Select(o => o.Input!.Title));
        Assert.Equal([null, "#0", "#1", "R-0002"], plan.Ops.Select(o => o.Input!.ParentKey));
    }

    [Fact]
    public void A_parent_loop_or_unknown_parent_is_a_problem_and_a_changed_parent_is_a_move()
    {
        var loop = Plan([new ImportRow(2, "a", "b", "A", "t", null, null, null, null, null), new ImportRow(3, "b", "a", "B", "t", null, null, null, null, null)]);
        Assert.All(loop.Rows, r => Assert.Contains("loop", string.Join(' ', r.Problems), StringComparison.Ordinal));
        var unknown = Plan([new ImportRow(2, null, "nope", "A", "t", null, null, null, null, null)]);
        Assert.Contains("parent nope was not found", unknown.Rows[0].Problems[0], StringComparison.Ordinal);
        var move = Plan([new ImportRow(2, "R-0002", null, "Child", "Child text", null, null, null, null, null), new ImportRow(3, "R-0001", "R-0002", "Parent", "Parent text", null, null, null, null, null)]);
        Assert.Contains(move.Ops, o => o.Op == "move" && o.Key == "R-0001");
    }

    [Fact]
    public void A_row_for_a_requirement_the_caller_may_not_change_is_a_problem_and_the_digest_depends_on_the_content()
    {
        var denied = Plan([new ImportRow(2, "R-0001", null, "x", "y", null, null, null, null, null)], r => r.Key != "R-0001");
        Assert.Contains("not authorized", denied.Rows[0].Problems[0], StringComparison.Ordinal);
        var a = Plan([new ImportRow(2, null, null, "Same", "Text", null, null, null, null, null)]).Digest;
        Assert.Equal(a, Plan([new ImportRow(9, null, null, "Same", "Text", null, null, null, null, null)]).Digest);
        Assert.NotEqual(a, Plan([new ImportRow(2, null, null, "Same", "Text 2", null, null, null, null, null)]).Digest);
    }
}

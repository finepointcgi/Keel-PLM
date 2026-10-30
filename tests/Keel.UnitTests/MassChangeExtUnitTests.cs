using System.Text;
using System.Text.RegularExpressions;
using Keel.Core.Reports;
using Keel.Reports;
using Keel.Reports.Tabular;

namespace Keel.UnitTests;

/// <summary>Mass change beyond parts (migration 0029, ADR-0050): the upload format, its limits and the rules that keep the database, the service and the host in step.</summary>
public sealed class MassChangeExtUnitTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Keel.slnx"))) dir = dir.Parent;
            return dir!.FullName;
        }
    }

    private static List<IReadOnlyList<string>> Table(params string[] lines) => TableReader.ReadCsv(Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n"));

    [Fact]
    public void A_file_gives_each_object_its_own_typed_values_and_blank_cells_leave_the_shared_value()
    {
        var parsed = MassUpload.Parse(Table("Assembly,Kind,Operation,Component,Replacement,Quantity,Find number,Designators", "A-1,assembly,add_component,P-1,,2.5,30,\"R1, R2\"", "A-2,assembly,add_component,P-1,,,,"));
        Assert.Empty(parsed.Errors);
        Assert.Equal("assembly", parsed.Kind);
        Assert.Equal(MassOperations.AddComponent, parsed.Operation);
        Assert.Equal(["A-1", "A-2"], parsed.Keys);
        var a1 = parsed.ItemParameters["A-1"];
        Assert.Equal(2.5m, a1["quantity"]!.GetValue<decimal>());
        Assert.Equal(30, a1["find_number"]!.GetValue<int>());
        Assert.Equal(["R1", "R2"], a1["designators"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(a1.ContainsKey("replacement"));
        Assert.Equal("P-1", parsed.ItemParameters["A-2"]["component"]!.GetValue<string>());
        Assert.False(parsed.ItemParameters["A-2"].ContainsKey("quantity"));
    }

    [Fact]
    public void A_file_with_a_key_column_only_has_no_item_values_and_header_spelling_is_forgiving()
    {
        var parsed = MassUpload.Parse(Table(" Part ", "P-1", "", "P-2"));
        Assert.Empty(parsed.Errors);
        Assert.Equal(["P-1", "P-2"], parsed.Keys);
        Assert.Empty(parsed.ItemParameters);
        Assert.Equal(["P-1"], MassUpload.Parse(Table("find-number,PART", "3,P-1")).Keys);
    }

    [Theory]
    [InlineData("Part,Name,Markings\r\nP-1,material,ITAR", "not one of the columns")]
    [InlineData("Name,Value\r\nmaterial,x", "must name the objects")]
    [InlineData("Part,Part\r\nP-1,P-2", "Only one column")]
    [InlineData("Part,Name,Name\r\nP-1,a,b", "appears twice")]
    [InlineData("Part,Name\r\nP-1,a\r\nP-1,b", "listed twice")]
    [InlineData("Part,Quantity\r\nP-1,-3", "positive number")]
    [InlineData("Part,Quantity\r\nP-1,two", "positive number")]
    [InlineData("Part,Find number\r\nP-1,1.5", "whole number")]
    [InlineData("Part,Operation\r\nP-1,favorite\r\nP-2,watch", "one file runs one operation")]
    [InlineData("Part,Kind\r\nP-1,part\r\nP-2,document", "one file changes one kind")]
    [InlineData("Part\r\n", "no objects")]
    public void A_file_that_cannot_be_read_unambiguously_is_refused_in_words(string csv, string expected)
    {
        var parsed = MassUpload.Parse(TableReader.ReadCsv(Encoding.UTF8.GetBytes(csv)));
        Assert.Contains(parsed.Errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void More_rows_than_the_cap_are_refused_before_anything_is_looked_up()
    {
        var parsed = MassUpload.Parse(Table(["Part", .. Enumerable.Range(1, ReportsLimits.MaxMassObjects + 1).Select(i => $"P-{i:D6}")]));
        Assert.Contains(parsed.Errors, e => e.Contains($"At most {ReportsLimits.MaxMassObjects}", StringComparison.Ordinal));
        Assert.True(parsed.Keys.Count <= ReportsLimits.MaxMassObjects);
        Assert.Contains("too many rows", Assert.Throws<XlsxFormatException>(() => TableReader.ReadCsv(Encoding.UTF8.GetBytes("a\r\n" + string.Concat(Enumerable.Repeat("b\r\n", 21_500))))).Message, StringComparison.Ordinal);
        Assert.Throws<XlsxFormatException>(() => TableReader.ReadCsv(new byte[5 * 1024 * 1024 + 1]));
    }

    [Fact]
    public void Errors_name_the_spreadsheet_row_and_stop_at_twenty()
    {
        var parsed = MassUpload.Parse(Table(["Part,Quantity", "", "P-1,2", .. Enumerable.Range(2, 40).Select(i => $"P-{i},bad")]));
        Assert.StartsWith("Row 4:", parsed.Errors[0], StringComparison.Ordinal); // blank rows still count, so the number matches what the user sees
        Assert.True(parsed.Errors.Count <= 21);
    }

    [Fact]
    public void A_template_is_the_header_row_only_and_neutralizing_apostrophes_are_removed_on_read()
    {
        var csv = Encoding.UTF8.GetString(MassUpload.Template(MassKinds.Assembly, MassOperations.ReplaceComponent)).TrimStart('﻿');
        Assert.Equal("assembly,component,replacement,quantity,unit,notes\r\n", csv);
        Assert.Equal("part,name,value\r\n", Encoding.UTF8.GetString(MassUpload.Template(MassKinds.Part, MassOperations.SetAttribute)).TrimStart('﻿'));
        Assert.Equal("document\r\n", Encoding.UTF8.GetString(MassUpload.Template(MassKinds.Document, MassOperations.Favorite)).TrimStart('﻿'));
        var parsed = MassUpload.Parse(Table("Part,Name,Value", "P-1,note,'=1+1"));
        Assert.Equal("=1+1", parsed.ItemParameters["P-1"]["value"]!.GetValue<string>());
        // Every column a template offers is one the reader accepts, for every operation.
        foreach (var op in MassChangeService.AllOperations)
            Assert.DoesNotContain(MassUpload.Parse(TableReader.ReadCsv(MassUpload.Template(MassKinds.Part, op).Append((byte)'\n').ToArray())).Errors, e => e.Contains("not one of the columns", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_operation_the_service_offers_is_allowed_by_the_database_and_every_kind_has_a_target_and_a_constraint()
    {
        var sql = File.ReadAllText(Path.Combine(Root, "db", "migrations", "0029_mass_change_ext.sql"));
        foreach (var op in MassChangeService.AllOperations) Assert.Contains($"'{op}'", sql, StringComparison.Ordinal);
        foreach (var kind in MassKinds.All) Assert.Contains($"'{kind}'", sql, StringComparison.Ordinal);
        var setup = File.ReadAllText(Path.Combine(Root, "src", "Keel.Api", "Infrastructure", "ReportsSetup.cs"));
        foreach (var target in new[] { "MassPartTarget", "MassDocumentTarget", "MassAssemblyTarget", "MassRequirementTarget", "MassChangeBinder" }) Assert.Contains(target, setup, StringComparison.Ordinal);
        // Forward-only: the migration never drops a table or column, and replaces a constraint only by adding the old values back.
        Assert.DoesNotMatch(new Regex(@"DROP\s+(TABLE|COLUMN|SCHEMA)", RegexOptions.IgnoreCase), sql);
        foreach (var old in new[] { "set_attribute", "change_markings", "baseline_proposal", "submit_review", "watch", "favorite" }) Assert.Contains($"'{old}'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_service_sets_the_change_notice_parameter_and_only_for_parts_and_assemblies()
    {
        Assert.True(MassChangeService.CanAttachToChange(MassKinds.Part, MassOperations.SetAttribute));
        Assert.True(MassChangeService.CanAttachToChange(MassKinds.Assembly, MassOperations.ReplaceComponent));
        Assert.False(MassChangeService.CanAttachToChange(MassKinds.Document, MassOperations.SetDescription));
        Assert.False(MassChangeService.CanAttachToChange(MassKinds.Requirement, MassOperations.SetRequirementField));
        Assert.False(MassChangeService.CanAttachToChange(MassKinds.Part, MassOperations.Watch));
        Assert.False(MassChangeService.CanAttachToChange(MassKinds.Part, MassOperations.SubmitReview));
        // The reserved name is written in exactly one place of the service (and read by the two part-like targets).
        var service = File.ReadAllText(Path.Combine(Root, "src", "Keel.Reports", "MassChangeService.cs"));
        Assert.Single(Regex.Matches(service, @"parameters\[MassParameterNames\.Change\]\s*="));
        Assert.Contains("parameters.Remove(MassParameterNames.Change)", service, StringComparison.Ordinal);
        foreach (var file in new[] { "MassDocumentTarget.cs", "MassRequirementTarget.cs" })
            Assert.DoesNotContain("MassParameterNames", File.ReadAllText(Path.Combine(Root, "src", "Keel.Api", "Infrastructure", "Reports", file)), StringComparison.Ordinal);
    }

    [Fact]
    public void Reversibility_is_declared_for_every_operation_that_can_be_undone()
    {
        foreach (var op in new[] { MassOperations.SetDescription, MassOperations.AddComponent, MassOperations.ReplaceComponent, MassOperations.RemoveComponent, MassOperations.SetRequirementField })
            Assert.True(MassChangeService.IsReversible(op), op);
        Assert.False(MassChangeService.IsReversible(MassOperations.SubmitReview));
    }
}

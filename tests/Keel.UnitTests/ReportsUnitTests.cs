using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Keel.Audit;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Documents.Marking;
using Keel.Reports;
using Keel.Reports.Pdf;
using Keel.Reports.Tabular;
using UglyToad.PdfPig;

namespace Keel.UnitTests;

/// <summary>Output module (ADR-0044): workbook writer and reader, formula neutralization, import limits, PDF marking on every page, report running, structure rules.</summary>
public sealed partial class ReportsUnitTests
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

    private static Markings M(DistributionStatement d = DistributionStatement.D, string? program = "ORCA", string cui = "", ExportJurisdiction j = ExportJurisdiction.Itar) =>
        Markings.Create(j, d, "ENG", j == ExportJurisdiction.Ear ? "9A610" : null, j == ExportJurisdiction.Itar ? "XI(d)" : null, cui.Length == 0 ? ["EXPT"] : ["EXPT", cui], "[Company] Engineering", "Critical technology", program, []).Value;

    // ---------------------------------------------------------------- xlsx

    [Fact]
    public void A_workbook_round_trips_text_and_numbers_and_the_reader_restores_neutralized_text()
    {
        var bytes = XlsxWriter.Write([new SheetData("BOM", ["Part", "Quantity", "Notes"], [[ "P-000001", Cell.N(2.5m), "=HYPERLINK(\"http://x\")" ], [ "P-000002", Cell.N(1), "plain" ]], ["BANNER LINE"])]);
        var rows = XlsxReader.ReadFirstSheet(bytes);
        Assert.Equal("BANNER LINE", rows[0][0]);
        Assert.Equal(["Part", "Quantity", "Notes"], rows[1]);
        Assert.Equal("2.5", rows[2][1]);
        Assert.Equal("=HYPERLINK(\"http://x\")", rows[2][2]); // restored on read, but stored as text, never as a formula
        // The stored cell is an inline string that begins with an apostrophe, so no spreadsheet evaluates it.
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var xml = sheet.ReadToEnd();
        Assert.Contains("'=HYPERLINK", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+SUM(A1)")]
    [InlineData("-2+3")]
    [InlineData("@SUM(1)")]
    [InlineData("\t=1")]
    public void Every_formula_trigger_is_neutralized_in_xlsx_and_csv(string value)
    {
        Assert.StartsWith("'", CellSafety.Neutralize(value), StringComparison.Ordinal);
        var csv = Encoding.UTF8.GetString(Csv.Write(["H"], [[Cell.T(value)]]));
        Assert.Contains("'" + value, csv.Replace("\"", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_are_numbers_and_negative_text_is_not()
    {
        Assert.Equal("", CellSafety.Neutralize(null));
        Assert.Equal("-5", Encoding.UTF8.GetString(Csv.Write(["H"], [[Cell.N(-5)]])).Split("\r\n")[1]);
        Assert.Equal("'-5", CellSafety.Neutralize("-5"));
    }

    private static byte[] Zip(Action<ZipArchive> fill)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true)) fill(z);
        return ms.ToArray();
    }

    private static void Put(ZipArchive z, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var w = new StreamWriter(z.CreateEntry(name, level).Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static byte[] Minimal(string sheetXml, Action<ZipArchive>? extra = null) => Zip(z =>
    {
        Put(z, "xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Put(z, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"x\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
        Put(z, "xl/worksheets/sheet1.xml", sheetXml);
        extra?.Invoke(z);
    });

    [Fact]
    public void A_decompression_bomb_is_refused_by_size_without_unpacking_it()
    {
        // 60 MB of zeros compress to a few KB: far past the per-part limit.
        var bomb = Minimal($"<worksheet xmlns=\"{Main}\"><sheetData>" + new string(' ', 60 * 1024 * 1024) + "</sheetData></worksheet>");
        Assert.True(bomb.Length < 200_000);
        var ex = Assert.Throws<XlsxFormatException>(() => XlsxReader.ReadFirstSheet(bomb));
        Assert.Contains("large", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_part_that_lies_about_its_size_is_cut_while_reading()
    {
        var limits = new XlsxLimits(MaxEntryBytes: 20_000, MaxTotalBytes: 40_000, MaxRatio: 100_000);
        var sheet = $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>" + new string('a', 30_000) + "</t></is></c></row></sheetData></worksheet>";
        Assert.Throws<XlsxFormatException>(() => XlsxReader.ReadFirstSheet(Minimal(sheet), limits));
    }

    [Fact]
    public void Too_many_parts_macros_external_links_and_non_zip_files_are_refused()
    {
        var sheet = $"<worksheet xmlns=\"{Main}\"><sheetData/></worksheet>";
        Assert.Throws<XlsxFormatException>(() => XlsxReader.ReadFirstSheet(Minimal(sheet, z => { for (var i = 0; i < 80; i++) Put(z, $"x/{i}.xml", "<a/>"); })));
        Assert.Throws<XlsxFormatException>(() => XlsxReader.ReadFirstSheet(Minimal(sheet, z => Put(z, "xl/vbaProject.bin", "x"))));
        Assert.Throws<XlsxFormatException>(() => XlsxReader.ReadFirstSheet(Minimal(sheet, z => Put(z, "xl/externalLinks/externalLink1.xml", "<a/>"))));
        Assert.Throws<XlsxFormatException>(() => XlsxReader.ReadFirstSheet(Encoding.UTF8.GetBytes("a,b\n1,2")));
        Assert.Throws<XlsxFormatException>(() => XlsxReader.ReadFirstSheet([]));
        Assert.Throws<XlsxFormatException>(() => XlsxReader.ReadFirstSheet(Minimal(sheet, z => Put(z, "../evil.xml", "x"))));
    }

    [Fact]
    public void A_dtd_is_prohibited_so_entity_expansion_cannot_run()
    {
        var evil = "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\">]>" +
                   $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>&lol2;</t></is></c></row></sheetData></worksheet>";
        Assert.Throws<XlsxFormatException>(() => XlsxReader.ReadFirstSheet(Minimal(evil)));
    }

    [Fact]
    public void Row_column_and_cell_limits_hold_and_formulas_are_never_evaluated()
    {
        var rows = string.Concat(Enumerable.Range(1, 30).Select(i => $"<row r=\"{i}\"><c r=\"A{i}\" t=\"inlineStr\"><is><t>x</t></is></c></row>"));
        Assert.Throws<XlsxFormatException>(() => XlsxReader.ReadFirstSheet(Minimal($"<worksheet xmlns=\"{Main}\"><sheetData>{rows}</sheetData></worksheet>"), new XlsxLimits(MaxRows: 10)));
        Assert.Throws<XlsxFormatException>(() => XlsxReader.ReadFirstSheet(Minimal($"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\"><c r=\"ZZ1\"><v>1</v></c></row></sheetData></worksheet>")));
        Assert.Throws<XlsxFormatException>(() => XlsxReader.ReadFirstSheet(Minimal($"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>{new string('a', 5000)}</t></is></c></row></sheetData></worksheet>")));
        var f = XlsxReader.ReadFirstSheet(Minimal($"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\"><c r=\"A1\"><f>HYPERLINK(\"x\")</f><v>cached</v></c><c r=\"C1\"><v>3</v></c></row></sheetData></worksheet>"));
        Assert.Equal(["cached", "", "3"], f[0]);
    }

    // ---------------------------------------------------------------- BOM import parsing

    [Fact]
    public void The_import_template_parses_and_every_bad_row_gets_its_own_error()
    {
        var table = XlsxReader.ReadFirstSheet(XlsxWriter.Write([new SheetData("BOM changes", BomExchangeService.TemplateHeader,
        [
            ["add", "P-000001", "P-000002", Cell.N(2), "EA", Cell.N(10), "R1, R2", "ok", ""],
            ["frobnicate", "P-000001", "P-000003", Cell.N(1), "", "", "", "", ""],
            ["add", "", "P-000003", Cell.N(0), "", "", "", "", ""],
            ["update", "P-000001", "P-000004", Cell.N(3), "", Cell.N(999_999), "", "", ""],
            ["remove", "P-000001", "P-000005", "", "", "", "", "", ""],
        ])]));
        var parsed = BomExchangeService.Parse(table);
        Assert.Equal(5, parsed.Rows.Count);
        Assert.Empty(parsed.Rows[0].Errors);
        Assert.Equal(["R1", "R2"], parsed.Rows[0].Designators);
        Assert.Contains("Action must be", parsed.Rows[1].Errors[0], StringComparison.Ordinal);
        Assert.Equal(2, parsed.Rows[2].Errors.Count);
        Assert.Empty(parsed.Rows[3].Errors);
        Assert.Empty(parsed.Rows[4].Errors); // remove needs no quantity
        Assert.Equal(4, parsed.Rows[2].Row); // the sheet's own row number: the header is row 1
    }

    [Fact]
    public void A_file_without_the_template_header_is_refused_with_guidance()
    {
        var parsed = BomExchangeService.Parse([["a", "b"], ["1", "2"]]);
        Assert.Empty(parsed.Rows);
        Assert.Contains("template", parsed.Errors[0], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- PDF marking on every page

    private static ReleasePackageContent ManyLines(int lines)
    {
        var m1 = M(); var m2 = M(DistributionStatement.F, "ATLAS", "PROPIN", ExportJurisdiction.Ear);
        var bom = Enumerable.Range(1, lines).Select(i => new PackageBomLine(1, i.ToString(System.Globalization.CultureInfo.InvariantCulture), i, $"P-{i:D6}", $"Bracket {i}", "A", "Released", 1, "EA", null, i % 2 == 0 ? m1 : m2)).ToList();
        return new ReleasePackageContent("part", "P-000001", "Top assembly", "B",
            [new PackageObject("part", "P-000001", "Top assembly", "B", "Released", m1), .. bom.Select(b => new PackageObject("part", b.Number, b.Name, "A", "Released", b.Markings))],
            bom, [new PackageDocument("D-000001", "Drawing", "Drawing", "A", "Released", "P-000001", m1, [new PackageFile("a.pdf", 1234, new string('a', 64))])],
            [new PackageSignature("P-000001", "B", "Alice Approver", "Approved", "Approved", new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), "Looks good")],
            [new PackageChange("ECN-000001", "ECN", "Revise bracket", "Released", "II", "Corrects a fit problem", ["part P-000002 A -> B (revise)"])]);
    }

    [Fact]
    public void Every_page_of_a_package_carries_the_aggregate_banner_and_the_notices_at_top_and_bottom()
    {
        var content = ManyLines(120);
        var marking = PackageMarking.For([.. content.Objects.Select(o => o.Markings)], MarkingStatements.Notices);
        Assert.Contains("ITAR", marking.Banner, StringComparison.Ordinal);
        Assert.Contains("EAR 9A610", marking.Banner, StringComparison.Ordinal);
        Assert.Contains("DISTRIBUTION F", marking.Banner, StringComparison.Ordinal); // the most restrictive statement of all included objects
        Assert.Contains("CUI//EXPT/PROPIN", marking.Banner, StringComparison.Ordinal);
        Assert.Contains("PROGRAM ATLAS, ORCA", marking.Banner, StringComparison.Ordinal);
        var pdf = ReleasePackagePdf.Render(content, marking, new PackageIdentity("RPK-000001", new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero), "Alice Engineer"), EmbeddedFontResolver.EnsureInstalled);
        using var doc = PdfDocument.Open(pdf);
        Assert.True(doc.NumberOfPages >= 5, $"expected a multi-page package, got {doc.NumberOfPages}");
        foreach (var page in doc.GetPages())
        {
            var text = Regex.Replace(page.Text, @"\s+", " ");
            var occurrences = Regex.Count(text, Regex.Escape("DISTRIBUTION F"));
            Assert.True(occurrences >= 2, $"page {page.Number}: the banner must appear at top and bottom, found {occurrences}");
            Assert.Contains("DISTRIBUTION STATEMENT F", text, StringComparison.Ordinal);
            Assert.Contains("DISTRIBUTION STATEMENT D", text, StringComparison.Ordinal);
            Assert.Contains($"Page {page.Number} of {doc.NumberOfPages}", text, StringComparison.Ordinal);
        }
        var all = string.Join(" ", doc.GetPages().Select(p => p.Text));
        var flat = Regex.Replace(all, @"\s+", "");
        foreach (var needle in new[] { "Release package", "BOM report", "Documents", "Signatures", "Change summary", new string('a', 64), "Alice Approver", "2026-10-01 12:00", "ECN-000001" })
            Assert.Contains(Regex.Replace(needle, @"\s+", ""), flat, StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_of_one_unmarked_distribution_a_object_still_prints_its_banner()
    {
        var m = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;
        var content = new ReleasePackageContent("part", "P-1", "Plain", "A", [new PackageObject("part", "P-1", "Plain", "A", "Released", m)], [], [], [], []);
        var marking = PackageMarking.For([m], MarkingStatements.Notices);
        var pdf = ReleasePackagePdf.Render(content, marking, new PackageIdentity("RPK-000002", DateTimeOffset.UnixEpoch, "x"), EmbeddedFontResolver.EnsureInstalled);
        using var doc = PdfDocument.Open(pdf);
        Assert.Equal("UNCLASSIFIED · DISTRIBUTION A", marking.Banner);
        Assert.True(Regex.Count(doc.GetPage(1).Text, "UNCLASSIFIED") >= 2); // top, bottom, and the cover text
    }

    [Fact]
    public void A_package_with_no_marked_object_cannot_be_marked()
    {
        Assert.Throws<ArgumentException>(() => PackageMarking.For([], MarkingStatements.Notices));
    }

    // ---------------------------------------------------------------- report running

    private sealed class FakeDataset(params (string[] Cells, Markings? M)[] rows) : IReportDataset
    {
        public string Id => "fake";
        public string Title => "Fake";
        public string Description => "";
        public string? ParameterLabel => null;
        public IReadOnlyList<DatasetColumn> Columns { get; } = [new("name", "Name", "text"), new("qty", "Quantity", "number"), new("kind", "Kind", "text"), new("secret", "Secret", "text", Sortable: false, Filterable: false, Groupable: false)];
        public Task<Outcome<DatasetResult>> QueryAsync(Subject s, DatasetQuery q, string? c, CancellationToken ct = default) =>
            Task.FromResult(Outcome<DatasetResult>.Ok(new DatasetResult([.. rows.Select(r => new DatasetRow(r.Cells, r.M))], rows.Length, false)));
    }

    private sealed class NullAudit : IAuditLog
    {
        public int Count;
        public Task<AuditRecord> AppendAsync(Npgsql.NpgsqlTransaction t, AuditEntry e, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AuditRecord> AppendAsync(AuditEntry e, CancellationToken ct = default) { Count++; return Task.FromResult<AuditRecord>(null!); }
    }

    private static Subject User() => new() { Id = "u1", DisplayName = "Alice", Username = "alice", UsPerson = true };

    private static (ReportBuilderService Svc, NullAudit Audit) Builder(IReportDataset ds)
    {
        var audit = new NullAudit();
        return (new ReportBuilderService(null!, audit, TimeProvider.System, [ds], MarkingStatements.Notices, EmbeddedFontResolver.EnsureInstalled), audit);
    }

    private static FakeDataset Data() => new(
        (["bolt", "10", "Standard", "s1"], M()), (["nut", "5", "Standard", "s2"], M(DistributionStatement.F, "ATLAS", "PROPIN")), (["bracket", "2", "Make", "s3"], M()), (["washer", "100", "Standard", "s4"], null));

    [Fact]
    public async Task A_report_filters_sorts_groups_counts_and_shows_the_aggregate_banner_of_the_rows_it_shows()
    {
        var (svc, audit) = Builder(Data());
        var run = await svc.RunAsync(User(), "fake", new ReportSpec(["name", "qty"], [new DatasetFilter("kind", FilterOps.Eq, "standard")], SortColumn: "qty", Descending: true, GroupBy: "kind"), "c1");
        Assert.True(run.IsOk);
        var r = run.Value!;
        Assert.Equal(["washer", "bolt", "nut"], r.Rows.Select(x => x.Cells[0]));
        Assert.Equal(("Standard", 3), (r.Groups[0].Value, r.Groups[0].Count));
        Assert.Contains("DISTRIBUTION F", r.Banner, StringComparison.Ordinal); // the nut is shown
        Assert.Equal(1, audit.Count);
        // Filtering the nut away removes its marking from the banner.
        var run2 = await svc.RunAsync(User(), "fake", new ReportSpec(["name"], [new DatasetFilter("name", FilterOps.Contains, "bolt")]), null);
        Assert.DoesNotContain("DISTRIBUTION F", run2.Value!.Banner, StringComparison.Ordinal);
        // Numbers filter and sort as numbers.
        var run3 = await svc.RunAsync(User(), "fake", new ReportSpec(["name", "qty"], [new DatasetFilter("qty", FilterOps.AtLeast, "10")], SortColumn: "qty"), null);
        Assert.Equal(["bolt", "washer"], run3.Value!.Rows.Select(x => x.Cells[0]));
    }

    [Fact]
    public void Unknown_columns_operators_sort_keys_and_unsortable_columns_are_refused_never_passed_on()
    {
        var (svc, _) = Builder(Data());
        Assert.Contains(svc.Validate("fake", new ReportSpec(["name; DROP TABLE x"])), e => e.Contains("not a column", StringComparison.Ordinal));
        Assert.Contains(svc.Validate("fake", new ReportSpec(["name"], [new DatasetFilter("name", "like", "x")])), e => e.Contains("operator", StringComparison.Ordinal));
        Assert.Contains(svc.Validate("fake", new ReportSpec(["name"], [new DatasetFilter("secret", FilterOps.Eq, "x")])), e => e.Contains("Cannot filter", StringComparison.Ordinal));
        Assert.Contains(svc.Validate("fake", new ReportSpec(["name"], SortColumn: "secret")), e => e.Contains("Cannot sort", StringComparison.Ordinal));
        Assert.Contains(svc.Validate("fake", new ReportSpec(["name"], SortColumn: "1; select")), e => e.Contains("Allowed", StringComparison.Ordinal));
        Assert.Contains(svc.Validate("fake", new ReportSpec(["name"], GroupBy: "secret")), e => e.Contains("Cannot group", StringComparison.Ordinal));
        Assert.Contains(svc.Validate("nope", new ReportSpec(["name"])), e => e.Contains("dataset", StringComparison.Ordinal));
        Assert.Contains(svc.Validate("fake", new ReportSpec(["qty"], [new DatasetFilter("qty", FilterOps.AtLeast, "abc")])), e => e.Contains("not a number", StringComparison.Ordinal));
        Assert.Empty(svc.Validate("fake", new ReportSpec(["name", "qty"], [new DatasetFilter("kind", FilterOps.Contains, "St")], SortColumn: "name", GroupBy: "kind")));
    }

    [Fact]
    public async Task Report_exports_carry_the_banner_in_csv_xlsx_and_pdf_and_neutralize_cells()
    {
        var (svc, _) = Builder(new FakeDataset((["=cmd|' /C calc'!A0", "1", "Standard", "x"], M())));
        foreach (var format in new[] { "csv", "xlsx", "pdf" })
        {
            var f = await svc.ExportAsync(User(), "fake", new ReportSpec(["name", "qty"]), format, null);
            Assert.True(f.IsOk, format);
            Assert.Contains("DISTRIBUTION D", f.Value!.Banner, StringComparison.Ordinal);
            if (format == "csv") { var t = Encoding.UTF8.GetString(f.Value.Content); Assert.Contains("DISTRIBUTION D", t, StringComparison.Ordinal); Assert.Contains("'=cmd", t, StringComparison.Ordinal); }
            if (format == "xlsx") Assert.Equal("=cmd|' /C calc'!A0", XlsxReader.ReadFirstSheet(f.Value.Content)[3][0]);
            if (format == "pdf") { using var d = PdfDocument.Open(f.Value.Content); Assert.True(Regex.Count(d.GetPage(1).Text, "DISTRIBUTION D") >= 2); }
        }
        Assert.False((await svc.ExportAsync(User(), "fake", new ReportSpec(["name"]), "docx", null)).IsOk);
    }

    // ---------------------------------------------------------------- structure rules

    [GeneratedRegex(@"""(output\.[a-z_]+\.[a-z_]+)""")]
    private static partial Regex ActionLiteral();

    [Fact]
    public void The_module_reads_no_marked_table_and_its_actions_all_use_the_output_prefix()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src", "Keel.Reports"), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"(FROM|JOIN|INTO|UPDATE)\s+(\w+)\.(\w+)", RegexOptions.IgnoreCase))
                if (m.Groups[2].Value is not ("rpt")) offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
            foreach (Match m in Regex.Matches(text, @"AuditEntry|ReportsAudit\.Entry\([^,]+,\s*""([^""]+)""")) if (m.Groups[1].Success && !m.Groups[1].Value.StartsWith("output.", StringComparison.Ordinal)) offenders.Add($"{Path.GetFileName(file)}: action {m.Groups[1].Value}");
        }
        Assert.True(offenders.Count == 0, "Keel.Reports may only touch rpt.* tables and output.* actions:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_migration_keeps_artifacts_immutable_and_has_no_marking_columns_and_one_registered_hash_column()
    {
        var sql = File.ReadAllText(Path.Combine(Root, "db", "migrations", "0027_reports.sql"));
        Assert.Contains("BEFORE UPDATE OR DELETE ON rpt.artifact", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("export_jurisdiction", sql, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(sql, @"(?im)^\s*sha256\s"));
        Assert.Contains(Keel.Documents.Vault.VaultReferences.Sources, s => s is { Schema: "rpt", Table: "artifact", Column: "sha256" });
        Assert.DoesNotContain("DROP CONSTRAINT", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_endpoint_of_the_module_requires_a_role_and_the_ui_download_routes_stay_out_of_the_published_document()
    {
        var text = File.ReadAllText(Path.Combine(Root, "src", "Keel.Api", "Endpoints", "ReportsEndpoints.cs"));
        Assert.Contains("MapGroup(\"/api/v1/output\").RequireAuthorization(Policies.AnyPlmRole)", text, StringComparison.Ordinal);
        var starts = Regex.Matches(text, @"app\.MapGet\(Ui").Select(m => m.Index).ToList();
        Assert.Equal(4, starts.Count);
        foreach (var at in starts)
        {
            var end = text.IndexOf("ExcludeFromDescription", at, StringComparison.Ordinal);
            Assert.True(end > 0 && end - at < 1500 && !text[at..end].Contains("g.Map", StringComparison.Ordinal), "A UI download route must be excluded from the published document.");
        }
    }
}

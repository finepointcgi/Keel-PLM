using System.IO.Compression;
using System.Text;
using Keel.Core;
using Keel.Documents;
using Keel.Documents.Compare;
using Keel.Documents.Marking;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using PdfDocument = PdfSharp.Pdf.PdfDocument;

namespace Keel.UnitTests;

/// <summary>Word diff, text extraction and redline report (increment 9.5, ADR-0053): pure logic, no database.</summary>
public sealed class RedlineUnitTests
{
    private static string Left(TextDiff d) => string.Concat(d.Segments.Where(s => s.Op != DiffOp.Insert).Select(s => s.Text));
    private static string Right(TextDiff d) => string.Concat(d.Segments.Where(s => s.Op != DiffOp.Delete).Select(s => s.Text));
    private static string Norm(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ------------------------------------------------------------------ word diff

    [Fact]
    public void Identical_texts_have_no_changes_and_full_similarity()
    {
        var d = WordDiff.Compare("The bracket is 4 mm thick.\nSecond line.", "The bracket is 4 mm thick.\nSecond line.")!;
        Assert.False(d.HasChanges);
        Assert.Equal(1.0, d.Counts.Similarity);
        Assert.Equal(0, d.Counts.InsertedWords + d.Counts.DeletedWords);
        Assert.Single(d.Segments);
    }

    [Fact]
    public void One_replaced_word_is_one_change_with_a_deletion_and_an_insertion()
    {
        var d = WordDiff.Compare("The wall is 4 mm thick.", "The wall is 6 mm thick.")!;
        var change = Assert.Single(d.Changes);
        Assert.Equal("replace", change.Kind);
        Assert.Equal(1, change.DeletedWords);
        Assert.Equal(1, change.InsertedWords);
        Assert.Equal("4", change.DeletedPreview);
        Assert.Equal("6", change.InsertedPreview);
        Assert.Equal(1, d.Counts.Changes);
        // A deletion is always shown before the insertion that replaces it, and both belong to change 0.
        var changed = d.Segments.Where(s => s.Op != DiffOp.Equal).ToList();
        Assert.Equal([DiffOp.Delete, DiffOp.Insert], changed.Select(s => s.Op));
        Assert.All(changed, s => Assert.Equal(0, s.Change));
    }

    [Fact]
    public void Insertions_and_deletions_are_counted_in_words_and_numbered_in_order()
    {
        var d = WordDiff.Compare("alpha beta gamma delta epsilon", "alpha beta NEW WORDS gamma epsilon")!;
        Assert.Equal(2, d.Counts.Changes);
        Assert.Equal("insert", d.Changes[0].Kind);
        Assert.Equal("delete", d.Changes[1].Kind);
        Assert.Equal(2, d.Counts.InsertedWords);
        Assert.Equal(1, d.Counts.DeletedWords);
        Assert.Equal([0, 1], d.Changes.Select(c => c.Index));
    }

    [Fact]
    public void Whitespace_and_line_wrapping_alone_are_not_changes()
    {
        var d = WordDiff.Compare("The quick brown fox\njumps over   the lazy dog", "The quick  brown\nfox jumps over the lazy dog\n\n")!;
        Assert.False(d.HasChanges);
    }

    [Fact]
    public void Punctuation_is_its_own_token_so_a_changed_full_stop_does_not_replace_a_word()
    {
        var d = WordDiff.Compare("Torque to 25 Nm.", "Torque to 25 Nm;")!;
        var change = Assert.Single(d.Changes);
        Assert.Equal(0, change.DeletedWords);
        Assert.Equal(0, change.InsertedWords);
        Assert.Equal(".", change.DeletedPreview);
        Assert.Equal(";", change.InsertedPreview);
    }

    [Fact]
    public void Numbers_with_decimal_points_and_contractions_stay_whole_tokens()
    {
        var d = WordDiff.Compare("Use 3.14 and don't stop", "Use 3.15 and don't stop")!;
        var change = Assert.Single(d.Changes);
        Assert.Equal("3.14", change.DeletedPreview);
        Assert.Equal("3.15", change.InsertedPreview);
    }

    [Fact]
    public void Each_side_is_restored_from_the_segments()
    {
        const string a = "Section 1\nThe pod shall survive 40 g.\nSection 2\nMaterial is 6061-T6.";
        const string b = "Section 1\nThe pod shall survive 60 g shock loads.\nSection 3\nMaterial is 7075-T6.\nNew paragraph.";
        var d = WordDiff.Compare(a, b)!;
        Assert.Equal(Norm(a), Norm(Left(d)));
        Assert.Equal(Norm(b), Norm(Right(d)));
        Assert.True(d.Counts.Changes >= 3);
    }

    [Fact]
    public void Offsets_point_into_the_restored_text_of_each_side()
    {
        var d = WordDiff.Compare("one two three four", "one two TWO-AND-A-HALF three four")!;
        var c = Assert.Single(d.Changes);
        Assert.StartsWith(" TWO", Right(d)[c.RightOffset..]);
        Assert.True(c.LeftOffset <= Left(d).Length);
    }

    [Fact]
    public void The_result_is_deterministic_and_digests_identify_changes()
    {
        const string a = "red green blue yellow";
        const string b = "red GREEN blue orange";
        var one = WordDiff.Compare(a, b)!;
        var two = WordDiff.Compare(a, b)!;
        Assert.Equal(one.Changes.Select(c => c.Digest), two.Changes.Select(c => c.Digest));
        Assert.Equal(2, one.Changes.Count);
        Assert.NotEqual(one.Changes[0].Digest, one.Changes[1].Digest);
        Assert.All(one.Changes, c => Assert.Matches("^[0-9a-f]{64}$", c.Digest));
        // The same edit in another place has the same digest: the digest is about the change, not its position.
        var elsewhere = WordDiff.Compare("x red green blue yellow", "x red GREEN blue orange")!;
        Assert.Equal(one.Changes.Select(c => c.Digest), elsewhere.Changes.Select(c => c.Digest));
    }

    [Fact]
    public void Myers_matches_a_dynamic_programming_edit_distance_on_random_input()
    {
        var rng = new Random(42);
        for (var round = 0; round < 200; round++)
        {
            var a = Enumerable.Range(0, rng.Next(0, 40)).Select(_ => rng.Next(0, 5)).ToArray();
            var b = Enumerable.Range(0, rng.Next(0, 40)).Select(_ => rng.Next(0, 5)).ToArray();
            var script = WordDiff.Myers(a, b, 4000)!;
            Assert.NotNull(script);
            var edits = script.Count(e => e.Op != DiffOp.Equal);
            var lcs = new int[a.Length + 1, b.Length + 1];
            for (var i = a.Length - 1; i >= 0; i--)
                for (var j = b.Length - 1; j >= 0; j--)
                    lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            Assert.Equal(a.Length + b.Length - 2 * lcs[0, 0], edits);
            // Replaying the script on a reproduces b.
            var replay = script.Where(e => e.Op != DiffOp.Delete).Select(e => b[e.B]).ToArray();
            Assert.Equal(b, replay);
            Assert.Equal(a, script.Where(e => e.Op != DiffOp.Insert).Select(e => a[e.A]).ToArray());
        }
    }

    [Fact]
    public void A_stretch_that_is_too_different_is_shown_as_one_replacement_and_marked_coarse()
    {
        var a = string.Join(' ', Enumerable.Range(0, 5000).Select(i => "a" + i));
        var b = string.Join(' ', Enumerable.Range(0, 5000).Select(i => "b" + i));
        var d = WordDiff.Compare(a, b)!;
        Assert.True(d.Coarse);
        var c = Assert.Single(d.Changes);
        Assert.Equal(5000, c.DeletedWords);
        Assert.Equal(5000, c.InsertedWords);
        Assert.Equal(Norm(a), Norm(Left(d)));
        Assert.Equal(Norm(b), Norm(Right(d)));
    }

    [Fact]
    public void Long_documents_with_few_edits_are_fast_and_exact()
    {
        var lines = Enumerable.Range(0, 30_000).Select(i => $"Requirement {i}: the unit shall operate at {i % 90} degrees.").ToList();
        var edited = new List<string>(lines);
        edited[100] = "Requirement 100: the unit shall NOT operate.";
        edited.RemoveAt(15_000);
        edited.Insert(29_000, "An added requirement.");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var d = WordDiff.Compare(string.Join('\n', lines), string.Join('\n', edited))!;
        watch.Stop();
        Assert.Equal(4, d.Counts.Changes); // the edited line is two word-level changes (an insertion and a deletion), plus one removed and one added line
        Assert.False(d.Coarse);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
    }

    [Fact]
    public void A_text_with_too_many_tokens_is_refused_not_guessed()
    {
        var big = new string('x', 1) + string.Concat(Enumerable.Repeat(" a", WordDiff.MaxTokensPerSide + 10));
        Assert.Null(WordDiff.Compare(big, "short"));
    }

    [Fact]
    public void Surrogate_pairs_are_never_split()
    {
        var d = WordDiff.Compare("go \U0001F600 now", "go \U0001F601 now")!;
        var c = Assert.Single(d.Changes);
        Assert.Equal("\U0001F600", c.DeletedPreview);
        Assert.Equal("\U0001F601", c.InsertedPreview);
    }

    // ------------------------------------------------------------------ extraction

    [Fact]
    public void Plain_text_markdown_and_utf16_are_read_and_binary_is_refused()
    {
        Assert.Equal("a\nb", TextExtractor.Extract("n.txt", "text/plain", "a\r\nb"u8.ToArray()).Text);
        Assert.Equal("markdown", TextExtractor.Extract("n.md", null, "# T"u8.ToArray()).Format);
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("hello é")).ToArray();
        Assert.Equal("hello é", TextExtractor.Extract("n.txt", null, utf16).Text);
        var bom = Encoding.UTF8.GetPreamble().Concat("bom"u8.ToArray()).ToArray();
        Assert.Equal("bom", TextExtractor.Extract("n.txt", null, bom).Text);
        var binary = TextExtractor.Extract("n.txt", "text/plain", [65, 0, 66]);
        Assert.False(binary.IsText);
        Assert.Contains("binary", binary.Reason);
        var invalid = TextExtractor.Extract("n.txt", "text/plain", [0xC3, 0x28]);
        Assert.False(invalid.IsText);
        Assert.Contains("UTF-8", invalid.Reason);
    }

    [Fact]
    public void Other_formats_say_they_are_not_comparable_and_old_office_formats_name_themselves()
    {
        var step = TextExtractor.Extract("part.stp", "application/step", [1, 2, 3]);
        Assert.False(step.IsText);
        Assert.Equal("other", step.Format);
        Assert.Contains("not text", step.Reason);
        Assert.Contains(".doc files", TextExtractor.Extract("old.doc", null, [1]).Reason);
    }

    private static byte[] Zip(params (string Name, string Content)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in parts)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        return ms.ToArray();
    }

    private const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";

    [Fact]
    public void Docx_text_is_read_by_paragraph_with_tabs_and_breaks_and_tracked_deletions_are_left_out()
    {
        var docx = Zip(("word/document.xml",
            $"<w:document {W}><w:body><w:p><w:r><w:t>Scope</w:t></w:r></w:p><w:p><w:r><w:t>One</w:t><w:tab/><w:t>two</w:t><w:br/><w:t>three</w:t></w:r><w:del><w:r><w:delText>gone</w:delText></w:r></w:del></w:p></w:body></w:document>"));
        var r = TextExtractor.Extract("spec.docx", null, docx);
        Assert.True(r.IsText, r.Reason);
        Assert.Equal("Scope\nOne\ttwo\nthree", r.Text);
    }

    [Fact]
    public void A_damaged_or_hostile_docx_is_not_comparable_and_never_throws()
    {
        Assert.False(TextExtractor.Extract("a.docx", null, "PK nonsense"u8.ToArray()).IsText);
        Assert.False(TextExtractor.Extract("a.docx", null, Zip(("other.xml", "<x/>"))).IsText);
        // A DTD with an entity is refused (no expansion).
        var xxe = Zip(("word/document.xml", "<!DOCTYPE d [<!ENTITY e \"boom\">]><w:document " + W + "><w:body><w:p><w:r><w:t>&e;</w:t></w:r></w:p></w:body></w:document>"));
        var r = TextExtractor.Extract("a.docx", null, xxe);
        Assert.False(r.IsText);
        Assert.Contains("could not be read", r.Reason);
    }

    [Fact]
    public void A_zip_bomb_docx_is_cut_off_at_the_limit()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("word/document.xml", CompressionLevel.SmallestSize).Open());
            w.Write($"<w:document {W}><w:body>");
            var chunk = new string('a', 1 << 20);
            for (var i = 0; i < 90; i++) w.Write($"<w:p><w:r><w:t>{chunk}</w:t></w:r></w:p>");
            w.Write("</w:body></w:document>");
        }
        var r = TextExtractor.Extract("bomb.docx", null, ms.ToArray());
        Assert.False(r.IsText);
    }

    [Fact]
    public void Xlsx_cells_are_listed_by_sheet_with_shared_strings_inline_strings_formulas_and_booleans()
    {
        const string ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        var xlsx = Zip(
            ("xl/workbook.xml", $"<workbook {ns}><sheets><sheet name=\"BOM\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Notes\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Target=\"worksheets/sheet2.xml\"/></Relationships>"),
            ("xl/sharedStrings.xml", $"<sst {ns}><si><t>Bracket</t></si><si><r><t>Bolt </t></r><r><t>M6</t></r></si></sst>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet {ns}><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\"><v>4</v></c><c r=\"C1\"><f>B1*2</f><v>8</v></c></row><row r=\"2\"><c r=\"A2\" t=\"s\"><v>1</v></c><c r=\"B2\" t=\"b\"><v>1</v></c></row></sheetData></worksheet>"),
            ("xl/worksheets/sheet2.xml", $"<worksheet {ns}><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>inline</t></is></c></row></sheetData></worksheet>"));
        var r = TextExtractor.Extract("bom.xlsx", null, xlsx);
        Assert.True(r.IsText, r.Reason);
        Assert.Equal("[Sheet: BOM]\nBOM!A1: Bracket\nBOM!B1: 4\nBOM!C1: =B1*2 = 8\nBOM!A2: Bolt M6\nBOM!B2: TRUE\n[Sheet: Notes]\nNotes!A1: inline", r.Text);
    }

    private static byte[] Pdf(params string[] pages)
    {
        EmbeddedFontResolver.EnsureInstalled();
        using var doc = new PdfDocument();
        var font = new XFont(EmbeddedFontResolver.FamilyName, 12);
        foreach (var text in pages)
        {
            using var g = XGraphics.FromPdfPage(doc.AddPage());
            var y = 72.0;
            foreach (var line in text.Split('\n')) { g.DrawString(line, font, XBrushes.Black, new XPoint(72, y)); y += 16; }
        }
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    [Fact]
    public void Pdf_text_is_read_per_page_and_line_wrapping_is_not_a_change()
    {
        var a = TextExtractor.Extract("a.pdf", "application/pdf", Pdf("The housing shall be\nanodized black.", "Page two text."));
        var b = TextExtractor.Extract("b.pdf", "application/pdf", Pdf("The housing shall be anodized\nblack.", "Page two text."));
        Assert.True(a.IsText, a.Reason);
        Assert.Contains("anodized black.", a.Text);
        Assert.Contains("Page two text.", a.Text);
        Assert.False(WordDiff.Compare(a.Text!, b.Text!)!.HasChanges);
    }

    [Fact]
    public void A_pdf_without_a_text_layer_or_with_garbage_says_so()
    {
        EmbeddedFontResolver.EnsureInstalled();
        using var doc = new PdfDocument();
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        var blank = TextExtractor.Extract("scan.pdf", "application/pdf", ms.ToArray());
        Assert.False(blank.IsText);
        Assert.Contains("no text layer", blank.Reason);
        var junk = TextExtractor.Extract("junk.pdf", "application/pdf", "%PDF-1.4 this is not a pdf"u8.ToArray());
        Assert.False(junk.IsText);
        Assert.Contains("could not be read", junk.Reason);
        Assert.Contains("does not start like a PDF", TextExtractor.Extract("x.pdf", null, "hello world"u8.ToArray()).Reason);
    }

    // ------------------------------------------------------------------ report

    private static readonly Markings Itar = Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"],
        controllingOffice: "ACME", distributionReason: "Critical technology", program: "ORCA").Value;

    private static DocumentComparison Comparison(string left, string right, bool withThread = true)
    {
        var diff = WordDiff.Compare(left, right)!;
        CompareSide Side(string number, string label) => new(number, "Spec " + number, Guid.NewGuid(), label, "InWork", DateTimeOffset.UnixEpoch, "alice", null, "spec.txt", "text/plain", 10, new string('a', 64), Itar.BannerText(), "text");
        IReadOnlyList<RedlineThread> threads = withThread && diff.HasChanges
            ? [new RedlineThread(Guid.NewGuid(), 0, diff.Changes[0].Kind, false, DateTimeOffset.UnixEpoch, "Alice <script>", "Why <b>this</b>?", false, null, null, [new RedlineReply(Guid.NewGuid(), "Bob", DateTimeOffset.UnixEpoch, "Because.")], true)]
            : [];
        return new DocumentComparison(Side("D-000001", "A.1"), Side("D-000002", "A.2"), CompareModes.Text, false, false, null, DocumentService.AggregateBanner([Itar]), DocumentService.AggregateNotices([Itar]),
            diff, [new MetadataRow("Version", "A.1", "A.2", false), new MetadataRow("SHA-256", new string('a', 64), new string('b', 64), false)], Guid.NewGuid(), threads, true);
    }

    [Fact]
    public void The_html_report_carries_both_banners_every_notice_the_counts_and_marks_changes_by_more_than_colour()
    {
        var c = Comparison("The wall is 4 mm thick.", "The wall is 6 mm thick and anodized.");
        var html = Encoding.UTF8.GetString(RedlineReport.Html(c, new RedlineIdentity("alice", DateTimeOffset.UnixEpoch)));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(html, "class=\"banner "));
        Assert.Contains(System.Net.WebUtility.HtmlEncode(c.Banner), html);
        foreach (var n in c.Notices) Assert.Contains(System.Net.WebUtility.HtmlEncode(n), html);
        Assert.Contains("<ins ", html);
        Assert.Contains("<del ", html);
        Assert.Contains("text-decoration: underline", html);
        Assert.Contains("text-decoration: line-through", html);
        Assert.Contains("\"[+ \"", html);
        Assert.Contains("Inserted words", html);
        Assert.Contains("SHA-256", html);
        Assert.DoesNotContain("<script", html.Replace("&lt;script", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Alice &lt;script&gt;", html);
        Assert.DoesNotContain("http://", html.Replace("http://www.w3.org", ""), StringComparison.Ordinal); // self-contained: no external resources
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_pdf_report_carries_the_banner_and_notices_on_every_page_and_the_markers_in_the_text()
    {
        var words = string.Join(' ', Enumerable.Range(0, 2500).Select(i => "word" + i));
        var c = Comparison(words, words.Replace("word10 ", "CHANGED ", StringComparison.Ordinal).Replace("word2000 ", "", StringComparison.Ordinal));
        EmbeddedFontResolver.EnsureInstalled();
        var pdf = RedlineReport.Pdf(c, new RedlineIdentity("alice", DateTimeOffset.UnixEpoch));
        using var doc = PdfPigDocument.Open(pdf);
        Assert.True(doc.NumberOfPages >= 2, "needs several pages to prove every page is marked");
        foreach (var page in doc.GetPages())
        {
            Assert.True(System.Text.RegularExpressions.Regex.Count(page.Text, System.Text.RegularExpressions.Regex.Escape("DISTRIBUTION D")) >= 2, $"banner at top and bottom of page {page.Number}");
            Assert.Contains("Page " + page.Number + " of " + doc.NumberOfPages, page.Text);
        }
        var all = string.Concat(doc.GetPages().Select(p => p.Text));
        Assert.Contains("[+", all);
        Assert.Contains("+]", all);
        Assert.Contains("Redline report", all);
    }

    [Fact]
    public void Reports_of_files_that_are_not_compared_say_so_honestly()
    {
        var c = Comparison("a", "b") with { Mode = CompareModes.Metadata, Diff = null, NotComparableReason = "Content not comparable: D-000001 is not a format Keel reads as text." };
        var html = Encoding.UTF8.GetString(RedlineReport.Html(c, new RedlineIdentity("alice", DateTimeOffset.UnixEpoch)));
        Assert.Contains("Content not comparable", html);
        Assert.DoesNotContain("<ins ", html);
        EmbeddedFontResolver.EnsureInstalled();
        using var doc = PdfPigDocument.Open(RedlineReport.Pdf(c, new RedlineIdentity("alice", DateTimeOffset.UnixEpoch)));
        Assert.Contains("Content not comparable", string.Concat(doc.GetPages().Select(p => p.Text)));
    }

    [Fact]
    public void Aggregate_banner_and_notices_cover_both_documents()
    {
        var open = Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;
        var banner = DocumentService.AggregateBanner([open, Itar]);
        Assert.Contains("ITAR", banner);
        Assert.Contains("DISTRIBUTION D", banner);
        Assert.Contains("PROGRAM ORCA", banner);
        var notices = DocumentService.AggregateNotices([open, Itar]);
        Assert.Contains(notices, n => n.Contains("DISTRIBUTION STATEMENT D"));
        Assert.Contains(notices, n => n.Contains("DISTRIBUTION STATEMENT A"));
        Assert.Equal(notices.Count, notices.Distinct().Count());
    }
}

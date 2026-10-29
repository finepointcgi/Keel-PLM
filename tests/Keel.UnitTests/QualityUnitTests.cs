using System.Text.RegularExpressions;
using Keel.Core;
using Keel.Notifications;
using Keel.Quality;
using Keel.Web.Components.Pages.Quality;
using Keel.Web.Services;
using Keel.Workspace;

namespace Keel.UnitTests;

/// <summary>Quality processes (8.1, ADR-0036): the rules that need no database.</summary>
public sealed partial class QualityUnitTests
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

    // ---------------------------------------------------------------- kinds are known everywhere

    [Fact]
    public void The_three_kinds_are_awareness_kinds_workspace_kinds_and_audit_object_types()
    {
        foreach (var kind in QualityKinds.All)
        {
            Assert.True(AwarenessKinds.IsKnown(kind));
            Assert.True(ObjectKinds.IsKnown(kind));
            Assert.Equal(kind, AwarenessKinds.FromAuditObjectType(kind));
            Assert.Equal(AwarenessKinds.Path(kind, "X-1"), ObjectKinds.Path(kind, "X-1"));
            Assert.StartsWith("quality/", AwarenessKinds.Path(kind, "X-1"), StringComparison.Ordinal);
            Assert.Equal(kind, AwarenessKinds.AuditObjectType(kind, "X-1"));
            Assert.False(string.IsNullOrWhiteSpace(AwarenessKinds.Label(kind)));
        }
        Assert.Equal("quality/ncrs/NCR-000001", AwarenessKinds.Path(AwarenessKinds.Ncr, "NCR-000001"));
        Assert.Equal("review", AwarenessKinds.ReviewTab(AwarenessKinds.Capa)); // the review tab of every quality page has this id
    }

    [Fact]
    public void Numbers_tables_and_lifecycle_families_follow_the_kind()
    {
        Assert.Equal(["NCR", "CAPA", "DEV"], QualityKinds.All.Select(QualityKinds.Prefix));
        Assert.Equal(["qual.ncr", "qual.capa", "qual.deviation"], QualityKinds.All.Select(QualityKinds.Table));
        Assert.Equal(["ncr-default", "capa-default", "deviation-default"], QualityKinds.All.Select(QualityKinds.Family));
        Assert.Equal("reporter", QualityKinds.Raiser(QualityKinds.Ncr));
        Assert.Equal("requester", QualityKinds.Raiser(QualityKinds.Capa));
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityKinds.Table("part"));
    }

    [Fact]
    public void The_route_segments_map_both_ways_and_unknown_segments_map_to_nothing()
    {
        foreach (var kind in QualityKinds.All) Assert.Equal(kind, QualityUi.KindOfSegment(QualityUi.Segment(kind)));
        Assert.Null(QualityUi.KindOfSegment("parts"));
        Assert.Null(QualityUi.KindOfSegment(null));
        Assert.All(QualityKinds.All, k => Assert.Equal("quality/" + QualityUi.Segment(k), QualityKinds.ListHref(k)));
    }

    // ---------------------------------------------------------------- sort keys are a whitelist

    [Fact]
    public void Sort_keys_are_looked_up_and_never_reach_the_text_of_a_statement()
    {
        foreach (var kind in QualityKinds.All)
        {
            var map = QualityQueries.SortKeysFor(kind);
            Assert.Contains("number", map.Keys);
            Assert.Contains("state", map.Keys);
            Assert.True(map.TryResolve("number", "desc", out var spec, out _));
            Assert.Equal(new SortSpec("number", true), spec);
            Assert.False(map.TryResolve("c.number; DROP TABLE qual.ncr", "asc", out _, out var error));
            Assert.Contains("Cannot sort by", error, StringComparison.Ordinal);
            Assert.False(map.TryResolve("number", "sideways", out _, out _));
            Assert.Equal("c.created_at DESC, c.number DESC", map.OrderBy(null, "c.created_at DESC, c.number DESC", "c.number"));
        }
        Assert.Contains("severity", QualityQueries.SortKeysFor(QualityKinds.Ncr).Keys);
        Assert.Contains("owner", QualityQueries.SortKeysFor(QualityKinds.Capa).Keys);
        Assert.Contains("window", QualityQueries.SortKeysFor(QualityKinds.Deviation).Keys);
    }

    // ---------------------------------------------------------------- deviation scope rules

    [Fact]
    public void A_scope_with_no_restriction_covers_every_unit()
    {
        Assert.True(DeviationService.Covers(null, null, [], [], null, null));
        Assert.True(DeviationService.Covers(null, null, [], [], "SN-77", "L-1"));
        Assert.Equal("Every unit", DeviationService.DescribeScope(null, null, null, [], [], null));
    }

    [Theory]
    [InlineData("101", true)]
    [InlineData("120", true)]
    [InlineData("100", false)]
    [InlineData("121", false)]
    [InlineData("SN-5", false)]
    [InlineData(null, false)]
    public void A_serial_range_covers_numeric_serials_inside_it_only(string? serial, bool expected) =>
        Assert.Equal(expected, DeviationService.Covers(101, 120, [], [], serial, null));

    [Fact]
    public void Listed_serials_and_lots_are_exact_matches_and_a_restriction_cannot_be_tested_without_the_value()
    {
        Assert.True(DeviationService.Covers(null, null, ["SN-5", "SN-9"], [], "SN-9", null));
        Assert.False(DeviationService.Covers(null, null, ["SN-5", "SN-9"], [], "SN-99", null));
        Assert.False(DeviationService.Covers(null, null, ["SN-5"], [], null, null)); // restricted by serial, no serial given
        Assert.True(DeviationService.Covers(null, null, [], ["L-1", "L-2"], null, "L-2"));
        Assert.False(DeviationService.Covers(null, null, [], ["L-1"], "SN-1", "L-9"));
        Assert.False(DeviationService.Covers(null, null, [], ["L-1"], "SN-1", null));
        // both dimensions must hold
        Assert.False(DeviationService.Covers(1, 10, [], ["L-1"], "5", "L-2"));
        Assert.True(DeviationService.Covers(1, 10, [], ["L-1"], "5", "L-1"));
        // an open-ended range
        Assert.True(DeviationService.Covers(50, null, [], [], "5000", null));
        Assert.False(DeviationService.Covers(null, 50, [], [], "51", null));
    }

    [Fact]
    public void A_scope_is_described_in_words()
    {
        Assert.Equal("revision B; serials 101 to 120; lot L-7; up to 25 units", DeviationService.DescribeScope("B", 101, 120, [], ["L-7"], 25));
        Assert.Equal("serials 50 and up", DeviationService.DescribeScope(null, 50, null, [], [], null));
        Assert.Equal("serial 7", DeviationService.DescribeScope(null, 7, 7, [], [], null));
        Assert.Equal("8 listed serials", DeviationService.DescribeScope(null, null, null, Enumerable.Range(1, 8).Select(i => "S" + i).ToArray(), [], null));
    }

    // ---------------------------------------------------------------- vocabularies

    [Fact]
    public void The_five_dispositions_and_their_labels_are_complete()
    {
        Assert.Equal(["UseAsIs", "Rework", "Repair", "Scrap", "ReturnToSupplier"], NcrDispositions.All);
        Assert.Equal("Use as is", NcrDispositions.Label("UseAsIs"));
        Assert.Equal("Return to supplier", NcrDispositions.Label("ReturnToSupplier"));
        Assert.Equal("Not proposed", NcrDispositions.Label(null));
        Assert.Equal(6, CapaOptions.Categories.Count); // the fishbone categories
        Assert.Equal(8, CapaOptions.MaxWhys);
    }

    [Fact]
    public void Settings_are_validated()
    {
        Assert.Empty(new QualityOptions().Validate());
        Assert.NotEmpty(new QualityOptions { MaintenanceInterval = TimeSpan.FromSeconds(5) }.Validate());
        Assert.NotEmpty(new QualityOptions { ExpiryWarningDays = 0 }.Validate());
        Assert.NotEmpty(new QualityOptions { ExpiryWarningDays = 91 }.Validate());
    }

    // ---------------------------------------------------------------- activity feed and notifications know the module

    [GeneratedRegex(@"""((?:ncr|capa|deviation|quality)\.[a-z_]+)""")]
    private static partial Regex ActionLiteral();

    [GeneratedRegex(@"(?:kind|Kind)\s*\+\s*""\.([a-z_]+)""")]
    private static partial Regex ActionSuffix();

    [Fact]
    public void Every_audit_action_of_the_module_is_a_feed_event_a_view_or_hidden_on_purpose()
    {
        var actions = new SortedSet<string>(StringComparer.Ordinal);
        var suffixes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src", "Keel.Quality"), "*.cs").Concat(Directory.EnumerateFiles(Path.Combine(Root, "src", "Keel.Api", "Endpoints"), "Quality*.cs")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in ActionLiteral().Matches(text)) actions.Add(m.Groups[1].Value);
            foreach (Match m in ActionSuffix().Matches(text)) suffixes.Add(m.Groups[1].Value);
        }
        Assert.True(actions.Count >= 10, $"Only {actions.Count} literal actions found; the pattern is probably broken.");
        Assert.True(suffixes.Count >= 5, $"Only {suffixes.Count} action suffixes found; the pattern is probably broken.");
        var unknown = actions.Where(a => !ActivityCatalog.IsKnown(a)).ToList();
        Assert.True(unknown.Count == 0, "Quality actions the activity feed does not know:\n" + string.Join("\n", unknown));
        // kind + ".suffix": the suffix must be known for at least the kinds that use it, and for the three kinds' shared ones for all of them
        var shared = new[] { "create", "update", "markings_change", "submit", "review_cancel", "review_reject", "link_add", "link_remove" };
        foreach (var s in shared) foreach (var kind in QualityKinds.All) Assert.True(ActivityCatalog.IsKnown(kind + "." + s), $"{kind}.{s} is not in the catalog");
        foreach (var s in suffixes) Assert.True(QualityKinds.All.Any(k => ActivityCatalog.IsKnown(k + "." + s)), $"No kind has a catalog entry for '.{s}'");
        foreach (var kind in new[] { QualityKinds.Ncr, QualityKinds.Capa })
            foreach (var s in new[] { "action_create", "action_start", "action_complete", "action_cancel", "action_overdue" }) Assert.True(ActivityCatalog.IsKnown(kind + "." + s));
        foreach (var kind in QualityKinds.All) { Assert.True(ActivityCatalog.IsKnown(kind + ".read")); Assert.True(ActivityCatalog.IsKnown(kind + ".search")); }
    }

    [Fact]
    public void The_new_notification_kinds_are_registered_with_pointers_that_land_on_the_right_tab()
    {
        foreach (var code in new[] { NotificationKindCodes.QualityActionAssigned, NotificationKindCodes.QualityActionOverdue, NotificationKindCodes.DeviationExpiring, NotificationKindCodes.DeviationExpired })
            Assert.NotNull(NotificationKinds.Find(code));
        Assert.False(NotificationKinds.Find(NotificationKindCodes.QualityActionAssigned)!.CanDisable); // an assigned action cannot be missed
        Assert.False(NotificationKinds.Find(NotificationKindCodes.QualityActionOverdue)!.CanDisable);
        Assert.True(NotificationKinds.Find(NotificationKindCodes.DeviationExpired)!.CanDisable);
        Assert.Equal("quality/capas/CAPA-000001?tab=actions", NotificationKinds.Href(NotificationKinds.Find(NotificationKindCodes.QualityActionAssigned)!, "capa", "CAPA-000001", null));
        Assert.Equal("quality/ncrs/NCR-000002#review", NotificationKinds.Href(NotificationKinds.Find(NotificationKindCodes.ReviewApproved)!, "ncr", "NCR-000002", null));
        // an expiry notice carries no marked text: its sentence names nobody and no object
        Assert.DoesNotContain("{actor}", NotificationKinds.Find(NotificationKindCodes.DeviationExpired)!.Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_prefixes_and_labels_for_the_new_kinds_exist()
    {
        Assert.Equal([ObjectKinds.Ncr], ParsedQuery.Parse("ncr: burr").Kinds);
        Assert.Equal([ObjectKinds.Capa], ParsedQuery.Parse("capa: torque").Kinds);
        Assert.Equal([ObjectKinds.Deviation], ParsedQuery.Parse("dev: finish").Kinds);
        Assert.Contains(PaletteActions.For([Roles.Viewer], false), a => a.Href == "quality/ncrs");
        Assert.DoesNotContain(PaletteActions.For([Roles.Viewer], false), a => a.Id.StartsWith("new:ncr", StringComparison.Ordinal)); // creating needs a role
        Assert.Contains(PaletteActions.For([Roles.Author], false), a => a.Id == "new:ncr");
        Assert.Contains(NavModel.For([Roles.Viewer]).SelectMany(g => g.Items), i => i.Href == "quality/deviations");
    }
}

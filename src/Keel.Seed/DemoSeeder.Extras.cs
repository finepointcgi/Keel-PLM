using System.Text.Json.Nodes;
using Keel.Admin;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Integration;
using Keel.Workflow;

namespace Keel.Seed;

/// <summary>What the second half of the demo seed created (configuration management, manufacturing, integration, classification).</summary>
public sealed record DemoExtras
{
    public required IReadOnlyList<string> ClassCodes { get; init; }
    public required int ClassifiedParts { get; init; }
    public required string DesignBaseline { get; init; }
    public required string ReleaseBaseline { get; init; }
    public required string EffectivityContext { get; init; }
    /// <summary>The in-work assembly whose find number 30 has two alternates by serial.</summary>
    public required string AlternatesAssembly { get; init; }
    /// <summary>The released part whose revision B is effective from serial 101 through <see cref="EffectivityEcn"/>.</summary>
    public required string RevisedPart { get; init; }
    public required string EffectivityEcn { get; init; }
    public required string ReleasedMbom { get; init; }
    public required string StaleMbom { get; init; }
    public required string TdpPart { get; init; }
    public required string TdpRecipient { get; init; }
    public required Guid TdpExportId { get; init; }
    public required IReadOnlyList<string> ClearanceProfiles { get; init; }
    public required string DisabledSink { get; init; }
    public required long OutboxEvents { get; init; }
    /// <summary>The requirement specification of the demo (In Work, requirements allocated to parts, one verified); null for seeds made before 8.3.</summary>
    public string? RequirementSpec { get; init; }
}

/// <summary>Classification catalogue of the demo (class tree and the parts classified before their release: class data is iteration-owned).</summary>
public static class DemoClassification
{
    public sealed record Assignment(string Class, JsonObject Values);

    public static ClassHierarchyDoc Tree() => new(ClassHierarchyDoc.CurrentFormat,
    [
        new ClassNodeDoc("FASTENERS", "Fasteners", "Standard fasteners and hardware.", null, 10, "active", []),
        new ClassNodeDoc("SCREWS", "Screws", "Threaded screws.", "FASTENERS", 10, "active",
        [
            new AttributeDoc("thread", "Thread", "enum", EnumValues: ["M3", "M4", "M5", "M6"], Required: true, SortOrder: 10, Group: "Thread"),
            new AttributeDoc("length_mm", "Length", "number", Unit: "mm", Min: 1, Max: 200, Required: true, SortOrder: 20, Group: "Thread"),
        ]),
        new ClassNodeDoc("SOCKET-HEAD", "Socket head", "Socket head cap screws.", "SCREWS", 10, "active",
        [
            new AttributeDoc("drive_size", "Drive size", "string", MaxLength: 12, SortOrder: 30, Group: "Head"),
        ]),
        new ClassNodeDoc("MACHINED", "Machined parts", "Parts machined from bar or plate.", null, 20, "active",
        [
            new AttributeDoc("material_spec", "Material specification", "string", MaxLength: 40, SortOrder: 10, Group: "Material"),
        ]),
        new ClassNodeDoc("BRACKETS", "Brackets", "Brackets, yokes and mounting plates.", "MACHINED", 10, "active",
        [
            new AttributeDoc("wall_mm", "Minimum wall thickness", "number", Unit: "mm", Min: 0.5m, Max: 20, Required: true, SortOrder: 20, Group: "Geometry"),
        ]),
        new ClassNodeDoc("ELECTRONICS", "Electronics", "Electrical and electronic items.", null, 30, "active", []),
        new ClassNodeDoc("MODULES", "Modules", "Purchased electronic modules.", "ELECTRONICS", 10, "active",
        [
            new AttributeDoc("voltage_v", "Nominal voltage", "number", Unit: "V", Min: 0, Max: 100, Required: true, SortOrder: 10, Group: "Electrical"),
        ]),
    ]);

    private static JsonObject V(params (string, object)[] values)
    {
        var o = new JsonObject();
        foreach (var (k, v) in values) o[k] = v switch { string s => s, double d => d, int i => i, decimal m => (double)m, _ => throw new InvalidOperationException() };
        return o;
    }

    /// <summary>Part key to class and values: 13 parts, classified in the same check-out that links their documents or carries their structure.</summary>
    public static IReadOnlyDictionary<string, Assignment> Assignments { get; } = new Dictionary<string, Assignment>(StringComparer.Ordinal)
    {
        ["scr-m3x6"] = new("SOCKET-HEAD", V(("thread", "M3"), ("length_mm", 6), ("drive_size", "2.5 mm hex"))),
        ["scr-m3x8"] = new("SOCKET-HEAD", V(("thread", "M3"), ("length_mm", 8), ("drive_size", "2.5 mm hex"))),
        ["scr-m3x10"] = new("SOCKET-HEAD", V(("thread", "M3"), ("length_mm", 10), ("drive_size", "2.5 mm hex"))),
        ["scr-m4x12"] = new("SOCKET-HEAD", V(("thread", "M4"), ("length_mm", 12), ("drive_size", "3 mm hex"))),
        ["scr-m5x16"] = new("SOCKET-HEAD", V(("thread", "M5"), ("length_mm", 16), ("drive_size", "4 mm hex"))),
        ["bracket"] = new("BRACKETS", V(("wall_mm", 2.0), ("material_spec", "7075-T6"))),
        ["yoke"] = new("BRACKETS", V(("wall_mm", 3.0), ("material_spec", "7075-T6"))),
        ["mount-plate"] = new("BRACKETS", V(("wall_mm", 4.0), ("material_spec", "7075-T6"))),
        ["motor-plate"] = new("BRACKETS", V(("wall_mm", 3.0), ("material_spec", "6061-T6"))),
        ["shell-fwd"] = new("MACHINED", V(("material_spec", "6061-T6"))),
        ["fc"] = new("MODULES", V(("voltage_v", 12.0))),
        ["pdb"] = new("MODULES", V(("voltage_v", 14.8))),
        ["battery"] = new("MODULES", V(("voltage_v", 14.8))),
    };
}

public sealed partial class DemoSeeder
{
    private const string Context = "ORCA-POD";
    private const string Plant = "PLANT-A";
    private const string Recipient = "orca-prime-contractor";
    private int _classified;

    private async Task CreateClassificationTreeAsync()
    {
        var ada = User("ada");
        var report = (await host.Classification.ImportHierarchyAsync(ada, DemoClassification.Tree(), dryRun: false, "Demo classification tree (synthetic)", Corr())).Must("create the classification tree");
        log.WriteLine($"Classification: {report.Created} classes created ({string.Join(", ", report.Lines.Select(l => l.Code))}).");
    }

    /// <summary>Sets the class of a part in its open check-out (the values are validated against the class's effective attribute set).</summary>
    private async Task ClassifyAsync(Subject author, string key, string number)
    {
        if (!DemoClassification.Assignments.TryGetValue(key, out var a)) return;
        (await host.Classification.SetForPartAsync(author, number, a.Class, (JsonObject)a.Values.DeepClone(), Corr())).Must($"classify {number}");
        _classified++;
    }

    private async Task<DemoExtras> RunExtrasAsync()
    {
        var alice = User("alice");
        var ada = User("ada");
        var gimbal = _part["gimbal"];

        // (b) baselines, before the yoke is revised: a Design baseline established directly, a Release baseline by review with e-signatures.
        var design = (await host.Baselines.CreateAsync(alice, new CreateBaselineCommand(gimbal, "Gimbal design baseline", "Design", BomConfigSpec.LatestReleased, ReleasedOnly: true, IncludeDocuments: true,
            Purpose: "Frozen design of the gimbal before the yoke change.", Milestone: "PDR"), Corr())).Must("create the design baseline");
        (await host.Baselines.EstablishAsync(alice, design.Summary.Number, "Gimbal design reviewed at PDR.", Corr())).Must("establish the design baseline");
        var housing = _part["housing"];
        var release = (await host.Baselines.CreateAsync(alice, new CreateBaselineCommand(housing, "Pressure housing release baseline", "Release", BomConfigSpec.LatestReleased, ReleasedOnly: true,
            IncludeDocuments: true, Purpose: "Released configuration of the pressure housing for the CDR package.", Milestone: "CDR"), Corr())).Must("create the release baseline");
        var started = (await host.Workflow.StartAsync(alice, new StartReviewCommand(BaselineService.ObjectType, release.Summary.Number, Reviewers()), Corr())).Must("submit the release baseline");
        started = await SignAsync(started, Reviewer);
        started = await SignAsync(started, Approver);
        if (started.Status != InstanceStatus.Approved) throw new SeedException($"{release.Summary.Number} did not reach Approved (status {started.Status}).");
        log.WriteLine($"Baselines: {design.Summary.Number} (Design, established) and {release.Summary.Number} (Release, signed).");

        // (c) effectivity: a context with blocks, two alternates by serial in an in-work assembly, revision B of a released part effective from serial 101 by ECN.
        (await host.Effectivity.CreateContextAsync(alice, new CreateContextCommand(Context, "ORCA pod (synthetic end item)", "Serial numbers and blocks of the ORCA sensor pod.", "ORCA",
            [new EffectivityBlock("Block 1", 1), new EffectivityBlock("Block 2", 2)]), Corr())).Must("create the effectivity context");
        var partner = _part["partner"];
        (await host.Parts.CheckOutAsync(alice, partner, Corr())).Must($"check out {partner}");
        var working = (await host.Bom.GetWorkingAsync(alice, partner, Corr())).Must($"read {partner}");
        var screw = working.Lines.Single(l => l.FindNumber == 30);
        (await host.Bom.UpdateUsageAsync(alice, partner, screw.Id, new UpdateUsageCommand(screw.Quantity, screw.Unit, 30, screw.ReferenceDesignators, screw.Notes, new EffectivityInput("serial", Context, "1", "100")), Corr())).Must("effectivity of the first alternate");
        (await host.Bom.AddUsageAsync(alice, partner, new AddUsageCommand(_part["scr-m4x12"], 8, null, 30, null, "Later units: redesigned plate with M4 screws",
            new EffectivityInput("serial", Context, "101", null)), Corr())).Must("second alternate");
        _lines++;
        (await host.Parts.CheckInAsync(alice, partner, "Alternates by serial number for find number 30", Corr())).Must($"check in {partner}");

        var yoke = _part["yoke"];
        var ecn = await NewChangeAsync(alice, ChangeTypes.Ecn, "Chamfer the gimbal yoke edge from serial 101",
            "Break the sharp edge at the cable pass-through. Production units from serial 101 get revision B; earlier units keep revision A.", "Medium",
            "Cable chafing found during assembly.", DemoCatalog.Itar(), ChangeClasses.II);
        (await host.Change.AddAffectedAsync(alice, ecn, new AddAffectedCommand("part", yoke, ChangeActions.Revise, null, false, new EffectivityInput("serial", Context, "101", null)), Corr())).Must($"add {yoke} to {ecn}");
        await EditNewRevisionAsync(alice, yoke, "Edge chamfer added at the cable pass-through (revision B).", 0.27, "Edge chamfer");
        var review = (await host.Workflow.StartAsync(alice, EcnReview(ecn), Corr())).Must($"submit {ecn}");
        review = await SignAsync(review, Reviewer);
        review = await SignAsync(review, Approver);
        if (review.Status != InstanceStatus.Approved) throw new SeedException($"{ecn} did not reach Approved (status {review.Status}).");
        log.WriteLine($"Effectivity: context {Context}; {partner} find 30 has two alternates by serial; {ecn} released yoke revision B effective from serial 101.");

        // (d) manufacturing BOMs: the gimbal drive module (released through review) and the pod (left in work; its EBOM then changes, so its reconciliation is stale).
        var drive = (await host.Mbom.GenerateAsync(alice, new GenerateMbomCommand(_part["drive"], Plant), Corr())).Must("generate the drive MBOM").Items[0].MbomNumber;
        (await host.Mbom.ReconcileAsync(alice, drive, null, Corr())).Must($"reconcile {drive}");
        var mbomReview = (await host.Workflow.StartAsync(alice, new StartReviewCommand(MbomService.ObjectType, drive, Reviewers()), Corr())).Must($"submit {drive}");
        mbomReview = await SignAsync(mbomReview, Reviewer);
        mbomReview = await SignAsync(mbomReview, Approver);
        if (mbomReview.Status != InstanceStatus.Approved) throw new SeedException($"{drive} did not reach Approved (status {mbomReview.Status}).");
        var pod = (await host.Mbom.GenerateAsync(alice, new GenerateMbomCommand(_part["top"], Plant, new EbomSpec(EbomSpecKind.Latest)), Corr())).Must("generate the pod MBOM").Items[0].MbomNumber;
        (await host.Mbom.ReconcileAsync(alice, pod, new EbomSpec(EbomSpecKind.Latest), Corr())).Must($"reconcile {pod}");
        var top = _part["top"];
        (await host.Parts.CheckOutAsync(alice, top, Corr())).Must($"check out {top}");
        var topLines = (await host.Bom.GetWorkingAsync(alice, top, Corr())).Must($"read {top}");
        var desiccant = topLines.Lines.Single(l => l.Quantity == 2 && l.FindNumber == topLines.Lines.Max(x => x.FindNumber));
        (await host.Bom.UpdateUsageAsync(alice, top, desiccant.Id, new UpdateUsageCommand(3, desiccant.Unit, desiccant.FindNumber, desiccant.ReferenceDesignators, desiccant.Notes), Corr())).Must("change the pod EBOM");
        (await host.Parts.CheckInAsync(alice, top, "Third desiccant pack", Corr())).Must($"check in {top}");
        (await host.Mbom.ReconcileAsync(alice, pod, new EbomSpec(EbomSpecKind.Latest), Corr())).Must($"reconcile {pod} against the changed EBOM");
        log.WriteLine($"MBOMs ({Plant}): {drive} released through review; {pod} in work, derived from an EBOM that has moved on (stale).");

        // (f) integration: clearance profiles and a disabled file-drop sink (nothing points at a system that does not exist).
        var profiles = new List<string>();
        profiles.Add((await host.IntegrationAdmin.SaveProfileAsync(ada, new ProfileInput(Recipient, "ORCA prime contractor (US persons only)", "Synthetic recipient cleared for ORCA technical data.",
            "DodContractor", true, ["None", "Ear", "Itar"], [], [], ["EXPT", "CTI"], ["ORCA"], ["A", "B", "C", "D"], []), Corr())).Must("save the prime contractor profile").Name);
        profiles.Add((await host.IntegrationAdmin.SaveProfileAsync(ada, new ProfileInput("partner-taa-0042", "Licensed partner (TAA-0042)", "Foreign partner that holds the agreement TAA-0042; only data that lists it can be released.",
            "Foreign", false, ["None", "Ear", "Itar"], [], [], ["EXPT"], ["ORCA"], ["B", "C", "D"], ["TAA-0042"]), Corr())).Must("save the partner profile").Name);
        profiles.Add((await host.IntegrationAdmin.SaveProfileAsync(ada, new ProfileInput("public-release", "Public release (distribution A only)", "Open data only.",
            "Public", false, ["None"], [], [], [], [], ["A"], []), Corr())).Must("save the public profile").Name);
        var sink = (await host.IntegrationAdmin.SaveSinkAsync(ada, new SinkInput("erp-staging-drop", "ERP staging drop (disabled)", "filedrop", false, Recipient,
            new JsonObject { ["directory"] = "erp-staging", ["format"] = "ndjson" }), Corr())).Must("save the file-drop sink").Name;

        // (e) a TDP export record: the real service writes the signed package to a throwaway stream; only the manifest is kept.
        var tdp = (await host.Tdp.PrepareAsync(alice, new TdpRequest("part", _part["bracket"], null, Recipient), Corr())).Must("prepare the TDP export of the gimbal bracket");
        var exportRecord = await tdp.WriteToAsync(Stream.Null, CancellationToken.None);
        log.WriteLine($"Integration: profiles {string.Join(", ", profiles)}; disabled sink {sink}; TDP export {exportRecord.Id:D} of {_part["bracket"]} for {Recipient}.");

        return new DemoExtras
        {
            ClassCodes = [.. DemoClassification.Tree().Nodes.Select(n => n.Code)], ClassifiedParts = _classified,
            DesignBaseline = design.Summary.Number, ReleaseBaseline = release.Summary.Number, EffectivityContext = Context, AlternatesAssembly = partner,
            RevisedPart = yoke, EffectivityEcn = ecn, ReleasedMbom = drive, StaleMbom = pod, TdpPart = _part["bracket"], TdpRecipient = Recipient, TdpExportId = exportRecord.Id,
            ClearanceProfiles = profiles, DisabledSink = sink, OutboxEvents = await host.OutboxCountAsync(ada), RequirementSpec = await SeedRequirementsAsync(),
        };
    }

    private static Dictionary<string, string[]> Reviewers() => new() { ["reviewer"] = [Reviewer], ["approvers"] = [Approver] };
}

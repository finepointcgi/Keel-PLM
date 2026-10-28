using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Documents;
using Keel.Parts;
using Keel.Workflow;

namespace Keel.Seed;

/// <summary>The objects the demo script and the browser tests refer to, plus what was created.</summary>
public sealed record DemoManifest
{
    public required string TopAssembly { get; init; }
    public required string HousingAssembly { get; init; }
    public required string GimbalAssembly { get; init; }
    public required string PayloadAssembly { get; init; }
    public required string AvionicsAssemblyUnderReview { get; init; }
    public required string PartnerAssembly { get; init; }
    public required string SparesKit { get; init; }
    public required string GimbalBracket { get; init; }
    public required string EndCap { get; init; }
    public required string LensBarrel { get; init; }
    public required string SensorMountingPlate { get; init; }
    public required string PartnerPlate { get; init; }
    public required string KrakenPart { get; init; }
    public required string OpenFastener { get; init; }
    public required string SystemSpecification { get; init; }
    public required string BracketDrawing { get; init; }
    public required string PartnerDrawing { get; init; }
    public required string CadPlaceholder { get; init; }
    public required string TorqueStandard { get; init; }
    public required string ProblemReportEvaluating { get; init; }
    public required string ProblemReportOpen { get; init; }
    public required string EcrAwaitingCcb { get; init; }
    public required string EcrClassIIDraft { get; init; }
    public required string EcnDraftInWork { get; init; }
    public required string EcnUnderReview { get; init; }
    public required string EcnRejected { get; init; }
    public required IReadOnlyList<string> ChangeTasks { get; init; }
    public required int Parts { get; init; }
    public required int Released { get; init; }
    public required int Documents { get; init; }
    public required int BomLines { get; init; }
    public required int Changes { get; init; }
    public required int Signatures { get; init; }
    public required long AuditEvents { get; init; }
    public bool AuditChainValid { get; set; }
    public long AuditEventsChecked { get; set; }
    public IReadOnlyList<string> AuditFailures { get; set; } = [];
}

/// <summary>
/// Builds the ORCA demonstration data set entirely through the module services as real users: every part, document, structure line,
/// review, signature, change object and task is authorized, validated and audited exactly like a click in the UI. The one thing the seed
/// does that a person would do through the identity provider is the fresh login: signers carry an <c>auth_time</c> of now.
/// </summary>
public sealed class DemoSeeder(SeedHost host, IReadOnlyDictionary<string, Subject> realmUsers, TextWriter log)
{
    private const string Reviewer = "ada";
    private const string Approver = "bob";
    private readonly Dictionary<string, string> _part = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _doc = new(StringComparer.Ordinal);
    private int _signatures, _lines, _released, _changes;
    private int _corr;

    private Subject User(string name) =>
        name == RealmUsers.KrakenEngineer.Username ? RealmUsers.KrakenEngineer
        : realmUsers.TryGetValue(name, out var s) ? s : throw new SeedException($"The dev realm has no user '{name}'.");

    private string Corr() => $"seed-demo-{++_corr:D4}";

    public async Task<DemoManifest> RunAsync()
    {
        await host.UpsertDirectoryAsync(realmUsers.Values);
        log.WriteLine($"Directory: {realmUsers.Count} realm users recorded ({string.Join(", ", realmUsers.Keys)}).");

        await CreatePartsAsync();
        await CreateDocumentsAsync();
        await BuildAndReleaseAsync();
        var changes = await CreateChangesAsync();

        var audit = await host.AuditEventCountAsync();
        return new DemoManifest
        {
            TopAssembly = _part["top"], HousingAssembly = _part["housing"], GimbalAssembly = _part["gimbal"], PayloadAssembly = _part["payload"],
            AvionicsAssemblyUnderReview = _part["avionics"], PartnerAssembly = _part["partner"], SparesKit = _part["spares"],
            GimbalBracket = _part["bracket"], EndCap = _part["endcap"], LensBarrel = _part["lens-barrel"], SensorMountingPlate = _part["mount-plate"],
            PartnerPlate = _part["plate-tx"], KrakenPart = _part["kraken"], OpenFastener = _part["scr-m5x16"],
            SystemSpecification = _doc["spec-system"], BracketDrawing = _doc["dwg-bracket"], PartnerDrawing = _doc["dwg-partner"],
            CadPlaceholder = _doc["cad-bracket"], TorqueStandard = _doc["std-torque"],
            ProblemReportEvaluating = changes.Pr1, ProblemReportOpen = changes.Pr2, EcrAwaitingCcb = changes.Ecr1, EcrClassIIDraft = changes.Ecr2,
            EcnDraftInWork = changes.Ecn1, EcnUnderReview = changes.Ecn2, EcnRejected = changes.Ecn3, ChangeTasks = changes.Tasks,
            Parts = _part.Count, Released = _released, Documents = _doc.Count, BomLines = _lines, Changes = _changes, Signatures = _signatures, AuditEvents = audit,
        };
    }

    // ---------------------------------------------------------------- parts and documents

    private async Task CreatePartsAsync()
    {
        foreach (var def in DemoCatalog.Parts)
        {
            var created = (await host.Parts.CreateAsync(User(def.Author),
                new CreatePartCommand(def.Name, def.Type, def.Unit, def.Source, def.Markings, def.Description, def.Attributes), Corr())).Must($"create {def.Name}");
            _part[def.Key] = created.Number;
        }
        log.WriteLine($"Parts: {_part.Count} created ({_part["top"]} is the top assembly).");
    }

    private async Task CreateDocumentsAsync()
    {
        foreach (var def in DemoCatalog.Documents)
        {
            await using var content = new MemoryStream(def.Content());
            var created = (await host.Documents.CreateAsync(User(def.Author), new CreateDocumentCommand(def.Name, def.Type, def.Markings, def.Description),
                new FileUpload(def.FileName, def.MediaType, content), Corr())).Must($"create document {def.Name}");
            _doc[def.Key] = created.Number;
        }
        log.WriteLine($"Documents: {_doc.Count} created with files in the vault.");
    }

    /// <summary>Bottom-up: a part's children are final before its own structure is checked in and released.</summary>
    private async Task BuildAndReleaseAsync()
    {
        var height = new Dictionary<string, int>(StringComparer.Ordinal);
        int Height(string key) => height.TryGetValue(key, out var h) ? h
            : height[key] = DemoCatalog.Structure.TryGetValue(key, out var lines) ? 1 + lines.Max(l => Height(l.Child)) : 0;
        foreach (var def in DemoCatalog.Parts) _ = Height(def.Key);

        foreach (var def in DemoCatalog.Parts.Where(p => p.Key != "kraken").OrderBy(p => height[p.Key]).ThenBy(p => DemoCatalog.Parts.ToList().IndexOf(p)))
        {
            var author = User(def.Author);
            var number = _part[def.Key];
            var hasLines = DemoCatalog.Structure.TryGetValue(def.Key, out var lines);
            var hasLinks = DemoCatalog.Links.TryGetValue(def.Key, out var links);
            if (hasLines || hasLinks)
            {
                (await host.Parts.CheckOutAsync(author, number, Corr())).Must($"check out {number}");
                if (hasLines)
                    foreach (var line in lines!)
                    {
                        (await host.Bom.AddUsageAsync(author, number, new AddUsageCommand(_part[line.Child], line.Qty, line.Unit, null, line.Refs), Corr())).Must($"add {line.Child} to {number}");
                        _lines++;
                    }
                if (hasLinks)
                    foreach (var (doc, type) in links!)
                        (await host.Documents.AddLinkToWorkingCopyAsync(author, number, _doc[doc], type, null, Corr())).Must($"link {doc} to {number}");
                (await host.Parts.CheckInAsync(author, number, hasLines ? "Structure and documents" : "Documents linked", Corr())).Must($"check in {number}");
            }

            switch (def.Fate)
            {
                case Fate.Released:
                    await ReleaseAsync(author, number);
                    break;
                case Fate.UnderReview:
                    (await host.Workflow.StartAsync(author, PartReview(number), Corr())).Must($"submit {number}");
                    log.WriteLine($"  {number} {def.Name}: under review (reviewer {Reviewer} pending).");
                    break;
            }
        }

        // The KRAKEN engineer adds the restricted line last: nobody else may read the child.
        var (parent, line2) = DemoCatalog.KrakenLine;
        var kraken = RealmUsers.KrakenEngineer;
        (await host.Parts.CheckOutAsync(kraken, _part[parent], Corr())).Must("KRAKEN check out");
        (await host.Bom.AddUsageAsync(kraken, _part[parent], new AddUsageCommand(_part[line2.Child], line2.Qty), Corr())).Must("add KRAKEN line");
        _lines++;
        (await host.Parts.CheckInAsync(kraken, _part[parent], "Cooler interface (KRAKEN program)", Corr())).Must("KRAKEN check in");
        log.WriteLine($"Structure: {_lines} usage lines; {_released} part revisions released with e-signatures.");
    }

    private static StartReviewCommand PartReview(string number) => new("part", number,
        new Dictionary<string, string[]> { ["reviewer"] = [Reviewer], ["approvers"] = [Approver] });

    private async Task ReleaseAsync(Subject author, string number)
    {
        var view = (await host.Workflow.StartAsync(author, PartReview(number), Corr())).Must($"submit {number}");
        view = await SignAsync(view, Reviewer);
        view = await SignAsync(view, Approver);
        if (view.Status != InstanceStatus.Approved) throw new SeedException($"{number} did not reach Approved (status {view.Status}).");
        _released++;
    }

    /// <summary>Signs the open task of <paramref name="username"/> with a fresh login (<c>auth_time</c> = now), as the step-up dialog would produce.</summary>
    private async Task<InstanceView> SignAsync(InstanceView view, string username, string? code = null, string? comment = null)
    {
        var task = view.Tasks.FirstOrDefault(t => t.Status == "Open" && t.AssigneeUsername == username)
            ?? throw new SeedException($"{username} has no open task in workflow {view.Id}.");
        var result = await host.Workflow.SignAsync(RealmUsers.Fresh(User(username)), task.Id, code ?? (task.Kind == "review" ? "Reviewed" : "Approved"), comment, Corr());
        _signatures++;
        return result.Must($"{username} signing '{task.Name}'");
    }

    // ---------------------------------------------------------------- change management

    private sealed record ChangeSet(string Pr1, string Pr2, string Ecr1, string Ecr2, string Ecn1, string Ecn2, string Ecn3, IReadOnlyList<string> Tasks);

    private async Task<ChangeSet> CreateChangesAsync()
    {
        var alice = User("alice");
        var m = DemoCatalog.Itar();

        // ECN-1 first (it is the notice the demo submits live): Class II, revises the gimbal bracket; revision B is in work and checked in.
        var ecn1 = await NewChangeAsync(alice, ChangeTypes.Ecn, "Increase gimbal bracket wall thickness",
            "Micro-cracks at the bracket radius after random vibration (see the vibration report). Increase the wall thickness from 2.0 mm to 3.0 mm.",
            "High", "Vibration test margin is below the program requirement.", m, ChangeClasses.II);
        await AddAffectedAsync(alice, ecn1, _part["bracket"]);
        await EditNewRevisionAsync(alice, _part["bracket"], "Wall thickness increased from 2.0 mm to 3.0 mm (revision B).", 0.36, "Thicker wall at the radius");

        // ECN-2: under review. The reviewer has signed; the approver's task is still open.
        var ecn2 = await NewChangeAsync(alice, ChangeTypes.Ecn, "Add drain groove to sensor end cap",
            "Condensation collects behind the window. Add a drain groove to the end cap.", "Medium", "Window fogging seen in the thermal chamber.", m, ChangeClasses.II);
        await AddAffectedAsync(alice, ecn2, _part["endcap"]);
        await EditNewRevisionAsync(alice, _part["endcap"], "Added a 1.5 mm drain groove at the lower edge (revision B).", 0.37, "Drain groove added");
        var review2 = (await host.Workflow.StartAsync(alice, EcnReview(ecn2), Corr())).Must($"submit {ecn2}");
        await SignAsync(review2, Reviewer, comment: "Groove depth checked against the O-ring seat.");

        // ECN-3: rejected by the approver; the notice is back in Draft with the rejection recorded.
        var ecn3 = await NewChangeAsync(alice, ChangeTypes.Ecn, "Change lens barrel material to aluminum",
            "Reduce cost by changing the lens barrel from titanium to 7075 aluminum.", "Low", "Cost reduction proposal.", m, ChangeClasses.II);
        await AddAffectedAsync(alice, ecn3, _part["lens-barrel"]);
        await EditNewRevisionAsync(alice, _part["lens-barrel"], "Material changed to 7075-T6 aluminum (revision B).", 0.15, "Material change");
        var review3 = (await host.Workflow.StartAsync(alice, EcnReview(ecn3), Corr())).Must($"submit {ecn3}");
        review3 = await SignAsync(review3, Reviewer);
        await SignAsync(review3, Approver, "Rejected", "Thermal expansion mismatch with the sapphire window is not addressed.");

        // PR-1 (evaluating) -> ECR-1: Class I, awaiting the CCB (bob's approval task is open).
        var pr1 = await NewChangeAsync(alice, ChangeTypes.Pr, "Sensor mount resonance at 180 Hz",
            "The payload shows a resonance at 180 Hz on the shaker table, close to the vehicle excitation band.", "High", "Reported by the test lab.", m);
        Must(await host.Change.TransitionAsync(alice, pr1, "Evaluating", null, "Under evaluation by engineering.", Corr()), "evaluate PR");
        var ecr1 = (await host.Change.CreateFromAsync(alice, pr1, ChangeTypes.Ecr, new CreateFromCommand(Title: "Redesign sensor mounting plate for stiffness", ChangeClass: ChangeClasses.I), Corr()))
            .Must("ECR from PR").Number;
        _changes++;
        await AddAffectedAsync(alice, ecr1, _part["mount-plate"]);
        (await host.Workflow.StartAsync(alice, new StartReviewCommand("ecr", ecr1, new Dictionary<string, string[]> { ["ccb"] = [Approver] }), Corr())).Must($"submit {ecr1}");

        // PR-2: open and untouched. ECR-2: a Class II request still in Draft.
        var pr2 = await NewChangeAsync(alice, ChangeTypes.Pr, "Window fogging in the thermal chamber",
            "Fogging appears on the inside of the sensor window after a cold soak.", "Medium", "Reported by the test lab.", m);
        var ecr2 = await NewChangeAsync(alice, ChangeTypes.Ecr, "Add a drain plug to the aft housing shell",
            "A drain plug would let the pod be emptied after a leak test.", "Low", "Operations request.", m, ChangeClasses.II);

        // Change tasks on ECN-1 (assignees: alice, ada, bob) and one finished task on ECN-2.
        var tasks = new List<string>();
        var due = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14));
        var ct1 = await NewTaskAsync(alice, ecn1, "Update the gimbal bracket drawing to revision B", "Thickness note and section view.", "alice", due);
        Must(await host.Change.StartTaskAsync(alice, ct1, Corr()), "start task");
        tasks.Add(ct1);
        tasks.Add(await NewTaskAsync(alice, ecn1, "Re-run the random vibration test on revision B", "Same profile as the original test.", "ada", due.AddDays(7)));
        tasks.Add(await NewTaskAsync(alice, ecn1, "Tell the partner about the interface change", "Send the revised interface drawing under TAA-0042.", "bob", due.AddDays(3)));
        var ct4 = await NewTaskAsync(alice, ecn2, "Update the end cap drawing", "Add the drain groove.", "alice", due);
        Must(await host.Change.CompleteTaskAsync(alice, ct4, "Drawing updated.", Corr()), "complete task");
        tasks.Add(ct4);

        var impact = (await host.Impact.AnalyzeAsync(alice, ecn1, 5, Corr())).Must("impact analysis of the demo ECN");
        log.WriteLine($"Changes: {_changes} objects; impact of {ecn1}: {impact.Objects.Sum(o => o.WhereUsed.Count)} where-used rows.");
        return new ChangeSet(pr1, pr2, ecr1, ecr2, ecn1, ecn2, ecn3, tasks);
    }

    private static StartReviewCommand EcnReview(string number) => new("ecn", number,
        new Dictionary<string, string[]> { ["reviewer"] = [Reviewer], ["approvers"] = [Approver] });

    private async Task<string> NewChangeAsync(Subject by, string type, string title, string description, string priority, string reason, Markings markings, string? cls = null)
    {
        var created = (await host.Change.CreateAsync(by, new CreateChangeCommand(type, title, description, priority, reason, markings, cls), Corr())).Must($"create {type} '{title}'");
        _changes++;
        return created.Number;
    }

    private async Task AddAffectedAsync(Subject by, string change, string part) =>
        (await host.Change.AddAffectedAsync(by, change, new AddAffectedCommand("part", part, ChangeActions.Revise), Corr())).Must($"add {part} to {change}");

    private async Task<string> NewTaskAsync(Subject by, string ecn, string title, string description, string assignee, DateOnly due)
    {
        var detail = (await host.Change.CreateTaskAsync(by, ecn, new CreateTaskCommand(title, description, assignee, due), Corr())).Must($"task '{title}'");
        return detail.Tasks.OrderByDescending(t => t.CreatedAt).First().Number;
    }

    private static void Must(Outcome<ChangeDetail> outcome, string what) => _ = outcome.Must(what);

    /// <summary>Edits the new revision the ECN created and checks it in, so the notice can be submitted.</summary>
    private async Task EditNewRevisionAsync(Subject by, string part, string description, double mass, string comment)
    {
        (await host.Parts.CheckOutAsync(by, part, Corr())).Must($"check out {part}");
        var detail = (await host.Parts.GetAsync(by, part, null, Corr())).Must($"read {part}");
        var attributes = detail.Checkout?.Attributes?.DeepClone() as JsonObject ?? [];
        attributes["mass"] = mass;
        (await host.Parts.UpdateWorkingCopyAsync(by, part, description, attributes, Corr())).Must($"edit {part}");
        (await host.Parts.CheckInAsync(by, part, comment, Corr())).Must($"check in {part}");
    }

}

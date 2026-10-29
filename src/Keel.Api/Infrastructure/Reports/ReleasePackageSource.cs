using Keel.Bom;
using Keel.Change;
using Keel.Core;
using Keel.Core.Reports;
using Keel.Parts;
using Keel.Security;
using Keel.Workflow;

namespace Keel.Api.Infrastructure.Reports;

/// <summary>
/// Collects the content of a release package from the owning services as the caller (ADR-0044). Each service applies its own read filter; a placeholder (redacted) row anywhere
/// means an object the package needs cannot be read, and the package is refused with a count and no names.
/// </summary>
public sealed class ReleasePackageSource(PartService parts, BomService bom, BaselineService baselines, ChangeService changes, WorkflowService workflow, ICadPartPort partPort,
    ICadDocumentPort documents, IEnumerable<ILinkedDocumentProvider> linkedProviders, IEnumerable<IObjectAccess> access) : IReleasePackageSource
{
    private const int MaxChildPartsForDocuments = 100;
    private readonly Dictionary<string, IObjectAccess> byKind = access.ToDictionary(a => a.Kind, StringComparer.Ordinal);

    private static Outcome<ReleasePackageContent> Refuse(int hidden) =>
        Outcome<ReleasePackageContent>.Forbidden([$"package.unreadable_objects:{hidden}"]);

    public async Task<Outcome<ReleasePackageContent>> CollectAsync(Subject subject, string kind, string key, string? correlationId, CancellationToken ct = default) =>
        kind switch
        {
            "part" => await CollectPartAsync(subject, key, correlationId, ct),
            "ecn" => await CollectChangeAsync(subject, key, correlationId, ct),
            "baseline" => await CollectBaselineAsync(subject, key, correlationId, ct),
            _ => Outcome<ReleasePackageContent>.Invalid(["Unknown package kind."]),
        };

    private static PackageBomLine Line(BomLine l) => new(l.Level, l.Path, l.FindNumber, l.Number!, l.Name ?? "", l.Revision ?? "", l.StateLabel ?? l.State ?? "", l.Quantity, l.Unit, l.EffectivityText, l.Markings!);

    private async Task<Outcome<ReleasePackageContent>> CollectPartAsync(Subject subject, string key, string? corr, CancellationToken ct)
    {
        var detail = await parts.GetAsync(subject, key, null, corr, ct);
        if (!detail.IsOk) return new Outcome<ReleasePackageContent>(detail.Kind, null, detail.Errors);
        var d = detail.Value!;
        if (d.Revision.State != "Released") return Outcome<ReleasePackageContent>.Conflict($"{d.Number} revision {d.Revision.Revision} is {d.Revision.StateLabel}. A release package is made for released data.");
        var objects = new List<PackageObject> { new("part", d.Number, d.Name, d.Revision.Revision, d.Revision.StateLabel, d.Markings) };
        var explosion = await bom.ExplodeAsync(subject, new BomQuery(key, Spec: BomConfigSpec.LatestReleased), corr, ct);
        if (!explosion.IsOk) return new Outcome<ReleasePackageContent>(explosion.Kind, null, explosion.Errors);
        var ex = explosion.Value!;
        if (ex.RedactedCount > 0) return Refuse(ex.RedactedCount);
        if (ex.UnresolvedCount > 0) return Outcome<ReleasePackageContent>.Conflict($"{ex.UnresolvedCount} line(s) of the structure have no released revision. Release them or take a baseline.");
        var lines = ex.Lines.Where(l => l.Number is not null && l.Markings is not null).ToList();
        var childParts = new List<(string Number, string Revision)>();
        foreach (var l in lines.DistinctBy(l => l.Number))
        {
            objects.Add(new PackageObject("part", l.Number!, l.Name ?? "", l.Revision ?? "", l.StateLabel ?? "", l.Markings!));
            childParts.Add((l.Number!, l.Revision ?? ""));
        }
        var docs = await DocumentsAsync(subject, [(d.Number, d.Revision.Revision), .. childParts.Take(MaxChildPartsForDocuments)], corr, ct);
        if (!docs.IsOk) return new Outcome<ReleasePackageContent>(docs.Kind, null, docs.Errors);
        var signatures = await SignaturesAsync(subject, "part", d.Number, d.Revision.Revision, corr, ct);
        var related = new List<PackageChange>();
        var bound = await changes.ChangesForObjectAsync(subject, "part", d.Number, d.Revision.Revision, corr, ct);
        if (bound.IsOk)
        {
            if (bound.Value!.BoundToHidden) return Refuse(1);
            if (bound.Value.BoundTo is { } cs)
            {
                var chg = await changes.GetAsync(subject, cs.Number, corr, ct);
                if (!chg.IsOk) return Refuse(1);
                objects.Add(new PackageObject("change", chg.Value!.Number, chg.Value.Title, "", chg.Value.StateLabel, chg.Value.Markings));
                related.Add(Summarize(chg.Value));
            }
        }
        return Outcome<ReleasePackageContent>.Ok(new ReleasePackageContent("part", d.Number, d.Name, d.Revision.Revision, objects, [.. lines.Select(Line)], docs.Value!, signatures, related));
    }

    private static PackageChange Summarize(ChangeDetail c) =>
        new(c.Number, c.Type.ToUpperInvariant(), c.Title, c.StateLabel, c.ChangeClass, c.Reason,
            [.. c.Affected.Where(a => !a.Redacted).Select(a => $"{a.ObjectType} {a.Key} {a.Revision} -> {a.ResultingRevision ?? "no new revision"} ({a.Action})")]);

    private async Task<Outcome<IReadOnlyList<PackageDocument>>> DocumentsAsync(Subject subject, IEnumerable<(string Number, string Revision)> partsToScan, string? corr, CancellationToken ct)
    {
        var provider = linkedProviders.FirstOrDefault(p => p.ObjectType == "part");
        var result = new List<PackageDocument>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hidden = 0;
        if (provider is null) return Outcome<IReadOnlyList<PackageDocument>>.Ok(result);
        foreach (var (number, _) in partsToScan)
        {
            var linked = await provider.GetLinkedAsync(subject, number, null, null, corr, ct);
            if (!linked.IsOk) continue;
            foreach (var l in linked.Value!)
            {
                if (l.Redacted || l.Number is null) { hidden++; continue; }
                if (!seen.Add(l.Number)) continue;
                var info = await documents.GetAsync(subject, l.Number, corr, ct);
                if (!info.IsOk) { hidden++; continue; }
                var i = info.Value!;
                result.Add(new PackageDocument(i.Number, i.Name, i.DocumentType, i.Revision, i.State, number, i.Markings, [.. i.Files.Select(f => new PackageFile(f.FileName, f.SizeBytes, f.Sha256))]));
            }
        }
        return hidden > 0 ? Outcome<IReadOnlyList<PackageDocument>>.Forbidden([$"package.unreadable_objects:{hidden}"]) : Outcome<IReadOnlyList<PackageDocument>>.Ok(result);
    }

    private async Task<IReadOnlyList<PackageSignature>> SignaturesAsync(Subject subject, string objectType, string key, string revision, string? corr, CancellationToken ct)
    {
        var status = await workflow.StatusForObjectAsync(subject, objectType, key, corr, ct);
        if (!status.IsOk) return [];
        return [.. status.Value!.Releases.Where(r => r.Revision == revision).SelectMany(r => r.Signatures)
            .Select(s => new PackageSignature(s.ObjectKey, s.Revision, s.SignerName, s.Meaning, s.Decision, s.SignedAt, s.Comment)).OrderBy(s => s.SignedAt)];
    }

    private async Task<Outcome<ReleasePackageContent>> CollectChangeAsync(Subject subject, string key, string? corr, CancellationToken ct)
    {
        var chg = await changes.GetAsync(subject, key, corr, ct);
        if (!chg.IsOk) return new Outcome<ReleasePackageContent>(chg.Kind, null, chg.Errors);
        var c = chg.Value!;
        if (c.Type != "ecn") return Outcome<ReleasePackageContent>.Invalid(["Only an ECN can be packaged as a change."]);
        if (!c.IsLocked) return Outcome<ReleasePackageContent>.Conflict($"{c.Number} is {c.StateLabel}. A release package is made for a released or closed change.");
        var hidden = c.Affected.Count(a => a.Redacted);
        if (hidden > 0) return Refuse(hidden);
        var objects = new List<PackageObject> { new("change", c.Number, c.Title, "", c.StateLabel, c.Markings) };
        var numbers = c.Affected.Where(a => a.ObjectType == "part" && a.Key is not null).Select(a => a.Key!).Distinct(StringComparer.Ordinal).ToList();
        var found = await partPort.FindManyAsync(subject, numbers, ct);
        if (found.Count != numbers.Count) return Refuse(numbers.Count - found.Count);
        objects.AddRange(found.Select(p => new PackageObject("part", p.Number, p.Name, p.Revision, p.State, p.Markings)));
        var docs = await DocumentsAsync(subject, numbers.Select(n => (n, "")), corr, ct);
        if (!docs.IsOk) return new Outcome<ReleasePackageContent>(docs.Kind, null, docs.Errors);
        var signatures = await SignaturesAsync(subject, "change", c.Number, "", corr, ct);
        return Outcome<ReleasePackageContent>.Ok(new ReleasePackageContent("ecn", c.Number, c.Title, "", objects, [], docs.Value!, signatures, [Summarize(c)]));
    }

    private async Task<Outcome<ReleasePackageContent>> CollectBaselineAsync(Subject subject, string key, string? corr, CancellationToken ct)
    {
        var bl = await baselines.GetAsync(subject, key, corr, ct);
        if (!bl.IsOk) return new Outcome<ReleasePackageContent>(bl.Kind, null, bl.Errors);
        var s = bl.Value!.Summary;
        if (s.State != BaselineStates.Established) return Outcome<ReleasePackageContent>.Conflict($"{s.Number} is {s.StateLabel}. A release package is made for an established baseline.");
        if (s.RootNumber is null) return Refuse(1);
        var root = await parts.GetAsync(subject, s.RootNumber, null, corr, ct);
        if (!root.IsOk) return Refuse(1);
        var explosion = await bom.ExplodeAsync(subject, new BomQuery(s.RootNumber, Spec: BomConfigSpec.Baseline, Baseline: s.Number), corr, ct);
        if (!explosion.IsOk) return new Outcome<ReleasePackageContent>(explosion.Kind, null, explosion.Errors);
        var ex = explosion.Value!;
        if (ex.RedactedCount > 0) return Refuse(ex.RedactedCount);
        var lines = ex.Lines.Where(l => l.Number is not null && l.Markings is not null).ToList();
        var objects = new List<PackageObject> { new("baseline", s.Number, s.Name, "", s.StateLabel, root.Value!.Markings), new("part", root.Value.Number, root.Value.Name, root.Value.Revision.Revision, root.Value.Revision.StateLabel, root.Value.Markings) };
        objects.AddRange(lines.DistinctBy(l => l.Number).Select(l => new PackageObject("part", l.Number!, l.Name ?? "", l.Revision ?? "", l.StateLabel ?? "", l.Markings!)));
        var pinned = await baselines.GetDocumentsAsync(subject, key, corr, ct);
        var docs = new List<PackageDocument>();
        if (pinned.IsOk)
        {
            if (pinned.Value!.Rows.Any(r => r.Redacted)) return Refuse(pinned.Value.Rows.Count(r => r.Redacted));
            foreach (var r in pinned.Value.Rows.DistinctBy(r => r.Number))
            {
                var info = await documents.GetAsync(subject, r.Number!, corr, ct);
                if (!info.IsOk) return Refuse(1);
                var i = info.Value!;
                docs.Add(new PackageDocument(i.Number, i.Name, i.DocumentType, r.Revision ?? i.Revision, r.State ?? i.State, r.ItemPath, i.Markings, [.. i.Files.Select(f => new PackageFile(f.FileName, f.SizeBytes, f.Sha256))]));
            }
        }
        var signatures = await SignaturesAsync(subject, "baseline", s.Number, "", corr, ct);
        return Outcome<ReleasePackageContent>.Ok(new ReleasePackageContent("baseline", s.Number, s.Name, "", objects, [.. lines.Select(Line)], docs, signatures, []));
    }

    public async Task<bool> CanReadAllAsync(Subject subject, IReadOnlyList<PackageObjectRef> objects, CancellationToken ct = default)
    {
        foreach (var o in objects)
        {
            if (!byKind.TryGetValue(o.ObjectType, out var a)) return false;
            var info = await a.FindAsync(o.Key, ct);
            if (info is null || !AccessPolicy.Evaluate(subject, info.Markings, PlmAction.Read).Permitted) return false;
        }
        return true;
    }
}

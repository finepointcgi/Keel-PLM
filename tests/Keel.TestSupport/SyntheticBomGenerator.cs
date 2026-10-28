using System.Globalization;
using Keel.Core;
using Npgsql;

namespace Keel.TestSupport;

/// <summary>Knobs of the deterministic synthetic structure generator. The same options always produce the same parts, lines and markings.</summary>
public sealed record SyntheticBomOptions
{
    public int Seed { get; init; } = 42;
    /// <summary>Depth of the structure below the root: the deepest lines are at this level.</summary>
    public int Levels { get; init; } = 10;
    /// <summary>Total usage lines (equals the explosion size, because only leaf parts are shared between parents).</summary>
    public int TargetLines { get; init; } = 50_000;
    public int RootChildren { get; init; } = 24;
    /// <summary>Share of the lines on each non-leaf level that are sub-assemblies.</summary>
    public double AssemblyFraction { get; init; } = 0.7;
    /// <summary>Standard parts (fasteners and the like) reused by many parents; they make where-used interesting.</summary>
    public int SharedPartCount { get; init; } = 40;
    public double SharedFraction { get; init; } = 0.35;
    /// <summary>Probability that a generated part is open, ITAR (program ORCA) or EAR (ECCN 9A610.x); the remainder is ITAR without a program.</summary>
    public double OpenFraction { get; init; } = 0.60;
    public double ItarProgramFraction { get; init; } = 0.20;
    public double EarFraction { get; init; } = 0.12;
    /// <summary>Share of revisions put into the Released state (so LatestReleased resolves them).</summary>
    public double ReleasedFraction { get; init; }
    public string NumberPrefix { get; init; } = "S";
    public string Program { get; init; } = "ORCA";
}

public sealed record SyntheticPart(Guid MasterId, Guid IterationId, string Number, string PartType, Markings Markings, bool Released);
public sealed record SyntheticLine(string ParentNumber, string ChildNumber, int Level, int FindNumber, decimal Quantity, string Unit);

public sealed record SyntheticBom(
    string RootNumber,
    IReadOnlyList<SyntheticPart> Parts,
    IReadOnlyList<SyntheticLine> Lines,
    IReadOnlyList<int> LinesPerLevel,
    IReadOnlyList<string> SharedPartNumbers)
{
    public IReadOnlyDictionary<string, SyntheticPart> ByNumber { get; } = Parts.ToDictionary(p => p.Number, StringComparer.Ordinal);
}

/// <summary>
/// Builds large BOM structures with bulk SQL (unnest inserts), not per-object service calls: one root assembly with
/// <see cref="SyntheticBomOptions.Levels"/> levels, a mix of open, ITAR and EAR parts, and shared standard parts. Synthetic data only.
/// Reused by the 1.7 demo data. Inserts through the application role, so every database guard still applies.
/// </summary>
public static class SyntheticBomGenerator
{
    public static SyntheticBom Generate(SyntheticBomOptions o)
    {
        var rng = new Random(o.Seed);
        var plan = PlanLevels(o);
        var parts = new List<SyntheticPart>();
        var lines = new List<SyntheticLine>();
        var counter = 0;

        SyntheticPart NewPart(string kind, string type, bool forceOpen = false)
        {
            counter++;
            var number = $"{o.NumberPrefix}{kind}-{counter.ToString("D6", CultureInfo.InvariantCulture)}";
            var part = new SyntheticPart(NextGuid(rng), NextGuid(rng), number, type, PickMarkings(o, rng, forceOpen), false);
            parts.Add(part);
            return part;
        }

        var shared = Enumerable.Range(0, o.SharedPartCount)
            .Select(_ => NewPart("S", "Standard", forceOpen: true)).ToList();
        var root = NewPart("R", "Assembly", forceOpen: true);

        var assembliesAtLevel = new List<SyntheticPart> { root };
        for (var level = 1; level <= o.Levels; level++)
        {
            var count = plan[level];
            var isLeafLevel = level == o.Levels;
            var assemblyCount = isLeafLevel ? 0 : AssemblyCount(o, count);
            // Which child slots on this level are sub-assemblies: shuffled so every parent gets a mix.
            var slotIsAssembly = Enumerable.Range(0, count).Select(i => i < assemblyCount).OrderBy(_ => rng.Next()).ToList();
            var perParent = Distribute(count, assembliesAtLevel.Count, rng);

            var next = new List<SyntheticPart>();
            var slot = 0;
            for (var p = 0; p < assembliesAtLevel.Count; p++)
            {
                var parent = assembliesAtLevel[p];
                var used = new HashSet<string>(StringComparer.Ordinal);
                for (var k = 0; k < perParent[p]; k++, slot++)
                {
                    SyntheticPart child;
                    if (slotIsAssembly[slot])
                    {
                        child = NewPart("A", "Assembly");
                        next.Add(child);
                    }
                    else
                    {
                        child = PickSharedOrNew(o, rng, shared, used, () => NewPart("C", rng.Next(10) == 0 ? "RawMaterial" : "Component"));
                    }
                    used.Add(child.Number);
                    var isRaw = child.PartType == "RawMaterial";
                    var unit = isRaw ? "mm" : "EA";
                    var qty = isRaw ? Math.Round((decimal)(rng.Next(100, 20000) / 10.0), 1) : rng.Next(1, 9);
                    lines.Add(new SyntheticLine(parent.Number, child.Number, level, (k + 1) * 10, qty, unit));
                }
            }
            assembliesAtLevel = next;
        }

        // Which revisions are Released (applied after the structure exists, because released revisions are locked).
        if (o.ReleasedFraction > 0)
            for (var i = 0; i < parts.Count; i++)
                if (rng.NextDouble() < o.ReleasedFraction) parts[i] = parts[i] with { Released = true };

        return new SyntheticBom(root.Number, parts, lines, plan.Skip(1).ToList(), shared.Select(s => s.Number).ToList());
    }

    /// <summary>Generates and inserts in one transaction: masters, one revision A with one iteration each, usage lines, then released states.</summary>
    public static async Task<SyntheticBom> InsertAsync(NpgsqlDataSource dataSource, SyntheticBomOptions options, CancellationToken ct = default)
    {
        var bom = Generate(options);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var parts = bom.Parts;

        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO plm.part_master (id, number, name, part_type, default_unit, source, lifecycle_id, export_jurisdiction, eccn, usml_category,
                cui_categories, distribution_statement, controlling_office, distribution_reason, program, owning_org, export_authorizations, created_at, created_by)
            SELECT t.id, t.number, t.name, t.part_type, CASE WHEN t.part_type = 'RawMaterial' THEN 'mm' ELSE 'EA' END, 'Make', 'part-default',
                   t.jur, t.eccn, t.usml, string_to_array(t.cui, ','), t.dist, t.office, t.reason, t.program, 'ENG', '{}', now(), 'synthetic'
            FROM unnest(@ids, @numbers, @names, @types, @jur, @eccn, @usml, @cui, @dist, @office, @reason, @program)
                 AS t(id, number, name, part_type, jur, eccn, usml, cui, dist, office, reason, program)
            """, conn, tx))
        {
            cmd.CommandTimeout = 300;
            cmd.Parameters.AddWithValue("ids", parts.Select(p => p.MasterId).ToArray());
            cmd.Parameters.AddWithValue("numbers", parts.Select(p => p.Number).ToArray());
            cmd.Parameters.AddWithValue("names", parts.Select(p => $"Synthetic {p.PartType.ToLowerInvariant()} {p.Number}").ToArray());
            cmd.Parameters.AddWithValue("types", parts.Select(p => p.PartType).ToArray());
            cmd.Parameters.AddWithValue("jur", parts.Select(p => p.Markings.ExportJurisdiction.ToString()).ToArray());
            cmd.Parameters.AddWithValue("eccn", parts.Select(p => p.Markings.Eccn).ToArray());
            cmd.Parameters.AddWithValue("usml", parts.Select(p => p.Markings.UsmlCategory).ToArray());
            cmd.Parameters.AddWithValue("cui", parts.Select(p => string.Join(',', p.Markings.CuiCategories)).ToArray());
            cmd.Parameters.AddWithValue("dist", parts.Select(p => p.Markings.DistributionStatement.ToString()).ToArray());
            cmd.Parameters.AddWithValue("office", parts.Select(p => p.Markings.ControllingOffice).ToArray());
            cmd.Parameters.AddWithValue("reason", parts.Select(p => p.Markings.DistributionReason).ToArray());
            cmd.Parameters.AddWithValue("program", parts.Select(p => p.Markings.Program).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var revisionIds = parts.Select(_ => System.Guid.NewGuid()).ToArray();
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO plm.part_revision (id, master_id, revision, lifecycle_state, is_latest, latest_iteration, created_at, created_by, state_changed_at, state_changed_by)
            SELECT t.rid, t.mid, 'A', 'InWork', true, 1, now(), 'synthetic', now(), 'synthetic' FROM unnest(@rids, @mids) AS t(rid, mid);
            INSERT INTO plm.part_iteration (id, revision_id, iteration, description, attributes, comment, created_at, created_by)
            SELECT t.iid, t.rid, 1, 'Synthetic', '{}'::jsonb, 'Generated', now(), 'synthetic' FROM unnest(@iids, @rids) AS t(iid, rid)
            """, conn, tx))
        {
            cmd.CommandTimeout = 300;
            cmd.Parameters.AddWithValue("rids", revisionIds);
            cmd.Parameters.AddWithValue("mids", parts.Select(p => p.MasterId).ToArray());
            cmd.Parameters.AddWithValue("iids", parts.Select(p => p.IterationId).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var lines = bom.Lines;
        var byNumber = bom.ByNumber;
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO plm.bom_usage (id, parent_iteration_id, child_master_id, quantity, unit, find_number, reference_designators, notes, created_at, created_by)
            SELECT t.id, t.parent_iter, t.child, t.qty, t.unit, t.find, '{}', NULL, now(), 'synthetic'
            FROM unnest(@ids, @parents, @children, @qty, @unit, @find) AS t(id, parent_iter, child, qty, unit, find)
            """, conn, tx))
        {
            cmd.CommandTimeout = 300;
            cmd.Parameters.AddWithValue("ids", lines.Select(_ => System.Guid.NewGuid()).ToArray());
            cmd.Parameters.AddWithValue("parents", lines.Select(l => byNumber[l.ParentNumber].IterationId).ToArray());
            cmd.Parameters.AddWithValue("children", lines.Select(l => byNumber[l.ChildNumber].MasterId).ToArray());
            cmd.Parameters.AddWithValue("qty", lines.Select(l => l.Quantity).ToArray());
            cmd.Parameters.AddWithValue("unit", lines.Select(l => l.Unit).ToArray());
            cmd.Parameters.AddWithValue("find", lines.Select(l => l.FindNumber).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var released = parts.Where(p => p.Released).Select(p => p.MasterId).ToArray();
        if (released.Length > 0)
        {
            await using var cmd = new NpgsqlCommand("UPDATE plm.part_revision SET lifecycle_state = 'Released' WHERE master_id = ANY(@m)", conn, tx);
            cmd.Parameters.AddWithValue("m", released);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return bom;
    }

    private static int[] PlanLevels(SyntheticBomOptions o)
    {
        if (o.Levels < 1) throw new ArgumentOutOfRangeException(nameof(o), "At least one level is required.");
        int[] Build(double growth)
        {
            var n = new int[o.Levels + 1];
            n[1] = o.RootChildren;
            for (var l = 1; l < o.Levels; l++) n[l + 1] = Math.Max(1, (int)Math.Min(5_000_000, Math.Round(AssemblyCount(o, n[l]) * growth)));
            return n;
        }
        double lo = 0.5, hi = 40;
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            if (Build(mid).Sum(x => (long)x) < o.TargetLines) lo = mid; else hi = mid;
        }
        var plan = Build(lo);
        plan[o.Levels] = Math.Max(1, plan[o.Levels] + o.TargetLines - plan.Sum());
        return plan;
    }

    private static int AssemblyCount(SyntheticBomOptions o, int lines) => Math.Max(1, (int)Math.Round(lines * o.AssemblyFraction));

    /// <summary>Splits <paramref name="total"/> children over parents as evenly as possible, giving the remainder to random parents.</summary>
    private static int[] Distribute(int total, int parents, Random rng)
    {
        var counts = new int[parents];
        Array.Fill(counts, total / parents);
        foreach (var i in Enumerable.Range(0, parents).OrderBy(_ => rng.Next()).Take(total % parents)) counts[i]++;
        return counts;
    }

    private static SyntheticPart PickSharedOrNew(SyntheticBomOptions o, Random rng, List<SyntheticPart> shared, HashSet<string> used, Func<SyntheticPart> create)
    {
        if (shared.Count > 0 && rng.NextDouble() < o.SharedFraction)
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var candidate = shared[rng.Next(shared.Count)];
                if (!used.Contains(candidate.Number)) return candidate;
            }
        return create();
    }

    private static Markings PickMarkings(SyntheticBomOptions o, Random rng, bool forceOpen)
    {
        var roll = forceOpen ? 0 : rng.NextDouble();
        if (roll < o.OpenFraction)
            return Markings.Create(ExportJurisdiction.None, DistributionStatement.A, "ENG").Value;
        if (roll < o.OpenFraction + o.ItarProgramFraction)
            return Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "XI(d)", cuiCategories: ["EXPT"],
                controllingOffice: "ACME", distributionReason: "Critical technology", program: o.Program).Value;
        if (roll < o.OpenFraction + o.ItarProgramFraction + o.EarFraction)
            return Markings.Create(ExportJurisdiction.Ear, DistributionStatement.D, "ENG", eccn: "9A610.x", cuiCategories: ["EXPT"],
                controllingOffice: "ACME", distributionReason: "Export controlled").Value;
        return Markings.Create(ExportJurisdiction.Itar, DistributionStatement.D, "ENG", usmlCategory: "VIII(h)", cuiCategories: ["EXPT"],
            controllingOffice: "ACME", distributionReason: "Critical technology").Value;
    }

    private static Guid NextGuid(Random rng)
    {
        var bytes = new byte[16];
        rng.NextBytes(bytes);
        return new Guid(bytes);
    }
}

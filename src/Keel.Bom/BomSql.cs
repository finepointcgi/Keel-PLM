using Keel.Security;

namespace Keel.Bom;

/// <summary>The SQL of the BOM read paths (ADR-0017). Every statement over plm.part_master interpolates the read filter.</summary>
internal static class BomSql
{
    /// <summary>
    /// SQL for "this range (alias) covers the requested unit". Parameters: @eff_ctx, @eff_serial, @eff_lot, @eff_day. A range of a dimension the
    /// request does not give is not applicable (CFG-5).
    /// </summary>
    public static string Covers(string a) =>
        $"""
        (({a}.eff_kind = 'serial' AND @eff_serial::bigint IS NOT NULL AND {a}.eff_context = @eff_ctx::text AND ({a}.eff_from IS NULL OR {a}.eff_from <= @eff_serial::bigint) AND ({a}.eff_to IS NULL OR {a}.eff_to >= @eff_serial::bigint))
          OR ({a}.eff_kind = 'lot' AND @eff_lot::bigint IS NOT NULL AND {a}.eff_context = @eff_ctx::text AND ({a}.eff_from IS NULL OR {a}.eff_from <= @eff_lot::bigint) AND ({a}.eff_to IS NULL OR {a}.eff_to >= @eff_lot::bigint))
          OR ({a}.eff_kind = 'date' AND @eff_day::bigint IS NOT NULL AND ({a}.eff_from IS NULL OR {a}.eff_from <= @eff_day::bigint) AND ({a}.eff_to IS NULL OR {a}.eff_to >= @eff_day::bigint)))
        """;

    /// <summary>A usage line applies to the requested unit when it has no effectivity or its range covers the unit.</summary>
    public static string UsageApplies(string u) => $"({u}.eff_kind IS NULL OR {Covers(u)})";

    /// <summary>
    /// Lateral subquery resolving a child master's revision and iteration under a config spec (ADR-0003, ADR-0021).
    /// LatestReleased picks the highest revision in state Released: ASME revision labels order by (length, text). Effectivity prefers the Released
    /// revision whose effectivity covers the unit and otherwise falls back to the highest Released revision.
    /// </summary>
    public static string Resolve(BomConfigSpec spec, string masterExpr) => spec switch
    {
        BomConfigSpec.Latest =>
            $"""
            SELECT r.id AS rev_id, i.id AS iter_id
            FROM plm.part_revision r
            JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE r.master_id = {masterExpr} AND r.is_latest
            """,
        BomConfigSpec.LatestReleased =>
            $"""
            SELECT r.id AS rev_id, i.id AS iter_id
            FROM plm.part_revision r
            JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            WHERE r.master_id = {masterExpr} AND r.lifecycle_state = 'Released'
            ORDER BY length(r.revision) DESC, r.revision DESC
            LIMIT 1
            """,
        BomConfigSpec.Effectivity =>
            $"""
            SELECT r.id AS rev_id, i.id AS iter_id
            FROM plm.part_revision r
            JOIN plm.part_iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
            LEFT JOIN cfg.revision_effectivity e ON e.revision_id = r.id
            WHERE r.master_id = {masterExpr} AND r.lifecycle_state = 'Released'
            ORDER BY (e.revision_id IS NOT NULL AND {Covers("e")}) DESC, length(r.revision) DESC, r.revision DESC
            LIMIT 1
            """,
        _ => throw new NotSupportedException($"Configuration spec '{spec}' has no per-level resolution."),
    };

    /// <summary>The usage-line condition of a spec: only Effectivity filters lines.</summary>
    private static string LineCondition(BomConfigSpec spec) => spec == BomConfigSpec.Effectivity ? UsageApplies("u") : "TRUE";

    private static string ChildCondition(BomConfigSpec spec) => spec == BomConfigSpec.Effectivity ? UsageApplies("c") : "TRUE";

    /// <summary>Revision of a root master for a unit: covering Released revision first, else the highest Released one.</summary>
    public static readonly string ResolveRootRevision =
        $"""
        SELECT r.revision FROM plm.part_revision r
        LEFT JOIN cfg.revision_effectivity e ON e.revision_id = r.id
        WHERE r.master_id = @m AND r.lifecycle_state = 'Released'
        ORDER BY (e.revision_id IS NOT NULL AND {Covers("e")}) DESC, length(r.revision) DESC, r.revision DESC
        LIMIT 1
        """;

    /// <summary>
    /// One recursive CTE: the config spec is resolved per level and the access filter is evaluated inside the recursion, so a
    /// subtree below a line the caller cannot read is never traversed. A pseudo row seeds the recursion (root or one lazily
    /// expanded line). Identifying columns of unreadable lines are nulled in the select list, so they never leave the database.
    /// </summary>
    public static string Explode(BomConfigSpec spec, SqlFilter filter) =>
        $"""
        WITH RECURSIVE tree AS (
            SELECT NULL::uuid AS line_id, NULL::uuid AS parent_line_id, @base_level::int AS lvl, @base_path::int[] AS path, @base_alts::int[] AS alts,
                   @base_tpath::text AS tpath, @base_ext::numeric AS ext, NULL::uuid AS master_id, @seed_iter::uuid AS iter_id, NULL::uuid AS rev_id,
                   true AS readable, false AS cyc, @base_anc::uuid[] AS anc, NULL::numeric AS qty, NULL::text AS unit,
                   NULL::int AS find, NULL::text[] AS refdes, NULL::text AS notes,
                   NULL::text AS ek, NULL::text AS ec, NULL::bigint AS ef, NULL::bigint AS et
            UNION ALL
            SELECT u.id, t.line_id, t.lvl + 1, t.path || u.find_number, t.alts || u.alt,
                   t.tpath || CASE WHEN t.tpath = '' THEN '' ELSE '.' END || u.find_number::text || CASE WHEN u.alt > 0 THEN '~' || u.alt::text ELSE '' END,
                   round(LEAST(t.ext * u.quantity, 1e18::numeric), 6),
                   u.child_master_id, res.iter_id, res.rev_id, f.ok, u.child_master_id = ANY(t.anc), t.anc || u.child_master_id,
                   u.quantity, u.unit, u.find_number, u.reference_designators, u.notes, u.eff_kind, u.eff_context, u.eff_from, u.eff_to
            FROM tree t
            JOIN plm.bom_usage u ON u.parent_iteration_id = t.iter_id AND {LineCondition(spec)}
            JOIN plm.part_master cm ON cm.id = u.child_master_id
            CROSS JOIN LATERAL (SELECT {filter.Sql} AS ok) f
            LEFT JOIN LATERAL ({Resolve(spec, "u.child_master_id")}) res ON f.ok
            WHERE t.readable AND NOT t.cyc AND t.lvl < @max_level
        )
        SELECT t.line_id, t.parent_line_id, t.lvl, t.tpath, t.ext, t.qty, t.unit, t.find, t.readable, t.cyc,
               (t.readable AND NOT t.cyc AND EXISTS (SELECT 1 FROM plm.bom_usage c WHERE c.parent_iteration_id = t.iter_id AND {ChildCondition(spec)})),
               cm.id, cm.number, cm.name, cm.part_type, r.revision, i.iteration, r.lifecycle_state, ls.label,
               CASE WHEN t.readable THEN t.refdes END, CASE WHEN t.readable THEN t.notes END,
               {MarkingColumns.Select("cm")}, t.ek, t.ec, t.ef, t.et, t.master_id, t.rev_id, t.iter_id
        FROM tree t
        LEFT JOIN plm.part_master cm ON cm.id = t.master_id AND t.readable AND {filter.Sql}
        LEFT JOIN plm.part_revision r ON r.id = t.rev_id AND cm.id IS NOT NULL
        LEFT JOIN plm.part_iteration i ON i.id = t.iter_id AND cm.id IS NOT NULL
        LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = cm.lifecycle_id AND ls.state = r.lifecycle_state
        WHERE t.line_id IS NOT NULL
        ORDER BY t.path, t.alts
        LIMIT @limit
        """;

    /// <summary>One hop of lazy expansion: the readable, resolved child at a find number under a parent iteration.</summary>
    public static string Hop(BomConfigSpec spec, SqlFilter filter) =>
        $"""
        SELECT u.child_master_id, u.quantity, res.iter_id
        FROM plm.bom_usage u
        JOIN plm.part_master cm ON cm.id = u.child_master_id AND {filter.Sql}
        LEFT JOIN LATERAL ({Resolve(spec, "u.child_master_id")}) res ON true
        WHERE u.parent_iteration_id = @parent AND u.find_number = @find AND u.alt = @alt AND {LineCondition(spec)}
        """;

    /// <summary>
    /// Where-used. The chain CTE holds the distinct masters at each level and only expands through parents the caller can read
    /// (the filter is in the recursion); the select then lists every parent usage, hiding unreadable parents.
    /// A parent is a revision whose latest iteration contains the child.
    /// </summary>
    public static string WhereUsed(SqlFilter parentFilter, SqlFilter usedFilter, bool effectivity = false)
    {
        // With a unit, only lines that apply to it count, and the parent must be the revision the unit resolves to (ADR-0021).
        var lineCondition = effectivity ? $" AND {UsageApplies("u")}" : "";
        var parentJoin = effectivity
            ? $"JOIN plm.part_revision pr ON pr.id = pi.revision_id JOIN LATERAL ({Resolve(BomConfigSpec.Effectivity, "pr.master_id")}) pres ON pres.rev_id = pr.id AND pres.iter_id = pi.id"
            : "JOIN plm.part_revision pr ON pr.id = pi.revision_id AND pr.latest_iteration = pi.iteration";
        return $"""
        WITH RECURSIVE chain(lvl, master_id) AS (
            SELECT 0, @target::uuid
            UNION
            SELECT c.lvl + 1, pr.master_id
            FROM chain c
            JOIN plm.bom_usage u ON u.child_master_id = c.master_id{lineCondition}
            JOIN plm.part_iteration pi ON pi.id = u.parent_iteration_id
            {parentJoin}
            JOIN plm.part_master pm ON pm.id = pr.master_id AND {parentFilter.Sql}
            WHERE c.lvl < @levels - 1
        )
        SELECT c.lvl + 1, u.find_number, u.quantity, u.unit, um.number,
               pm.number, pm.name, pr.revision, pi.iteration, pr.lifecycle_state, ls.label, pm.id,
               {MarkingColumns.Select("pm")}, u.eff_kind, u.eff_context, u.eff_from, u.eff_to
        FROM chain c
        JOIN plm.bom_usage u ON u.child_master_id = c.master_id{lineCondition}
        JOIN plm.part_iteration pi ON pi.id = u.parent_iteration_id
        {parentJoin}
        LEFT JOIN plm.part_master pm ON pm.id = pr.master_id AND {parentFilter.Sql}
        LEFT JOIN plm.part_master um ON um.id = c.master_id AND {usedFilter.Sql}
        LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = pm.lifecycle_id AND ls.state = pr.lifecycle_state
        ORDER BY c.lvl, pm.number NULLS LAST, length(pr.revision), pr.revision, u.find_number, u.alt
        LIMIT @limit
        """;
    }

    /// <summary>Lines of one iteration for the comparison; children the caller cannot read come back with null part columns.</summary>
    public static string IterationLines(SqlFilter filter) =>
        $"""
        SELECT u.child_master_id, u.find_number, u.quantity, u.unit, u.reference_designators, u.notes,
               cm.id, cm.number, cm.name, {MarkingColumns.Select("cm")}, u.eff_kind, u.eff_context, u.eff_from, u.eff_to
        FROM plm.bom_usage u
        LEFT JOIN plm.part_master cm ON cm.id = u.child_master_id AND {filter.Sql}
        WHERE u.parent_iteration_id = @iter
        ORDER BY u.find_number, u.alt
        """;

    public static string WorkingLines(SqlFilter filter, bool single) =>
        $"""
        SELECT w.id, w.find_number, w.quantity, w.unit, w.reference_designators, w.notes, w.child_master_id,
               cm.id, cm.number, cm.name, cm.part_type, {MarkingColumns.Select("cm")}, w.eff_kind, w.eff_context, w.eff_from, w.eff_to
        FROM plm.bom_working_usage w
        LEFT JOIN plm.part_master cm ON cm.id = w.child_master_id AND {filter.Sql}
        WHERE w.checkout_revision_id = @rid{(single ? " AND w.id = @id" : "")}
        ORDER BY w.find_number, w.eff_from NULLS FIRST, w.created_at
        """;

    /// <summary>Masters that contain @parent through their latest structure, each with the lower master it was found through.</summary>
    public const string Ancestors =
        """
        WITH RECURSIVE up(master_id, via) AS (
            SELECT @parent::uuid, NULL::uuid
            UNION
            SELECT r.master_id, up.master_id
            FROM up
            JOIN plm.bom_usage u ON u.child_master_id = up.master_id
            JOIN plm.part_iteration i ON i.id = u.parent_iteration_id
            JOIN plm.part_revision r ON r.id = i.revision_id AND r.is_latest AND r.latest_iteration = i.iteration
        )
        SELECT master_id, via FROM up
        """;
}

using Keel.Security;

namespace Keel.Bom;

/// <summary>The SQL of the MBOM read paths (ADR-0024), modelled on <see cref="BomSql"/>: the read filter sits inside the recursion, unreadable lines are nulled in the select list.</summary>
internal static class MbomSql
{
    /// <summary>
    /// The MBOM of a Make sub-assembly in the same plant: its latest released revision (or its latest revision), at that revision's latest iteration. Only an MBOM the
    /// caller may read resolves; otherwise the line is a leaf (the caller learns nothing about an MBOM it cannot read).
    /// </summary>
    public static string ResolveChild(bool latestChildren, SqlFilter mbomFilter, string masterExpr) =>
        $"""
        SELECT r.id AS rev_id, i.id AS iter_id
        FROM mbom.mbom mm
        JOIN mbom.revision r ON r.mbom_id = mm.id
        JOIN mbom.iteration i ON i.revision_id = r.id AND i.iteration = r.latest_iteration
        WHERE mm.part_master_id = {masterExpr} AND mm.plant = @plant AND {mbomFilter.Sql}
          AND {(latestChildren ? "r.is_latest" : "r.lifecycle_state = 'Released'")}
        ORDER BY length(r.revision) DESC, r.revision DESC
        LIMIT 1
        """;

    /// <summary>
    /// Option conditions of an MBOM line (ADR-0039) are inherited from the EBOM lines it implements: with a variant, a line applies when it implements no EBOM line
    /// (manufacturing only) or at least one linked EBOM line applies. MBOM lines carry no condition of their own. Parameter @var_sel is always bound.
    /// </summary>
    public static string VariantApplies(string u) =>
        $"""
        (@var_sel::jsonb IS NULL
          OR NOT EXISTS (SELECT 1 FROM mbom.usage_link vl WHERE vl.mbom_usage_id = {u}.id)
          OR EXISTS (SELECT 1 FROM mbom.usage_link vl JOIN plm.bom_usage ve ON ve.id = vl.ebom_usage_id
                     WHERE vl.mbom_usage_id = {u}.id AND (ve.opt_ast IS NULL OR var.eval_cond(ve.opt_ast, @var_sel::jsonb))))
        """;

    private static string LineCondition(bool unit) => (unit ? BomSql.UsageApplies("u") : "TRUE") + " AND " + VariantApplies("u");
    private static string ChildCondition(bool unit) => (unit ? BomSql.UsageApplies("c") : "TRUE") + " AND " + VariantApplies("c");

    public static string Explode(bool latestChildren, bool unit, SqlFilter partFilter, SqlFilter mbomFilter) =>
        $"""
        WITH RECURSIVE tree AS (
            SELECT NULL::uuid AS line_id, NULL::uuid AS parent_line_id, @base_level::int AS lvl, @base_path::int[] AS path, @base_alts::int[] AS alts,
                   @base_tpath::text AS tpath, @base_ext::numeric AS ext, @base_gross::numeric AS gross, NULL::uuid AS master_id, @seed_iter::uuid AS iter_id, NULL::uuid AS rev_id,
                   true AS readable, false AS cyc, @base_anc::uuid[] AS anc, NULL::numeric AS qty, NULL::text AS unit, NULL::int AS find, NULL::text[] AS refdes, NULL::text AS notes,
                   NULL::numeric AS scrap, NULL::boolean AS phantom, NULL::text AS supply, NULL::text AS op, NULL::boolean AS only,
                   NULL::text AS ek, NULL::text AS ec, NULL::bigint AS ef, NULL::bigint AS et
            UNION ALL
            SELECT u.id, t.line_id, t.lvl + 1, t.path || u.find_number, t.alts || u.alt,
                   t.tpath || CASE WHEN t.tpath = '' THEN '' ELSE '.' END || u.find_number::text || CASE WHEN u.alt > 0 THEN '~' || u.alt::text ELSE '' END,
                   round(LEAST(t.ext * u.quantity, 1e18::numeric), 6),
                   round(LEAST(t.gross * u.quantity * (1 + COALESCE(u.scrap_factor, 0)), 1e18::numeric), 6),
                   u.child_master_id, res.iter_id, res.rev_id, f.ok, u.child_master_id = ANY(t.anc), t.anc || u.child_master_id,
                   u.quantity, u.unit, u.find_number, u.reference_designators, u.notes, u.scrap_factor, u.phantom, u.supply_type, u.operation_ref, u.mbom_only,
                   u.eff_kind, u.eff_context, u.eff_from, u.eff_to
            FROM tree t
            JOIN mbom.usage u ON u.iteration_id = t.iter_id AND {LineCondition(unit)}
            JOIN plm.part_master cm ON cm.id = u.child_master_id
            CROSS JOIN LATERAL (SELECT {partFilter.Sql} AS ok) f
            LEFT JOIN LATERAL ({ResolveChild(latestChildren, mbomFilter, "u.child_master_id")}) res ON f.ok
            WHERE t.readable AND NOT t.cyc AND t.lvl < @max_level
        )
        SELECT t.line_id, t.parent_line_id, t.lvl, t.tpath, t.ext, t.gross, t.qty, t.unit, t.find, t.readable, t.cyc,
               (t.readable AND NOT t.cyc AND t.iter_id IS NOT NULL AND EXISTS (SELECT 1 FROM mbom.usage c WHERE c.iteration_id = t.iter_id AND {ChildCondition(unit)})),
               t.phantom, cm.id, cm.number, cm.name, cm.part_type, cm.source, t.scrap, t.supply, t.op, t.only,
               CASE WHEN t.readable THEN t.refdes END, CASE WHEN t.readable THEN t.notes END,
               r.revision, i.iteration, r.lifecycle_state, ls.label,
               {MarkingColumns.Select("cm")}, t.ek, t.ec, t.ef, t.et
        FROM tree t
        LEFT JOIN plm.part_master cm ON cm.id = t.master_id AND t.readable AND {partFilter.Sql}
        LEFT JOIN mbom.revision r ON r.id = t.rev_id AND cm.id IS NOT NULL
        LEFT JOIN mbom.iteration i ON i.id = t.iter_id AND cm.id IS NOT NULL
        LEFT JOIN mbom.mbom mm ON mm.id = r.mbom_id AND {mbomFilter.Sql}
        LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = mm.lifecycle_id AND ls.state = r.lifecycle_state
        WHERE t.line_id IS NOT NULL
        ORDER BY t.path, t.alts
        LIMIT @limit
        """;

    /// <summary>One hop of lazy expansion: the readable child at a find number under a parent iteration and the iteration of its MBOM.</summary>
    public static string Hop(bool latestChildren, bool unit, SqlFilter partFilter, SqlFilter mbomFilter) =>
        $"""
        SELECT u.child_master_id, u.quantity, u.scrap_factor, res.iter_id
        FROM mbom.usage u
        JOIN plm.part_master cm ON cm.id = u.child_master_id AND {partFilter.Sql}
        LEFT JOIN LATERAL ({ResolveChild(latestChildren, mbomFilter, "u.child_master_id")}) res ON true
        WHERE u.iteration_id = @parent AND u.find_number = @find AND u.alt = @alt AND {LineCondition(unit)}
        """;

    /// <summary>Where a part is used in MBOMs (latest iteration of each MBOM's latest revision); an MBOM or part the caller may not read is a placeholder row.</summary>
    public static string WhereUsed(SqlFilter mbomFilter, SqlFilter partFilter) =>
        $"""
        SELECT u.find_number, u.quantity, u.unit, mm.number, pm.number, pm.name, mm.plant, r.revision, i.iteration, r.lifecycle_state, ls.label, u.supply_type, pm.id,
               {MarkingColumns.Select("mm")}
        FROM mbom.usage u
        JOIN mbom.iteration i ON i.id = u.iteration_id
        JOIN mbom.revision r ON r.id = i.revision_id AND r.is_latest AND r.latest_iteration = i.iteration
        JOIN mbom.mbom pl ON pl.id = r.mbom_id AND (@plant::text IS NULL OR pl.plant = @plant)
        LEFT JOIN mbom.mbom mm ON mm.id = r.mbom_id AND {mbomFilter.Sql}
        LEFT JOIN plm.part_master pm ON pm.id = mm.part_master_id AND {partFilter.Sql}
        LEFT JOIN plm.lifecycle_state ls ON ls.lifecycle_id = mm.lifecycle_id AND ls.state = r.lifecycle_state
        WHERE u.child_master_id = @target
        ORDER BY pl.plant, mm.number NULLS LAST, u.find_number
        LIMIT @limit
        """;
}

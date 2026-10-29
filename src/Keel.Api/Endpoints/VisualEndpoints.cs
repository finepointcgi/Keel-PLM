using System.Text;
using Keel.Api.Infrastructure;
using Keel.Audit;
using Keel.Change;
using Keel.Core;
using Keel.Web.Services.Visuals;

namespace Keel.Api.Endpoints;

/// <summary>
/// Downloads of the relationship pictures (UX-3, ADR-0029). Like the CSV exports, a plain same-origin link authenticated by the UI session cookie: no token in the URL, no
/// script. The picture is rebuilt on the server for the caller through the same services, read filter and audit as the page, so it can never contain more than the page shows.
/// </summary>
public static class VisualEndpoints
{
    public static void MapVisualEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/ui/changes/{number}/impact-map.svg", async (string number, int? levels, bool? documents, bool? mboms, bool? conflicts, bool? only,
                HttpContext ctx, ChangeService changes, ImpactAnalysisService impact, IAuditLog audit, CancellationToken ct) =>
            {
                var subject = SubjectFactory.Require(ctx);
                var corr = CorrelationId.Get(ctx);
                var detail = await changes.GetAsync(subject, number, corr, ct);
                if (!detail.IsOk) return Results.NotFound();
                var analysis = await impact.AnalyzeAsync(subject, number, ImpactAnalysisService.DefaultLevels, corr, ct);
                if (!analysis.IsOk) return Results.NotFound();

                var hidden = new HashSet<GraphNodeKind>();
                if (documents == false) hidden.Add(GraphNodeKind.Document);
                if (mboms == false) hidden.Add(GraphNodeKind.Mbom);
                if (conflicts == false) hidden.Add(GraphNodeKind.Conflict);
                var graph = ImpactGraphBuilder.Build(detail.Value!, analysis.Value!, new ImpactGraphOptions(Math.Clamp(levels ?? 3, 1, 10), hidden, only == true));
                var layout = LayeredLayout.Compute(
                    [.. graph.Nodes.Select(n => { var (w, h) = GraphGeometry.Size(n.Kind); return new LayoutNodeInput(n.Id, n.Layer, w, h); })],
                    [.. graph.Edges.Select(e => new LayoutEdgeInput(e.From, e.To))]);
                var svg = GraphSvgWriter.Write(graph, layout, $"Impact map {number}", subject.Username ?? subject.DisplayName, DateTimeOffset.UtcNow);

                await audit.AppendAsync(new AuditEntry
                {
                    Actor = ActorSnapshot.From(subject),
                    Action = "change.impact_export",
                    Outcome = AuditOutcome.Success,
                    ObjectType = ChangeTypes.ObjectTypeOf(detail.Value!.Type),
                    ObjectId = number,
                    CorrelationId = corr,
                    Details = AuditDetails.Of(("items", graph.Nodes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("banner", graph.Banner)),
                }, ct);
                return Results.File(Encoding.UTF8.GetBytes(svg), "image/svg+xml; charset=utf-8", $"{number}_impact-map.svg");
            })
            .RequireAuthorization(Policies.AnyPlmRole);
    }
}

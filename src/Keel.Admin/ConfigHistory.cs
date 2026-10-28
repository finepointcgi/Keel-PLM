using System.Text.Json;
using System.Text.Json.Nodes;
using Keel.Audit;
using Keel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Admin;

/// <summary>One configuration event from the audit log with its before and after values and a field-by-field difference.</summary>
public sealed record ConfigHistoryEntry(
    long Seq, DateTimeOffset At, string Actor, string Action, string? ObjectId, string Outcome, string? Before, string? After, string? Reason, IReadOnlyList<string> Diff,
    IReadOnlyDictionary<string, string?> Details);

/// <summary>
/// Configuration change history, read from the tamper-evident audit log (ADR-0005): every administrative change (action prefix <c>admin.</c>) and every
/// workflow template upload, including denied attempts. The events hold configuration only, never object data, so administrators may read them here;
/// the full audit log stays with the auditor role.
/// </summary>
public sealed class ConfigHistoryService(AdminRunner run, AuditQueries queries)
{
    public const int MaxLimit = 500;

    public async Task<Outcome<IReadOnlyList<ConfigHistoryEntry>>> ListAsync(Subject subject, int limit, string? actionContains, CancellationToken ct = default)
    {
        if (!await run.AdminOnlyAsync(subject, "admin.history_read", null, null, ct)) return Outcome<IReadOnlyList<ConfigHistoryEntry>>.Forbidden([Security.AccessPolicy.RoleMissing]);
        limit = Math.Clamp(limit, 1, MaxLimit);
        var events = (await queries.ListAsync(new AuditQuery(ObjectType: AdminRunner.ObjectType, Limit: AuditQueries.MaxLimit, NewestFirst: true), ct))
            .Concat(await queries.ListAsync(new AuditQuery(ObjectType: "workflow_template", Limit: 200, NewestFirst: true), ct))
            .OrderByDescending(e => e.Seq)
            .Where(e => actionContains is null || e.Action.Contains(actionContains, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(Parse)
            .ToList();
        return Outcome<IReadOnlyList<ConfigHistoryEntry>>.Ok(events);
    }

    internal static ConfigHistoryEntry Parse(AuditRecord e)
    {
        using var doc = JsonDocument.Parse(e.Payload);
        var details = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("details", out var d))
            foreach (var p in d.EnumerateObject()) details[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
        var actor = doc.RootElement.GetProperty("actor");
        var name = actor.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : actor.GetProperty("id").GetString()!;
        details.TryGetValue("before", out var before);
        details.TryGetValue("after", out var after);
        details.TryGetValue("reason", out var reason);
        return new ConfigHistoryEntry(e.Seq, e.OccurredAt, name, e.Action, e.ObjectId, e.Outcome.ToString(), before, after, reason, DiffOf(before, after), details);
    }

    /// <summary>"field: before → after" lines when both sides are JSON objects; otherwise one line with both values.</summary>
    internal static IReadOnlyList<string> DiffOf(string? before, string? after)
    {
        if (before is null && after is null) return [];
        try
        {
            var a = before is null ? new JsonObject() : JsonNode.Parse(before) as JsonObject;
            var b = after is null ? new JsonObject() : JsonNode.Parse(after) as JsonObject;
            if (a is not null && b is not null) return ConfigDiff.FieldDiff(a, b);
        }
        catch (JsonException)
        {
            // not JSON: shown as plain text below
        }
        return [$"{before ?? "(none)"} → {after ?? "(none)"}"];
    }
}

public static class AdminModule
{
    /// <summary>Registers the administration services, the cached reference-list provider and the classification participant.</summary>
    public static IServiceCollection AddKeelAdmin(this IServiceCollection services)
    {
        services.AddSingleton<AdminRunner>();
        services.AddSingleton<ReferenceDataService>();
        services.AddSingleton<IReferenceData>(sp => sp.GetRequiredService<ReferenceDataService>());
        services.AddSingleton<ReferenceAdminService>();
        services.AddSingleton<AttributeAdminService>();
        services.AddSingleton<LifecycleAdminService>();
        services.AddSingleton<NumberingAdminService>();
        services.AddSingleton<ReasonCodeAdminService>();
        services.AddSingleton<WorkflowAdminService>();
        services.AddSingleton<ClassificationService>();
        services.AddSingleton<ConfigTransferService>();
        services.AddSingleton<ConfigHistoryService>();
        services.AddSingleton<IPartIterationParticipant, ClassificationParticipant>();
        return services;
    }
}

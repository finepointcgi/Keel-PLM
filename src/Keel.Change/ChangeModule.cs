using System.Data.Common;
using Keel.Core;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Keel.Change;

/// <summary>
/// Answers the Parts module's question "does an open change notice own this revision?" without Parts referencing this module: the part
/// review workflow refuses to release such a revision on its own (ADR-0019).
/// </summary>
internal sealed class ChangeBindingGuard : IChangeBindingGuard
{
    public async Task<string?> BoundChangeAsync(DbConnection conn, DbTransaction tx, string objectType, Guid revisionId, CancellationToken ct)
    {
        Guid? holder;
        await using (var cmd = new NpgsqlCommand(
            "SELECT ao.change_id FROM chg.affected_object ao WHERE ao.object_type = @t AND ao.resulting_revision_id = @r AND ao.binding", (NpgsqlConnection)conn, (NpgsqlTransaction)tx))
        {
            cmd.Parameters.AddWithValue("t", objectType);
            cmd.Parameters.AddWithValue("r", revisionId);
            holder = (Guid?)await cmd.ExecuteScalarAsync(ct);
        }
        // Only the number is returned: enough to point the user at the notice, nothing else about it.
        return holder is null ? null : (await ChangeStore.LoadByIdAsync(conn, tx, holder.Value, forUpdate: false, ct))?.Number;
    }
}

public static class ChangeModule
{
    /// <summary>
    /// Registers the change service, impact analysis, the two review gateways (ECR for the CCB, ECN for release) and the binding guard.
    /// The host must also register an <see cref="IChangeObjectGateway"/> per changeable object type and the providers the impact analysis uses.
    /// </summary>
    public static IServiceCollection AddKeelChange(this IServiceCollection services)
    {
        services.AddSingleton(sp => new ChangeService(sp.GetRequiredService<Npgsql.NpgsqlDataSource>(), sp.GetRequiredService<Keel.Audit.IAuditLog>(),
            sp.GetServices<IChangeObjectGateway>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<IRevisionEffectivity>()));
        services.AddSingleton<ImpactAnalysisService>();
        services.AddSingleton<IChangeBindingGuard, ChangeBindingGuard>();
        services.AddSingleton<IWorkflowObjectGateway>(sp => new ChangeWorkflowGateway(ChangeTypes.Ecr, sp.GetRequiredService<Keel.Audit.IAuditLog>(), sp.GetServices<IChangeObjectGateway>(), sp.GetService<IRevisionEffectivity>()));
        services.AddSingleton<IWorkflowObjectGateway>(sp => new ChangeWorkflowGateway(ChangeTypes.Ecn, sp.GetRequiredService<Keel.Audit.IAuditLog>(), sp.GetServices<IChangeObjectGateway>(), sp.GetService<IRevisionEffectivity>()));
        return services;
    }
}

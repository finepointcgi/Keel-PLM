using Keel.Core;
using Npgsql;

namespace Keel.Admin;

public sealed partial class ClassificationService
{
    /// <summary>
    /// Creates missing classes inside the caller's transaction (the migration importer's): the same code path as the hierarchy import, so every rule about codes,
    /// depth and attribute names applies. The actor is the migration operator; no audit event is written here (the importer audits the batch).
    /// </summary>
    internal Task<ClassImportReport> ApplyForMigrationAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string actor, ClassHierarchyDoc doc, CancellationToken ct)
    {
        var subject = new Subject { Id = "migration:" + actor, DisplayName = actor + " (keel-import)", Username = actor };
        return ApplyAsync(conn, tx, subject, doc, ClassificationIO.Sha256(AdminRunner.ToJson(doc)), dryRun: false, ct);
    }
}

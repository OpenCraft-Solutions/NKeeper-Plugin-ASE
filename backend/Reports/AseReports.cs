using System.Data.Common;
using NKeeper.Plugins.Databases;
using Kinds = NKeeper.Plugins.Databases.NodeKinds;

namespace NKeeper.Plugins.Ase;

/// <summary>
/// 1.x's ASE Management reports (docs/architecture.md §7.35), on db-core 1.1, in the order 1.x's folders listed them:
/// activity, storage and backups, replication, security. Each reads only; the scripts it offers are never run.
/// </summary>
internal static class AseReports
{
    public static IReadOnlyList<DatabaseReportInfo> All =>
    [
        new(AseHealth.Resources, "Resources & Activity", "Limits, sessions, caches"),
        new(AseHealth.Errors, "Error Log", "Recent errors and deadlocks"),
        new(AseDba.Space, "Space", "Databases, logs and devices"),
        new(AseHealth.Dumps, "Backups (dumps)", "Last full and log dumps"),
        new(AseHealth.Maintenance, "Maintenance", "Big tables, stale statistics"),
        new(AseReplication.Tool, "Replication Health", "Rep Agents, logs, truncation points"),
        new(AseHealth.Security, "Logins & Users", "Locked logins, users without logins"),
        new(SybaseAdmin.License, "License", "SySAM licenses and expiry"),
    ];

    /// <summary>The reports as the explorer's Management tools: db-core opens <c>report:&lt;name&gt;</c>.</summary>
    public static IEnumerable<SqlObjectNode> ToolNodes =>
        All.Select(r => new SqlObjectNode { Kind = Kinds.Tool, Label = r.Label, Detail = r.Detail, Tool = $"report:{r.Name}" });

    public static Task<ToolReport> ReportAsync(DbConnection connection, string name, DateTimeOffset now, CancellationToken cancellationToken) => name switch
    {
        AseDba.Space => AseDba.SpaceAsync(connection, now, cancellationToken),
        AseReplication.Tool => AseReplication.PrimaryAsync(connection, now, cancellationToken),
        SybaseAdmin.License => SybaseAdmin.AseLicenseAsync(connection, now, cancellationToken),
        _ => AseHealth.ReportAsync(connection, name, now, cancellationToken),
    };
}

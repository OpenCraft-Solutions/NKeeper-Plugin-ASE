using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using NKeeper.Plugins.Databases;
using Kinds = NKeeper.Plugins.Databases.NodeKinds;

namespace NKeeper.Plugins.Ase;

/// <summary>
/// Replication health. On ASE (the primary side): each database's transaction log, whether replication
/// holds its truncation point, and the Rep Agents. On Replication Server: what's down, its threads, queues,
/// stable devices and warm standby, and resuming a suspended connection.
/// </summary>
internal static partial class AseReplication
{
    public const string Tool = "replication";

    /// <summary>A log at least this full is reported.</summary>
    private const double FullLogPercent = 80;

    /// <summary>Stable devices at least this full are reported: when they fill, replication stops.</summary>
    private const double FullQueuePercent = 70;


    // ---- ASE (primary) ----

    public static async Task<ToolReport> PrimaryAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var sections = new List<ToolSection>();

        const string logSql = """
            SELECT d.name,
                   (SELECT sum(u.size) FROM master..sysusages u WHERE u.dbid = d.dbid AND u.segmap & 4 = 4),
                   lct_admin('logsegment_freepages', d.dbid),
                   @@maxpagesize,
                   (SELECT min(h.starttime) FROM master..syslogshold h WHERE h.dbid = d.dbid AND h.name = '$replication_truncation_point'),
                   (SELECT min(h.spid) FROM master..syslogshold h WHERE h.dbid = d.dbid AND h.spid <> 0),
                   (SELECT min(h.starttime) FROM master..syslogshold h WHERE h.dbid = d.dbid AND h.spid <> 0)
            FROM master..sysdatabases d
            WHERE d.status2 & 16 = 0
            ORDER BY d.name
            """;
        try
        {
            var logs = await DbQuery.ListAsync(
                connection,
                logSql,
                [],
                r => new
                {
                    Name = DbQuery.Text(r, 0)!,
                    Pages = DbQuery.Long(r, 1) ?? 0,
                    FreePages = DbQuery.Long(r, 2) ?? 0,
                    PageBytes = DbQuery.Long(r, 3) ?? 2048,
                    ReplicationSince = r.IsDBNull(4) ? (DateTime?)null : Convert.ToDateTime(r.GetValue(4), CultureInfo.InvariantCulture),
                    OldestSpid = r.IsDBNull(5) ? (int?)null : DbQuery.Int(r, 5),
                    OldestSince = r.IsDBNull(6) ? (DateTime?)null : Convert.ToDateTime(r.GetValue(6), CultureInfo.InvariantCulture),
                },
                cancellationToken);

            var rows = new List<string?[]>();
            foreach (var log in logs)
            {
                var mb = (double)log.Pages * log.PageBytes / (1024 * 1024);
                var freeMb = (double)log.FreePages * log.PageBytes / (1024 * 1024);
                var usedPercent = log.Pages > 0 ? Math.Clamp(100 * (1 - ((double)log.FreePages / log.Pages)), 0, 100) : 0;
                rows.Add(
                [
                    log.Name,
                    mb.ToString("0.0", CultureInfo.InvariantCulture),
                    freeMb.ToString("0.0", CultureInfo.InvariantCulture),
                    usedPercent.ToString("0", CultureInfo.InvariantCulture),
                    log.ReplicationSince?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    log.OldestSpid is { } spid ? $"spid {spid} since {log.OldestSince:yyyy-MM-dd HH:mm:ss}" : null,
                ]);

                if (log.Pages > 0 && usedPercent >= FullLogPercent)
                {
                    var oldest = log.OldestSpid is { } holder ? $" The oldest open transaction (spid {holder}) has been open since {log.OldestSince:yyyy-MM-dd HH:mm}." : string.Empty;
                    warnings.Add((log.ReplicationSince is not null
                        ? $"{log.Name}: the log is {usedPercent:0}% full and replication holds its truncation point (since {log.ReplicationSince:yyyy-MM-dd HH:mm}). If the Rep Agent is behind or down, the log can't be truncated."
                        : $"{log.Name}: the log is {usedPercent:0}% full.") + oldest);
                }
            }

            IReadOnlyList<SqlColumn> columns =
            [
                new("Database", "varchar", false),
                new("Log MB", "numeric", true),
                new("Free MB", "numeric", true),
                new("Used %", "numeric", true),
                new("Replication point since", "datetime", false),
                new("Oldest open transaction", "varchar", false),
            ];
            sections.Add(new ToolSection(
                "Transaction logs",
                "master..sysusages, lct_admin('logsegment_freepages'), master..syslogshold",
                [new SqlOutput("results", Columns: columns, Rows: rows, RowCount: rows.Count)],
                null));
        }
        catch (Exception ex) when (AseSections.IsServerError(ex))
        {
            sections.Add(new ToolSection("Transaction logs", "master..sysusages, master..syslogshold", [], ex.Message));
        }

        sections.Add(await AseSections.RunAsync(connection, "Rep Agents", "sp_help_rep_agent", cancellationToken));
        return new ToolReport(warnings, sections, now);
    }

    // ---- Helpers ----

    /// <summary>A DSI thread's Info, e.g. "101 PDS.pdb" or "102(1) LDS.ldb": the connection is the server.database part.</summary>
    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_$#@]*\.[A-Za-z_][A-Za-z0-9_$#@]*")]
    private static partial Regex ConnectionInInfo();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$#@]*\.[A-Za-z_][A-Za-z0-9_$#@]*$")]
    private static partial Regex ConnectionName();
}

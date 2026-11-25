using System.Data.Common;
using System.Globalization;
using NKeeper.Plugins.Databases;
using Kinds = NKeeper.Plugins.Databases.NodeKinds;

namespace NKeeper.Plugins.Ase;

/// <summary>
/// SAP ASE's Space report, ported from 1.x's <c>AseDba</c>: databases' data and log, and devices. It only reads;
/// scripts it offers are run by the user. 1.x's Replication Drift and Copy Database aren't ported yet.
/// </summary>
internal static class AseDba
{
    public const string Space = "space";

    /// <summary>Databases that are the server's own: not offered as copies' targets, nor counted as user data.</summary>
    internal static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "master", "model", "tempdb", "sybsystemprocs", "sybsystemdb", "sybsecurity", "sybmgmtdb", "dbccdb", "dbccalt", "sybpcidb",
    };

    private const int MaxScripts = 20;


    // ---- Space ----

    /// <summary>A database's space, in MB: data (every fragment that isn't log-only) and log.</summary>
    internal sealed record DatabaseSpace(string Name, double DataMb, double DataFreeMb, double LogMb, double LogFreeMb, bool MixedLog);

    internal sealed record DeviceSpace(string Name, string Path, double SizeMb, double AllocatedMb)
    {
        public double FreeMb => Math.Max(0, SizeMb - AllocatedMb);
    }

    private const string PageMb = "(@@maxpagesize / 1048576.0)";

    internal static async Task<IReadOnlyList<DatabaseSpace>> DatabaseSpaceAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await DbQuery.ListAsync(
            connection,
            $"""
            SELECT d.name,
                   sum(CASE WHEN u.segmap <> 4 THEN u.size ELSE 0 END) * {PageMb},
                   sum(CASE WHEN u.segmap <> 4 THEN curunreservedpgs(u.dbid, u.lstart, u.unreservedpgs) ELSE 0 END) * {PageMb},
                   sum(CASE WHEN u.segmap = 4 THEN u.size ELSE 0 END) * {PageMb},
                   max(lct_admin('logsegment_freepages', d.dbid)) * {PageMb},
                   sum(CASE WHEN u.segmap & 4 = 4 AND u.segmap <> 4 THEN 1 ELSE 0 END)
            FROM master..sysdatabases d JOIN master..sysusages u ON u.dbid = d.dbid
            WHERE d.status2 & 16 = 0 AND d.status & 32 = 0
            GROUP BY d.name, d.dbid
            ORDER BY d.name
            """,
            [],
            r => new DatabaseSpace(
                DbQuery.Text(r, 0)!,
                Number(r, 1),
                Number(r, 2),
                Number(r, 3),
                Number(r, 4),
                DbQuery.Int(r, 5) > 0),
            cancellationToken);

    internal static async Task<IReadOnlyList<DeviceSpace>> DeviceSpaceAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await DbQuery.ListAsync(
            connection,
            $"""
            SELECT d.name, d.phyname, (d.high - d.low + 1) / 512.0,
                   isnull((SELECT sum(u.size) FROM master..sysusages u WHERE u.vdevno = d.vdevno), 0) * {PageMb}
            FROM master..sysdevices d
            WHERE d.status & 2 = 2
            ORDER BY d.name
            """,
            [],
            r => new DeviceSpace(DbQuery.Text(r, 0)!, DbQuery.Text(r, 1) ?? string.Empty, Number(r, 2), Number(r, 3)),
            cancellationToken);

    public static async Task<ToolReport> SpaceAsync(DbConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var sections = new List<ToolSection>();
        IReadOnlyList<DeviceSpace> devices = [];

        try
        {
            devices = await DeviceSpaceAsync(connection, cancellationToken);
        }
        catch (Exception ex) when (AseSections.IsServerError(ex))
        {
            // Shown as the devices section's error below.
        }

        var holds = new Dictionary<string, (DateTime? Transaction, int? Spid, DateTime? Replication)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var hold in await DbQuery.ListAsync(
                connection,
                """
                SELECT db_name(dbid),
                       min(CASE WHEN spid <> 0 THEN starttime END), min(CASE WHEN spid <> 0 THEN spid END),
                       min(CASE WHEN name = '$replication_truncation_point' THEN starttime END)
                FROM master..syslogshold GROUP BY dbid
                """,
                [],
                r => (Db: DbQuery.Text(r, 0) ?? string.Empty, Transaction: Date(r, 1), Spid: r.IsDBNull(2) ? (int?)null : DbQuery.Int(r, 2), Replication: Date(r, 3)),
                cancellationToken))
            {
                holds[hold.Db] = (hold.Transaction, hold.Spid, hold.Replication);
            }
        }
        catch (Exception ex) when (AseSections.IsServerError(ex))
        {
            // Without syslogshold, the warning just doesn't say what holds the log.
        }

        try
        {
            var databases = await DatabaseSpaceAsync(connection, cancellationToken);
            var rows = new List<string?[]>();
            var scripts = new List<ToolScript>();
            foreach (var db in databases)
            {
                var dataUsed = Percent(db.DataMb - db.DataFreeMb, db.DataMb);
                var logUsed = db.MixedLog ? (double?)null : Percent(db.LogMb - db.LogFreeMb, db.LogMb);
                rows.Add(
                [
                    db.Name,
                    Mb(db.DataMb),
                    Mb(db.DataFreeMb),
                    dataUsed?.ToString("0.0", CultureInfo.InvariantCulture),
                    db.MixedLog ? "on data" : Mb(db.LogMb),
                    db.MixedLog ? null : Mb(db.LogFreeMb),
                    logUsed?.ToString("0.0", CultureInfo.InvariantCulture),
                ]);

                if (dataUsed >= 85)
                {
                    warnings.Add($"{db.Name}: data is {dataUsed:0}% full ({Mb(db.DataFreeMb)} MB free).");
                    if (scripts.Count < MaxScripts && Roomiest(devices) is { } device)
                    {
                        scripts.Add(new ToolScript($"{db.Name}: add data space on {device.Name}", $"alter database {Quote(db.Name)} on {device.Name} = '{GrowBy(db.DataMb, device)}'"));
                    }
                }

                if (logUsed >= 80)
                {
                    var held = holds.TryGetValue(db.Name, out var h)
                        ? string.Join("; ", new[]
                        {
                            h.Transaction is { } t ? $"the oldest open transaction (spid {h.Spid}) has held it since {Stamp(t)}" : null,
                            h.Replication is { } r ? $"replication's truncation point has held it since {Stamp(r)}" : null,
                        }.Where(x => x is not null))
                        : string.Empty;
                    warnings.Add($"{db.Name}: the log is {logUsed:0}% full{(held.Length > 0 ? $": {held}" : "")}. Dump the transaction log, or end what holds it.");
                    if (scripts.Count < MaxScripts && Roomiest(devices) is { } device)
                    {
                        scripts.Add(new ToolScript($"{db.Name}: add log space on {device.Name}", $"alter database {Quote(db.Name)} log on {device.Name} = '{GrowBy(db.LogMb, device)}'"));
                    }

                    if (scripts.Count < MaxScripts)
                    {
                        scripts.Add(new ToolScript($"{db.Name}: dump the log", $"dump transaction {Quote(db.Name)} to '/path/to/{db.Name}_log.dmp'"));
                    }
                }

                if (db.MixedLog && dataUsed >= 85)
                {
                    warnings.Add($"{db.Name} keeps its log on its data devices: a full log and full data are the same thing there.");
                }
            }

            scripts.Add(new ToolScript("Space of one database (sp_helpdb)", "sp_helpdb dbname"));
            IReadOnlyList<SqlColumn> columns =
            [
                new("Database", "varchar", false),
                new("Data MB", "numeric", true),
                new("Data free MB", "numeric", true),
                new("Data used %", "numeric", true),
                new("Log MB", "numeric", true),
                new("Log free MB", "numeric", true),
                new("Log used %", "numeric", true),
            ];
            sections.Add(new ToolSection(
                "Databases",
                "master..sysusages, curunreservedpgs, lct_admin('logsegment_freepages')",
                [new SqlOutput("results", Columns: columns, Rows: rows, RowCount: rows.Count)],
                null,
                scripts,
                "A database whose log shares its data devices shows its log as “on data”: its data figures include the log."));
        }
        catch (Exception ex) when (AseSections.IsServerError(ex))
        {
            sections.Add(new ToolSection("Databases", "master..sysusages", [], ex.Message));
        }

        if (devices.Count > 0)
        {
            IReadOnlyList<SqlColumn> columns =
            [
                new("Device", "varchar", false),
                new("Path", "varchar", false),
                new("Size MB", "numeric", true),
                new("Allocated MB", "numeric", true),
                new("Free MB", "numeric", true),
            ];
            var rows = devices.Select(d => new[] { d.Name, d.Path, Mb(d.SizeMb), Mb(d.AllocatedMb), Mb(d.FreeMb) }).ToList<string?[]>();
            sections.Add(new ToolSection(
                "Devices",
                "master..sysdevices, master..sysusages",
                [new SqlOutput("results", Columns: columns, Rows: rows, RowCount: rows.Count)],
                null,
                [new ToolScript("New device (disk init)", "disk init name = 'data_dev2', physname = '/sybase/data/data_dev2.dat', size = '10G'")],
                "Free is space on the device not yet given to any database: alter database can use it."));
        }
        else
        {
            sections.Add(await AseSections.RunAsync(connection, "Devices", "sp_helpdevice", cancellationToken));
        }

        return new ToolReport(warnings, sections, now);
    }

    private static DeviceSpace? Roomiest(IReadOnlyList<DeviceSpace> devices) =>
        devices.Where(d => d.FreeMb >= 16 && !d.Name.Equals("master", StringComparison.OrdinalIgnoreCase)).OrderByDescending(d => d.FreeMb).FirstOrDefault();

    /// <summary>A quarter more (at least 256 MB), or what the device has left.</summary>
    private static string GrowBy(double currentMb, DeviceSpace device)
    {
        var mb = Math.Min(Math.Max(256, Math.Ceiling(currentMb / 4)), Math.Floor(device.FreeMb));
        return $"{mb.ToString("0", CultureInfo.InvariantCulture)}M";
    }

    // ---- Helpers ----

    internal static string Quote(string name) => AseDialect.Quote(name);

    private static double Number(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static DateTime? Date(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToDateTime(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>A server time, as the reports show them (the database server's own clock).</summary>
    private static string Stamp(DateTime? time) => time?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "an unknown time";

    /// <summary>A grid's date without its milliseconds.</summary>
    private static string Trim(string? cell) => cell is { Length: > 19 } ? cell[..19] : cell ?? "an unknown time";

    private static double? Percent(double part, double whole) => whole > 0 ? Math.Clamp(100 * part / whole, 0, 100) : null;

    internal static string Mb(double mb) => mb.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan span) =>
        span < TimeSpan.Zero ? "0s"
        : span.TotalDays >= 1 ? $"{span.TotalDays:0.#} days"
        : span.TotalHours >= 1 ? $"{span.TotalHours:0.#} h"
        : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0} min"
        : $"{span.TotalSeconds:0} s";
}

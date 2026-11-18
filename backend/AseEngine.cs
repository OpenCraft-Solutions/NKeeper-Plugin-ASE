using System.Data.Common;
using System.Text;
using AdoNetCore.AseClient;
using NKeeper.Abstractions;
using NKeeper.Plugins.Databases;

namespace NKeeper.Plugins.Ase;

/// <summary>
/// SAP (Sybase) ASE for db-core (docs/architecture.md §7.35), ported from 1.x's <c>AseConnector</c>: the service and a
/// login, which core signs in with AdoNetCore.AseClient (SDK 1.7). The password is sent encrypted when the server
/// supports it; the rest of the connection is as the server is set up (ASE's own SSL isn't used), as in 1.x.
/// </summary>
/// <remarks>
/// AdoNetCore.AseClient keeps the password in its connection string whatever it's told, so this plugin's manifest
/// declares <c>credentialRelease</c>, and opens its connections with <see cref="DatabaseConnectRequest.DriverKeepsPassword"/>
/// (SDK 1.10): core hands them over, and audits each login as <c>credential.reveal</c>.
/// </remarks>
public sealed class AseEngine(IConnectivity connectivity, ICredentialBroker broker) : IDatabaseEngine
{
    public const string PluginId = "ase";

    private const int ConnectTimeoutSeconds = 20;

    static AseEngine()
    {
        // ASE servers often use single-byte character sets such as iso_1 and cp850.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    string IDatabaseEngine.PluginId => PluginId;

    public string Product => "SAP ASE";

    public IDatabaseDialect Dialect { get; } = new AseDialect();

    public async Task<IDatabaseLogin> OpenAsync(StreamSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var handle = request.Credential ?? throw new ConnectException(ConnectFailure.NoSecret, "Choose a credential, or type one, to sign in to SAP ASE.");
        var described = await broker.DescribeAsync(request.Host, request.ServiceId, handle, cancellationToken);
        if (described.Domain is { Length: > 0 })
        {
            throw new ConnectException(ConnectFailure.AuthenticationFailed, "ASE logins have no domain: use a credential without one.");
        }

        var connect = new DatabaseConnectRequest(
            AseClientFactory.Instance,
            endpoint => new DbConnectionStringBuilder
            {
                ["Data Source"] = endpoint.Address,
                ["Port"] = endpoint.Port,
                ["ApplicationName"] = "NKeeper",
                ["LoginTimeOut"] = ConnectTimeoutSeconds,
                ["EncryptPassword"] = 1,
                // Each session holds its own connections; a pool would keep logins open after it ends.
                ["Pooling"] = false,
                ["TextSize"] = 65536,
                // The driver won't connect without a database; master is in every server and readable by every login.
                ["Database"] = "master",
            },
            "Uid",
            "Pwd")
        {
            ConnectTimeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds + 5),
            DriverKeepsPassword = true,
        };

        try
        {
            return await connectivity.OpenDatabaseAsync(request.Host, request.ServiceId, handle, connect, cancellationToken);
        }
        catch (ConnectException ex) when (ex.Reason == ConnectFailure.AuthenticationFailed && ex.Message.Contains("Login failed", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectException(ex.Reason, $"ASE refused the login “{described.Username}”. Check the username and password.");
        }
    }
}

/// <summary>What the ASE dialect adds for db-core: what the server says about itself, and its errors.</summary>
public sealed partial class AseDialect
{
    public async Task<DatabaseServerInfo> ServerInfoAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var row = (await DbQuery.ListAsync(
            connection,
            "SELECT @@servername, @@version, suser_name(), @@spid",
            [],
            r => (Name: DbQuery.Text(r, 0), Version: DbQuery.Text(r, 1), Login: DbQuery.Text(r, 2), Spid: DbQuery.Int(r, 3)),
            cancellationToken))[0];

        // e.g. "Adaptive Server Enterprise/16.0 SP03 PL08/EBF 29704 SMP/P/x86_64/SLES 12.4/…": 16.0 SP03 PL08.
        var parts = row.Version?.Split('/') ?? [];
        var version = parts.Length > 1 ? parts[1] : row.Version ?? string.Empty;
        return new DatabaseServerInfo(row.Name ?? connection.DataSource, version, null, parts.Length > 0 ? parts[0] : null, row.Login ?? string.Empty, row.Spid);
    }

    /// <summary>AdoNetCore.AseClient's errors are <see cref="AseException"/>s, which aren't <see cref="DbException"/>s.</summary>
    public bool IsServerError(Exception exception) => exception is AseException or DbException;
}

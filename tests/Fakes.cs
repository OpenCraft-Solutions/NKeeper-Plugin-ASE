using System.Data.Common;
using System.Text.Json;
using AdoNetCore.AseClient;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using NKeeper.Abstractions;

[assembly: AssemblyFixture(typeof(NKeeper.Plugins.Ase.Tests.AseFixture))]

namespace NKeeper.Plugins.Ase.Tests;

/// <summary>
/// A real SAP ASE 16.0 (datagrip/sybase in Docker), as 1.x's live tests used. Tests are skipped, not passed, when
/// Docker is unavailable.
/// </summary>
public sealed class AseFixture : IAsyncLifetime
{
    public const string Address = "127.0.0.1";
    public const string UserName = "sa";
    public const string Password = "myPassword";

    private IContainer? _container;

    public string? UnavailableReason { get; private set; }

    public int Port => _container!.GetMappedPublicPort(5000);

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder("datagrip/sybase:16.0").WithPortBinding(5000, assignRandomHostPort: true).Build();
            await _container.StartAsync();

            // The server takes a while to start after its port opens: wait for a login.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            while (true)
            {
                try
                {
                    await using var connection = await OpenAsync(cancellationToken: timeout.Token);
                    break;
                }
                catch (AseException) when (!timeout.IsCancellationRequested)
                {
                    await Task.Delay(2000, timeout.Token);
                }
            }
        }
#pragma warning disable CA1031 // Any failure here means "no ASE available", which is reported as a skip.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UnavailableReason = $"ASE tests need Docker (Testcontainers). Docker was not usable: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public void SkipIfUnavailable()
    {
        if (_container is null || UnavailableReason is not null)
        {
            Assert.Skip(UnavailableReason ?? "ASE is not available.");
        }
    }

    /// <summary>A connection of the test's own (as sa), to set things up or to hold locks.</summary>
    public async Task<AseConnection> OpenAsync(string database = "master", CancellationToken cancellationToken = default)
    {
        var connection = new AseConnection($"Data Source={Address};Port={Port};Database={database};Uid={UserName};Pwd={Password};Pooling=false;");
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public async Task ExecuteAsync(string sql, string database = "master", CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(database, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>
/// Core's side, as the engine sees it (docs/architecture.md §7.31): a login opened with the engine's driver and the
/// credential's password, and handed over although the driver keeps it, only when the request says so (SDK 1.10).
/// </summary>
internal sealed class FakeConnectivity(int port, string username, string password) : IConnectivity
{
    public int Port => port;

    public string Username => username;

    public string Password => password;

    /// <summary>The requests the engine made.</summary>
    public List<DatabaseConnectRequest> Requests { get; } = [];

    public async Task<IDatabaseLogin> OpenDatabaseAsync(HostContext host, Guid serviceId, CredentialHandle handle, DatabaseConnectRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        var login = new Login(this, request);
        await login.OpenAnotherAsync(cancellationToken);
        return login;
    }

    public Task<ISshConnection> OpenSshAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IHttpConnection> OpenHttpAsync(HostContext host, Guid serviceId, bool tls, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IDesktopConnection> OpenDesktopAsync(HostContext host, Guid serviceId, CredentialHandle handle, DesktopOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    private sealed class Login(FakeConnectivity core, DatabaseConnectRequest request) : IDatabaseLogin
    {
        private readonly List<DbConnection> _opened = [];

        public DbConnection Connection => _opened[0];

        public async Task<DbConnection> OpenAnotherAsync(CancellationToken cancellationToken = default)
        {
            var builder = request.Build(new DatabaseEndpoint(AseFixture.Address, core.Port));
            builder[request.UserKeyword] = core.Username;
            builder[request.PasswordKeyword] = core.Password;
            builder["Persist Security Info"] = false;
            var connection = request.Factory.CreateConnection()!;
            connection.ConnectionString = builder.ConnectionString;
            try
            {
                await connection.OpenAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await connection.DisposeAsync();
                throw new ConnectException(ConnectFailure.AuthenticationFailed, $"{AseFixture.Address}:{core.Port}: {ex.Message.Replace(core.Password, "***", StringComparison.Ordinal)}");
            }

            // As core: a driver that kept the password is refused, unless the plugin declared it and said so.
            if (!request.DriverKeepsPassword && connection.ConnectionString.Contains(core.Password, StringComparison.Ordinal))
            {
                await connection.DisposeAsync();
                throw new ConnectException(ConnectFailure.SecurityNegotiationFailed, "The driver kept the password.");
            }

            _opened.Add(connection);
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var connection in _opened)
            {
                await connection.DisposeAsync();
            }
        }
    }
}

/// <summary>The credential broker's description of the handle: its user name, and a domain or none.</summary>
internal sealed class FakeBroker(string username, string? domain = null) : ICredentialBroker
{
    public Task<CredentialHandle> ResolveSavedAsync(HostContext host, Guid serviceId, Guid credentialId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CredentialHandle> ResolvePromptAsync(HostContext host, Guid serviceId, string promptReference, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CredentialDescription> DescribeAsync(HostContext host, Guid serviceId, CredentialHandle handle, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CredentialDescription(CredentialSource.Prompt, null, username, domain, true, false));
}

internal static class Requests
{
    public static readonly Guid ServiceId = Guid.NewGuid();

    public static StreamSessionRequest Session()
    {
        var service = new HostService(ServiceId, AsePlugin.ServiceType, null, 5000, AseFixture.Address, JsonDocument.Parse("{}").RootElement, true);
        var host = new HostContext(Guid.NewGuid(), "ase01", null, AseFixture.Address, [], HostPermission.Use) { Services = [service] };
        return new StreamSessionRequest(host, ServiceId, Handle(), JsonDocument.Parse("{}").RootElement);
    }

    /// <summary>A handle, as the broker gives one: opaque to the plugin.</summary>
    public static CredentialHandle Handle() =>
        (CredentialHandle)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CredentialHandle));
}

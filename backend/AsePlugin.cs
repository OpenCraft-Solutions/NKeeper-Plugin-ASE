using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Abstractions;
using NKeeper.Plugins.Databases;

namespace NKeeper.Plugins.Ase;

/// <summary>
/// The ase plugin (docs/architecture.md §7.35): the <c>ase</c> service type and its database tab, which db-core runs
/// and draws (§7.32). Core opens the connections and signs in (§7.31); the plugin's driver keeps the password, which its
/// manifest declares (SDK 1.10).
/// </summary>
public sealed class AsePlugin : IPlugin
{
    public const string ServiceType = "ase";

    public const string SessionType = "ase";

    public void Contribute(IPluginContributions contributions, PluginContext context)
    {
        contributions.AddServiceType(new ServiceType(ServiceType, "SAP ASE") { DefaultPort = 5000, Health = HealthProbe.Tcp });
        contributions.AddSessionType(new SessionType(SessionType, "SAP ASE", typeof(DatabaseSessionHandler<AseEngine>)) { ServiceTypes = [ServiceType] });
        contributions.AddHostAction(new HostAction("ase.query", "SAP ASE", SessionType: SessionType));
    }

    public void ConfigureServices(IServiceCollection services, PluginContext context) => services.AddDatabaseEngine<AseEngine>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
    }
}

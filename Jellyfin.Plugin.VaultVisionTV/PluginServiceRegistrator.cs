using Jellyfin.Plugin.VaultVisionTV.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.VaultVisionTV;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<SchedulerService>();
        serviceCollection.AddSingleton<ArchiveOrgResolver>();
        serviceCollection.AddSingleton<EpgService>();
        serviceCollection.AddSingleton<StreamService>();

        serviceCollection.AddSingleton<CatalogService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<CatalogService>());
    }
}

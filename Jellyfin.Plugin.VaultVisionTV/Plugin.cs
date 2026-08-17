using Jellyfin.Plugin.VaultVisionTV.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.VaultVisionTV;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "VaultVisionTV";

    public override Guid Id => Guid.Parse("8f2d1a6e-9c3b-4a5d-8e7f-1b2c3d4e5f60");

    public string DataFolder => Path.Combine(ApplicationPaths.PluginConfigurationsPath, "VaultVisionTV");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var resourcePrefix = $"{GetType().Namespace}.Configuration.";

        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = Name,
                EnableInMainMenu = true,
                EmbeddedResourcePath = resourcePrefix + "configPage.html",
            },
        };
    }
}

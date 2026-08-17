using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.VaultVisionTV.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // Published VaultVisionTV catalog — see tools/build-catalog.py in the
    // source repo. Overridable for anyone running their own fork/mirror.
    public string CatalogUrl { get; set; } = "https://chairpants.github.io/VaultVisionTV/data/catalog.json";

    public int CatalogRefreshHours { get; set; } = 24;

    // How many days ahead the XMLTV guide covers.
    public int GuideDays { get; set; } = 3;
}

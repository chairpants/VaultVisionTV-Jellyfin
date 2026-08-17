namespace Jellyfin.Plugin.VaultVisionTV.Domain;

// Mirrors VaultVisionTV's data/catalog.json shape exactly (see
// tools/build-catalog.py in the source repo) so CatalogService can deserialize
// the published catalog with no translation layer.
public class CatalogData
{
    public string GeneratedAt { get; set; } = string.Empty;

    public List<string> Genres { get; set; } = new();

    public Dictionary<string, Show> Shows { get; set; } = new();
}

public class Show
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Genre { get; set; } = string.Empty;

    public string Grouping { get; set; } = string.Empty;

    public string? ArtUrl { get; set; }

    public long TotalDurationSec { get; set; }

    public List<Episode> Episodes { get; set; } = new();
}

public class Episode
{
    // Unique across the whole catalog: "{itemId}::{fileHint-or-name}".
    public string Key { get; set; } = string.Empty;

    public string ItemId { get; set; } = string.Empty;

    public string? FileHint { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? MovieTitle { get; set; }

    public int Index { get; set; }

    public int SeasonNum { get; set; }

    public long DurationSec { get; set; }

    public long IntroSkipSec { get; set; }

    public Crop? Crop { get; set; }
}

public class Crop
{
    public double X { get; set; }

    public double Y { get; set; }

    public double W { get; set; }

    public double H { get; set; }
}

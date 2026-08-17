using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.VaultVisionTV.Domain;

// Ported from VaultVisionTV's channels.js — see Data/channels.json, generated
// from that file directly (tools/extract-channels in the source repo) rather
// than hand-retyped. "guide" and "vod" kind channels are dropped there:
// Jellyfin renders its own guide grid, and VOD is out of scope for this phase.
public class Channel
{
    public int Number { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty; // "genre" | "curated"

    public string Tagline { get; set; } = string.Empty;

    // "genre" channels only: sweeps every show in the catalog tagged with any
    // of these genres, zero manual curation.
    [JsonConverter(typeof(StringOrArrayConverter))]
    public List<string> Genre { get; set; } = new();

    // Overrides the shuffle key so two channels can share a pool (VBO / VBO 2)
    // without ever showing the same thing at the same time.
    public string? Seed { get; set; }

    public List<string> ExcludeShowIds { get; set; } = new();

    // "curated" channels only: hand-picked pool, optionally gated to specific
    // days/hours via daypart windows; fallbackPool plays outside every window.
    public List<DaypartWindow> Daypart { get; set; } = new();

    public List<string> FallbackPool { get; set; } = new();
}

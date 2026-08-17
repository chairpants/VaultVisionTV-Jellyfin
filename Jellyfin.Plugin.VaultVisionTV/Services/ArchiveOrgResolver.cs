using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VaultVisionTV.Services;

// Port of player.js's resolveEpisodeUrl: an episode's itemId + fileHint ->
// the actual playable archive.org file URL. Per-item metadata is fetched at
// most once and shared by every episode living in that item, same as
// player.js's metaCache — cheap since items rarely change mid-run and
// several shows keep dozens of episodes in one item.
public partial class ArchiveOrgResolver
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ArchiveOrgResolver> _logger;
    private readonly ConcurrentDictionary<string, Task<ArchiveOrgMetadata?>> _metaCache = new();

    [GeneratedRegex(@"\.mp4$", RegexOptions.IgnoreCase)]
    private static partial Regex Mp4Regex();

    [GeneratedRegex(@"\.(mp4|webm|ogv)$", RegexOptions.IgnoreCase)]
    private static partial Regex WebSafeExtRegex();

    [GeneratedRegex(@"\.(ogv|webm)$", RegexOptions.IgnoreCase)]
    private static partial Regex OgvWebmRegex();

    [GeneratedRegex(@"\.[^./]+$")]
    private static partial Regex ExtensionRegex();

    public ArchiveOrgResolver(IHttpClientFactory httpClientFactory, ILogger<ArchiveOrgResolver> logger)
    {
        _httpClient = httpClientFactory.CreateClient(nameof(ArchiveOrgResolver));
        _logger = logger;
    }

    private Task<ArchiveOrgMetadata?> FetchItemMetadataAsync(string itemId, CancellationToken cancellationToken)
    {
        return _metaCache.GetOrAdd(itemId, id => FetchAsync(id, cancellationToken));

        async Task<ArchiveOrgMetadata?> FetchAsync(string id, CancellationToken ct)
        {
            try
            {
                return await _httpClient.GetFromJsonAsync<ArchiveOrgMetadata>($"https://archive.org/metadata/{id}", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "VaultVisionTV: failed to fetch archive.org metadata for {ItemId}", id);
                return null;
            }
        }
    }

    // Mirrors player.js's resolveEpisodeUrl exactly, including the
    // derivative-.mp4-preference logic documented there: archive.org's
    // *original* upload can carry non-web audio (AC3/DTS) under the exact
    // fileHint name, while the actual web-safe transcode is filed separately
    // as "foo.ia.mp4" (source: "derivative", original: "foo.mp4"). Preferring
    // that derivative avoids silent-audio playback.
    public async Task<string?> ResolveEpisodeUrlAsync(string itemId, string? fileHint, CancellationToken cancellationToken)
    {
        var meta = await FetchItemMetadataAsync(itemId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ArchiveOrgFile> files = (IReadOnlyList<ArchiveOrgFile>?)meta?.Files ?? Array.Empty<ArchiveOrgFile>();

        ArchiveOrgFile? derivative = fileHint is not null
            ? files.FirstOrDefault(f => f.Original == fileHint && Mp4Regex().IsMatch(f.Name ?? string.Empty))
            : null;

        bool needsDerivative = fileHint is not null && !WebSafeExtRegex().IsMatch(fileHint);
        string? derivativeName = needsDerivative ? ExtensionRegex().Replace(fileHint!, ".mp4") : null;

        ArchiveOrgFile? file = derivative ?? (fileHint is not null
            ? (needsDerivative ? files.FirstOrDefault(f => f.Name == derivativeName) : null)
              ?? files.FirstOrDefault(f => f.Name == fileHint)
            : files.FirstOrDefault(f => Mp4Regex().IsMatch(f.Name ?? string.Empty))
              ?? files.FirstOrDefault(f => OgvWebmRegex().IsMatch(f.Name ?? string.Empty)));

        if (file?.Name is null)
        {
            return null;
        }

        return $"https://archive.org/download/{itemId}/{EncodePath(file.Name)}";
    }

    private static string EncodePath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}

public class ArchiveOrgMetadata
{
    [JsonPropertyName("files")]
    public List<ArchiveOrgFile> Files { get; set; } = new();
}

public class ArchiveOrgFile
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("original")]
    public string? Original { get; set; }
}

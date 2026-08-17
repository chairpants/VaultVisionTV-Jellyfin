using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.VaultVisionTV.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VaultVisionTV.Services;

// Loads the two data sources VaultVisionTV needs:
//   - the channel lineup (Data/channels.json, embedded in the plugin — ported
//     from channels.js, fixed at build time)
//   - the show/episode catalog (data/catalog.json from the *published*
//     VaultVisionTV site — ~14MB and growing, so it's fetched and cached to
//     disk rather than shipped in the plugin package, refreshed on a timer
//     and via a manual "Refresh catalog" action on the config page)
public class CatalogService : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SchedulerService _scheduler;
    private readonly ILogger<CatalogService> _logger;
    private readonly HttpClient _httpClient;
    private Timer? _refreshTimer;

    public CatalogService(SchedulerService scheduler, ILogger<CatalogService> logger, IHttpClientFactory httpClientFactory)
    {
        _scheduler = scheduler;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient(nameof(CatalogService));
    }

    public List<Channel> Channels { get; private set; } = new();

    public CatalogData? Current { get; private set; }

    private string CachePath => Path.Combine(Plugin.Instance!.DataFolder, "catalog.json");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        LoadChannels();
        await LoadCachedCatalogAsync(cancellationToken).ConfigureAwait(false);

        _ = RefreshAsync(cancellationToken); // kick an initial refresh in the background; cached copy (if any) already serves requests

        var interval = TimeSpan.FromHours(Math.Max(1, Plugin.Instance!.Configuration.CatalogRefreshHours));
        _refreshTimer = new Timer(_ => _ = RefreshAsync(CancellationToken.None), null, interval, interval);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _refreshTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    private void LoadChannels()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string ResourceName = "Jellyfin.Plugin.VaultVisionTV.Data.channels.json";
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} not found.");
        Channels = JsonSerializer.Deserialize<List<Channel>>(stream, JsonOptions) ?? new List<Channel>();
        _logger.LogInformation("VaultVisionTV: loaded {Count} channels", Channels.Count);
    }

    private async Task LoadCachedCatalogAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(CachePath))
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(CachePath);
            var data = await JsonSerializer.DeserializeAsync<CatalogData>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (data is not null)
            {
                Current = data;
                _logger.LogInformation("VaultVisionTV: loaded cached catalog ({Count} shows, generated {GeneratedAt})", data.Shows.Count, data.GeneratedAt);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "VaultVisionTV: failed to load cached catalog, will fetch fresh");
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var url = Plugin.Instance!.Configuration.CatalogUrl;
        try
        {
            _logger.LogInformation("VaultVisionTV: fetching catalog from {Url}", url);
            await using var responseStream = await _httpClient.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(Plugin.Instance!.DataFolder);
            var tempPath = CachePath + ".tmp";
            await using (var fileStream = File.Create(tempPath))
            {
                await responseStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, CachePath, overwrite: true);

            await using var stream = File.OpenRead(CachePath);
            var data = await JsonSerializer.DeserializeAsync<CatalogData>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("catalog.json deserialized to null");

            Current = data;
            _scheduler.InvalidateCaches();
            _logger.LogInformation("VaultVisionTV: catalog refreshed — {Count} shows, generated {GeneratedAt}", data.Shows.Count, data.GeneratedAt);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            _logger.LogError(ex, "VaultVisionTV: catalog refresh failed, keeping previous catalog");
        }
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }
}

using System.Diagnostics;
using System.Globalization;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VaultVisionTV.Services;

// Server-side equivalent of player.js's live-join: seek ffmpeg into the
// resolved archive.org URL at the schedule's current offset (position.OffsetSec
// + episode.IntroSkipSec, exactly player.js's applyPositionToVideo seekTo
// math) and remux to MPEG-TS piped to the response.
//
// Phase 1 scope: one ffmpeg process per stream request, bounded to the
// episode's own remaining playable runtime — when the slot ends, the stream
// just ends (Jellyfin's client will show it as stopped rather than seamlessly
// continuing to the next scheduled item). Seamless chaining across slot
// boundaries, commercial-break padding, and idle-process teardown for
// concurrent viewers are Phase 2.
public class StreamService
{
    private readonly CatalogService _catalog;
    private readonly SchedulerService _scheduler;
    private readonly ArchiveOrgResolver _resolver;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<StreamService> _logger;

    public StreamService(CatalogService catalog, SchedulerService scheduler, ArchiveOrgResolver resolver, IMediaEncoder mediaEncoder, ILogger<StreamService> logger)
    {
        _catalog = catalog;
        _scheduler = scheduler;
        _resolver = resolver;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public enum StreamResult
    {
        Ok,
        ChannelNotFound,
        NoSignal,
        InBreak,
        ResolveFailed,
    }

    public async Task<StreamResult> StreamChannelAsync(int channelNumber, Stream output, CancellationToken cancellationToken)
    {
        var channel = _catalog.Channels.FirstOrDefault(c => c.Number == channelNumber);
        if (channel is null)
        {
            _logger.LogWarning("VaultVisionTV: stream request for unknown channel {Channel}", channelNumber);
            return StreamResult.ChannelNotFound;
        }

        var catalog = _catalog.Current;
        if (catalog is null)
        {
            _logger.LogWarning("VaultVisionTV: stream request for channel {Channel} but no catalog is loaded yet", channelNumber);
            return StreamResult.NoSignal;
        }

        var position = _scheduler.GetPositionAt(channel, catalog, DateTime.Now);
        if (position is null)
        {
            _logger.LogWarning("VaultVisionTV: channel {Channel} has no scheduled position right now (empty pool?)", channelNumber);
            return StreamResult.NoSignal;
        }

        if (_scheduler.IsBroken(position.Episode.Key))
        {
            _logger.LogWarning("VaultVisionTV: channel {Channel} landed on episode {Key} which is marked broken", channelNumber, position.Episode.Key);
            return StreamResult.NoSignal;
        }

        // Phase 1 has no commercial-break handling yet — if the schedule is
        // already in a slot's dead-air tail, there's nothing to stream.
        if (position.Padding)
        {
            _logger.LogInformation("VaultVisionTV: channel {Channel} is in a commercial-break gap right now (not handled until Phase 2)", channelNumber);
            return StreamResult.InBreak;
        }

        var url = await _resolver.ResolveEpisodeUrlAsync(position.Episode.ItemId, position.Episode.FileHint, cancellationToken).ConfigureAwait(false);
        if (url is null)
        {
            _logger.LogWarning(
                "VaultVisionTV: could not resolve a playable archive.org URL for channel {Channel}, episode {Key} (itemId={ItemId}, fileHint={FileHint}) — marking broken",
                channelNumber,
                position.Episode.Key,
                position.Episode.ItemId,
                position.Episode.FileHint);
            _scheduler.MarkBroken(position.Episode.Key);
            return StreamResult.ResolveFailed;
        }

        double seekTo = position.OffsetSec + position.Episode.IntroSkipSec;
        double remaining = SchedulerService.PlayableSec(position.Episode) - position.OffsetSec;

        _logger.LogInformation(
            "VaultVisionTV: tuning channel {Channel} -> {Show} {Code} @ {Seek:F0}s ({Remaining:F0}s left)",
            channelNumber,
            position.Show.Title,
            position.Episode.Code,
            seekTo,
            remaining);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (var arg in BuildArgs(url, seekTo, remaining))
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Viewer disconnected or request cancelled — not an error.
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKill(process);
            }
        }

        if (process.HasExited && process.ExitCode != 0)
        {
            var stderr = await stderrTask.ConfigureAwait(false);
            _logger.LogWarning("VaultVisionTV: ffmpeg exited {Code} for channel {Channel}: {Stderr}", process.ExitCode, channelNumber, stderr);
        }

        return StreamResult.Ok;
    }

    // -c copy (stream copy, no re-encode) works whenever the source's codecs
    // are already TS-legal, true for the great majority of VaultVisionTV's
    // catalog (player.js's resolveEpisodeUrl already prefers h264/aac web-safe
    // derivatives). A source that isn't falls through to whatever ffmpeg's
    // error looks like in the log — a re-encode fallback is a Phase 2
    // hardening item, not implemented here.
    private static IEnumerable<string> BuildArgs(string url, double seekTo, double remaining) =>
    [
        "-hide_banner",
        "-loglevel", "error",
        "-ss", seekTo.ToString("F3", CultureInfo.InvariantCulture),
        "-i", url,
        "-t", remaining.ToString("F3", CultureInfo.InvariantCulture),
        "-c", "copy",
        "-f", "mpegts",
        "-mpegts_flags", "+resend_headers",
        "pipe:1",
    ];

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the HasExited check and Kill — fine.
        }
    }
}

using System.Text;
using System.Xml;
using Jellyfin.Plugin.VaultVisionTV.Domain;

namespace Jellyfin.Plugin.VaultVisionTV.Services;

// Generates the M3U channel list and XMLTV guide Jellyfin's Live TV "M3U
// Tuner" source consumes. Programme listings are reconstructed by walking
// SchedulerService.GetPositionAt() forward slot-by-slot — the same function
// player.js polls every 15s to stay "live", just called repeatedly ahead of
// time instead of once for "now". Every pool VaultVisionTV schedules is
// slotted (see scheduler.js), so every position carries a real slot
// boundary to walk to next.
public class EpgService
{
    private const int MaxPogrammesPerChannel = 2000; // safety guard against a pathological zero-length-slot loop

    private readonly CatalogService _catalog;
    private readonly SchedulerService _scheduler;

    public EpgService(CatalogService catalog, SchedulerService scheduler)
    {
        _catalog = catalog;
        _scheduler = scheduler;
    }

    public IEnumerable<Channel> LiveChannels => _catalog.Channels; // channels.json already excludes "guide"/"vod" kinds

    internal IEnumerable<ProgrammeSlot> WalkProgrammes(Channel channel, CatalogData catalog, DateTime fromLocal, DateTime toLocal)
    {
        // Every duration in the catalog is a whole number of seconds and the
        // broadcast grid (SlotSec) is too, so every real slot boundary falls
        // exactly on a whole second. The walk cursor is rounded to one at each
        // step specifically so floating-point DateTime arithmetic can't drift
        // a hair to either side of a boundary — landing a fraction of a
        // microsecond *before* one reads back as "~0 seconds left in the old
        // slot" and re-emits a spurious near-zero-length duplicate entry.
        var t = RoundToSecond(fromLocal);
        for (int i = 0; i < MaxPogrammesPerChannel && t < toLocal; i++)
        {
            var pos = _scheduler.GetPositionAt(channel, catalog, t);
            if (pos is null)
            {
                yield break;
            }

            var rawSlotStart = RoundToSecond(t.AddSeconds(-pos.OffsetSec));

            // Two curated-channel pools can each reach across a daypart
            // transition in opposite ways:
            //   - A window pool's "elapsed" axis is cumulative real *open*
            //     airtime (windowElapsedSec pauses while the window is closed
            //     and resumes mid-episode next time it opens — deliberate, so
            //     a nightly block picks up where last night's left off). That
            //     can put offsetSec further back than this session has been
            //     open, so t - offsetSec lands before the window actually
            //     started this time.
            //   - The fallback pool's own continuous slot grid keeps ticking
            //     the whole time a window is open even though nothing reads
            //     it then, so right as the window closes its "current slot"
            //     can already have started before the window closed.
            // Either way, a guide segment must never claim to start before
            // the most recent point the live schedule would actually have
            // been showing it — clamp to the last daypart transition at or
            // before t.
            var slotStart = ClampToPrevTransition(channel, t, rawSlotStart);

            var rawSlotEnd = RoundToSecond(t.AddSeconds(pos.SlotEndsInSec));
            if (rawSlotEnd <= t)
            {
                // Should not happen once boundaries are whole-second-aligned —
                // absolute last-resort guard against a non-advancing loop.
                rawSlotEnd = t.AddSeconds(SchedulerService.SlotSec);
            }

            // Symmetrically, a slot can run past the point where the window
            // closes (or the fallback pool's own slot grid overruns into a
            // window opening) — same "content can jump discontinuously right
            // as a window starts/ends" simplification scheduler.js's own
            // windowElapsedSec doc already accepts for live playback. The
            // guide must not claim a slot runs past a boundary where the live
            // schedule would already have cut to something else.
            var slotEnd = ClampToNextTransition(channel, t, rawSlotEnd);
            if (slotEnd <= slotStart)
            {
                slotEnd = slotStart.AddSeconds(SchedulerService.SlotSec); // safety floor — never emit a zero/negative-length entry
            }

            yield return new ProgrammeSlot(slotStart, slotEnd, pos);
            t = slotEnd;
        }
    }

    private static bool SameRegime((DaypartWindow Win, int Index)? a, (DaypartWindow Win, int Index)? b)
        => a is null ? b is null : b is not null && a.Value.Index == b.Value.Index;

    private static DateTime ClampToNextTransition(Channel channel, DateTime t, DateTime slotEnd)
    {
        if (channel.Kind != "curated" || channel.Daypart.Count == 0)
        {
            return slotEnd;
        }

        var current = SchedulerService.MatchingWindow(channel, t);
        var hour = t.Date.AddHours(t.Hour + 1);
        while (hour < slotEnd)
        {
            if (!SameRegime(current, SchedulerService.MatchingWindow(channel, hour)))
            {
                return hour;
            }

            hour = hour.AddHours(1);
        }

        return slotEnd;
    }

    // Latest transition at or before t — the earliest instant the channel's
    // *current* daypart regime could actually have started airing.
    private static DateTime ClampToPrevTransition(Channel channel, DateTime t, DateTime slotStart)
    {
        if (channel.Kind != "curated" || channel.Daypart.Count == 0)
        {
            return slotStart;
        }

        var current = SchedulerService.MatchingWindow(channel, t);
        var hour = t.Date.AddHours(t.Hour);
        // Keep searching while *this hour's* transition target (hour+1) could
        // still move slotStart forward — not while hour itself is still ahead
        // of slotStart, which stops one step too early and misses the exact
        // boundary hour whose transition lands precisely on slotStart's edge.
        for (int i = 0; i < 24 * 8 && hour.AddHours(1) > slotStart; i++) // 8 days back is far more than any regime needs
        {
            if (!SameRegime(current, SchedulerService.MatchingWindow(channel, hour)))
            {
                return hour.AddHours(1); // loop guard (hour > slotStart) guarantees this is later than slotStart
            }

            hour = hour.AddHours(-1);
        }

        return slotStart;
    }

    private static DateTime RoundToSecond(DateTime d)
    {
        long ticks = (long)Math.Round(d.Ticks / (double)TimeSpan.TicksPerSecond) * TimeSpan.TicksPerSecond;
        return new DateTime(ticks, d.Kind);
    }

    public string GenerateM3u(string baseUrl)
    {
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        foreach (var channel in LiveChannels.OrderBy(c => c.Number))
        {
            sb.Append($"#EXTINF:-1 tvg-id=\"{channel.Number}\" tvg-chno=\"{channel.Number}\" tvg-name=\"{EscapeAttr(channel.Name)}\" group-title=\"VaultVisionTV\",{channel.Name}\n");
            sb.Append($"{baseUrl}/VaultVisionTV/iptv/stream/{channel.Number}\n");
        }

        return sb.ToString();
    }

    public string GenerateXmlTv(string baseUrl, DateTime nowLocal, int guideDays)
    {
        var catalog = _catalog.Current;
        var settings = new XmlWriterSettings { Indent = false, OmitXmlDeclaration = false, Encoding = Encoding.UTF8 };
        using var stringWriter = new Utf8StringWriter();
        using (var writer = XmlWriter.Create(stringWriter, settings))
        {
            writer.WriteStartElement("tv");
            writer.WriteAttributeString("generator-info-name", "VaultVisionTV");

            foreach (var channel in LiveChannels.OrderBy(c => c.Number))
            {
                writer.WriteStartElement("channel");
                writer.WriteAttributeString("id", channel.Number.ToString());
                writer.WriteStartElement("display-name");
                writer.WriteString(channel.Name);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            if (catalog is not null)
            {
                var from = nowLocal.AddHours(-1);
                var to = nowLocal.AddDays(guideDays);
                foreach (var channel in LiveChannels.OrderBy(c => c.Number))
                {
                    foreach (var slot in WalkProgrammes(channel, catalog, from, to))
                    {
                        WriteProgramme(writer, channel, slot);
                    }
                }
            }

            writer.WriteEndElement(); // tv
        }

        return stringWriter.ToString();
    }

    private static void WriteProgramme(XmlWriter writer, Channel channel, ProgrammeSlot slot)
    {
        var pos = slot.Position;
        var title = pos.Episode.MovieTitle ?? pos.Show.Title;
        var subtitle = pos.Episode.MovieTitle is not null
            ? pos.Show.Title
            : !string.IsNullOrEmpty(pos.Episode.Name) ? $"{pos.Episode.Code}  {pos.Episode.Name}" : pos.Episode.Code;

        writer.WriteStartElement("programme");
        writer.WriteAttributeString("start", FormatXmlTvTime(slot.Start));
        writer.WriteAttributeString("stop", FormatXmlTvTime(slot.End));
        writer.WriteAttributeString("channel", channel.Number.ToString());

        writer.WriteStartElement("title");
        writer.WriteString(title);
        writer.WriteEndElement();

        if (!string.IsNullOrEmpty(subtitle))
        {
            writer.WriteStartElement("sub-title");
            writer.WriteString(subtitle);
            writer.WriteEndElement();
        }

        if (!string.IsNullOrEmpty(pos.Show.ArtUrl))
        {
            writer.WriteStartElement("icon");
            writer.WriteAttributeString("src", pos.Show.ArtUrl);
            writer.WriteEndElement();
        }

        writer.WriteEndElement(); // programme
    }

    // XMLTV timestamps carry an explicit UTC offset, so slots computed in
    // local wall-clock time (the dayparting math is inherently local) still
    // round-trip correctly for a client in any timezone.
    private static string FormatXmlTvTime(DateTime local)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{local:yyyyMMddHHmmss} {sign}{offset.Duration():hhmm}";
    }

    private static string EscapeAttr(string s) => s.Replace("\"", "&quot;", StringComparison.Ordinal);

    private sealed class Utf8StringWriter : System.IO.StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}

internal record ProgrammeSlot(DateTime Start, DateTime End, SchedulePosition Position);

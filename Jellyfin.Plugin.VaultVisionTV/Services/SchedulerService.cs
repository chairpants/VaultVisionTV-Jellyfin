using System.Collections.Concurrent;
using Jellyfin.Plugin.VaultVisionTV.Domain;

namespace Jellyfin.Plugin.VaultVisionTV.Services;

// Port of VaultVisionTV's scheduler.js — pure deterministic math, no I/O.
// Given a channel and the catalog, answers "what's airing at time T and how
// far into it are we", from wall-clock time alone. That's what makes tuning
// in join a show already in progress, identically for every viewer.
//
// Ported for self-consistency, not for bit-exact agreement with the browser
// app at vaultvisiontv — this plugin computes its own independent schedule on
// its own server clock, so it only needs to agree with itself over time, not
// with a separate JS runtime.
//
// All wall-clock arguments are local DateTimes (DateTimeKind.Local) — the
// dayparting math (which hour, which day of week) is inherently local-time,
// same as scheduler.js relying on the browser's local Date. The server
// process (and its Docker container, if containerized) must be configured
// with the viewer's real timezone or dayparts will be off by the UTC offset.
public class SchedulerService
{
    public static readonly DateTime Epoch = new(2020, 1, 6, 0, 0, 0, DateTimeKind.Local);
    public const int SlotSec = 300; // 5-minute broadcast grid — see scheduler.js's own header for why 5, not 30.

    private readonly ConcurrentDictionary<string, PoolInfo> _poolCache = new();
    private readonly ConcurrentDictionary<string, List<string>> _genreIdsCache = new();
    private readonly ConcurrentDictionary<string, byte> _brokenKeys = new();

    // Every episode occupies a whole number of SlotSec-sized slots, so program
    // starts always land on a clean grid mark.
    public static double SlotFor(double durationSec) => Math.Ceiling(durationSec / SlotSec) * SlotSec;

    // How much of a file actually airs, after episode.IntroSkipSec (a station
    // bumper, an uploader's colorization credit — not part of the broadcast).
    public static double PlayableSec(Episode ep) => Math.Max(1, ep.DurationSec - ep.IntroSkipSec);

    // A catalog refresh (CatalogService) invalidates every cached pool — pools
    // hold direct references into the old CatalogData, and a channel must
    // never keep airing a snapshot the catalog has moved past.
    public void InvalidateCaches()
    {
        _poolCache.Clear();
        _genreIdsCache.Clear();
    }

    // Retired for the life of the process once an archive.org file is found
    // to 404 or fail to decode — every lookup from then on substitutes the
    // next usable programme into the same slot. Process-lifetime rather than
    // session-lifetime (scheduler.js's browser-tab equivalent): a runtime
    // observation, not a catalog fact, but this process serves every viewer.
    public void MarkBroken(string episodeKey) => _brokenKeys[episodeKey] = 0;

    public bool IsBroken(string episodeKey) => _brokenKeys.ContainsKey(episodeKey);

    // -- deterministic shuffle ---------------------------------------------
    // Same seed -> same order, forever — what makes a channel's schedule
    // reproducible without storing anything.
    internal static uint HashSeed(string s)
    {
        int h = unchecked((int)2166136261);
        foreach (char c in s)
        {
            h ^= c;
            h = unchecked(h * 16777619);
        }

        return unchecked((uint)h);
    }

    internal static Func<double> Mulberry32(uint seed)
    {
        int a = unchecked((int)seed);
        return () =>
        {
            a = unchecked(a + 0x6d2b79f5);
            int t = a;
            t = unchecked((t ^ (int)((uint)t >> 15)) * (1 | t));
            t = unchecked((t + ((t ^ (int)((uint)t >> 7)) * (61 | t))) ^ t);
            uint result = unchecked((uint)(t ^ (int)((uint)t >> 14)));
            return result / 4294967296.0;
        };
    }

    internal static List<PoolEntry> Shuffled(List<PoolEntry> arr, string seedStr)
    {
        var rand = Mulberry32(HashSeed(seedStr));
        var outArr = new List<PoolEntry>(arr);
        for (int i = outArr.Count - 1; i > 0; i--)
        {
            int j = (int)Math.Floor(rand() * (i + 1));
            (outArr[i], outArr[j]) = (outArr[j], outArr[i]);
        }

        return outArr;
    }

    // -- pool building --------------------------------------------------------
    // A "pool" is a flat, deterministically-ordered list of every episode of
    // every show in a set of showIds, plus a prefix-sum duration index so a
    // timestamp can be located in it with a binary search.
    internal static PoolInfo BuildPool(IEnumerable<string> showIds, CatalogData catalog, string seedStr, bool slotted = true, bool ordered = false)
    {
        var flat = new List<PoolEntry>();
        foreach (var id in showIds)
        {
            if (!catalog.Shows.TryGetValue(id, out var show))
            {
                continue; // channel references an unknown show id — skip, same as scheduler.js
            }

            foreach (var episode in show.Episodes)
            {
                flat.Add(new PoolEntry(id, show, episode));
            }
        }

        var sequence = ordered ? flat : Shuffled(flat, seedStr);
        var cumulative = new double[sequence.Count];
        double acc = 0;
        for (int i = 0; i < sequence.Count; i++)
        {
            cumulative[i] = acc;
            acc += slotted ? SlotFor(PlayableSec(sequence[i].Episode)) : sequence[i].Episode.DurationSec;
        }

        return new PoolInfo(sequence, cumulative, acc, slotted);
    }

    private PoolInfo CachedPool(string cacheKey, IEnumerable<string> showIds, CatalogData catalog, string seedStr, bool slotted = true, bool ordered = false)
        => _poolCache.GetOrAdd(cacheKey, _ => BuildPool(showIds, catalog, seedStr, slotted, ordered));

    // The next entry at or after idx that isn't known-broken. Falls back to
    // the original once it's gone all the way round — if every file in a pool
    // is dead there's nothing to substitute.
    private PoolEntry UsableFrom(PoolInfo info, int idx)
    {
        if (_brokenKeys.IsEmpty)
        {
            return info.Pool[idx];
        }

        int i = idx;
        for (int hops = 0; hops < info.Pool.Count; hops++)
        {
            if (!_brokenKeys.ContainsKey(info.Pool[i].Episode.Key))
            {
                return info.Pool[i];
            }

            i = (i + 1) % info.Pool.Count;
        }

        return info.Pool[idx];
    }

    internal SchedulePosition? Locate(PoolInfo info, double elapsedSec)
    {
        if (info.Pool.Count == 0 || info.TotalSec <= 0)
        {
            return null;
        }

        double t = elapsedSec >= 0
            ? elapsedSec % info.TotalSec
            : ((elapsedSec % info.TotalSec) + info.TotalSec) % info.TotalSec;

        int lo = 0, hi = info.Pool.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (info.Cumulative[mid] <= t)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        double offsetSec = t - info.Cumulative[lo];
        var scheduled = info.Pool[lo]; // what the grid says; may be unplayable
        var entry = UsableFrom(info, lo);

        if (!info.Slotted)
        {
            return new SchedulePosition { ShowId = entry.ShowId, Show = entry.Show, Episode = entry.Episode, OffsetSec = offsetSec };
        }

        var next = UsableFrom(info, (lo + 1) % info.Pool.Count);
        return new SchedulePosition
        {
            ShowId = entry.ShowId,
            Show = entry.Show,
            Episode = entry.Episode,
            OffsetSec = offsetSec,
            Padding = offsetSec >= PlayableSec(entry.Episode),
            SlotEndsInSec = SlotFor(PlayableSec(scheduled.Episode)) - offsetSec,
            Next = new NextProgramme { ShowId = next.ShowId, Show = next.Show, Episode = next.Episode },
        };
    }

    private PoolInfo GenrePool(Channel channel, CatalogData catalog)
    {
        string idsKey = $"genre:{channel.Number}:ids";
        var ids = _genreIdsCache.GetOrAdd(idsKey, _ =>
        {
            var excluded = new HashSet<string>(channel.ExcludeShowIds);
            var wanted = new HashSet<string>(channel.Genre);
            return catalog.Shows.Values
                .Where(s => wanted.Contains(s.Genre) && !excluded.Contains(s.Id))
                .Select(s => s.Id)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
        });

        string cacheKey = $"genre:{channel.Number}";
        string seed = channel.Seed ?? $"genre:{string.Join('+', channel.Genre)}";
        return CachedPool(cacheKey, ids, catalog, seed);
    }

    // -- dayparting -----------------------------------------------------------
    // Cumulative real airtime a recurring weekly window has had since Epoch,
    // through nowLocal (a complete past occurrence counts in full; the
    // in-progress one, if any, counts partially) — what lets next Saturday's
    // cartoon block continue where last Saturday's left off.
    internal static double WindowElapsedSec(DaypartWindow win, DateTime nowLocal)
    {
        double windowLenSec = (win.EndHour - win.StartHour) * 3600.0;
        long weeksSince = (long)Math.Floor((nowLocal - Epoch).TotalSeconds / (7 * 86400.0));
        double totalSec = weeksSince * win.Days.Count * windowLenSec;

        foreach (var dow in win.Days)
        {
            var occStart = nowLocal.Date.AddDays(dow - (int)nowLocal.DayOfWeek).AddHours(win.StartHour);
            var occEnd = occStart.AddSeconds(windowLenSec);
            if (nowLocal >= occEnd)
            {
                totalSec += windowLenSec;
            }
            else if (nowLocal > occStart)
            {
                totalSec += (nowLocal - occStart).TotalSeconds;
            }

            // else: this week's occurrence hasn't started yet — contributes 0
        }

        return totalSec;
    }

    internal static (DaypartWindow Win, int Index)? MatchingWindow(Channel channel, DateTime nowLocal)
    {
        for (int wi = 0; wi < channel.Daypart.Count; wi++)
        {
            var win = channel.Daypart[wi];
            if (!win.Days.Contains((int)nowLocal.DayOfWeek))
            {
                continue;
            }

            if (nowLocal.Hour >= win.StartHour && nowLocal.Hour < win.EndHour)
            {
                return (win, wi);
            }
        }

        return null;
    }

    // -- public API -------------------------------------------------------
    public SchedulePosition? GetPositionAt(Channel channel, CatalogData catalog, DateTime nowLocal)
    {
        if (channel.Kind == "genre")
        {
            return Locate(GenrePool(channel, catalog), (nowLocal - Epoch).TotalSeconds);
        }

        if (channel.Kind == "curated")
        {
            var match = MatchingWindow(channel, nowLocal);
            if (match is { } m)
            {
                string cacheKey = $"curated:{channel.Number}:w{m.Index}";
                var info = CachedPool(cacheKey, m.Win.Pool, catalog, cacheKey, slotted: true, ordered: m.Win.Ordered);
                return Locate(info, WindowElapsedSec(m.Win, nowLocal));
            }

            string fallbackKey = $"curated:{channel.Number}:fallback";
            var fallbackInfo = CachedPool(fallbackKey, channel.FallbackPool, catalog, fallbackKey);
            return Locate(fallbackInfo, (nowLocal - Epoch).TotalSeconds);
        }

        return null;
    }
}

public record PoolEntry(string ShowId, Show Show, Episode Episode);

public record PoolInfo(List<PoolEntry> Pool, double[] Cumulative, double TotalSec, bool Slotted);

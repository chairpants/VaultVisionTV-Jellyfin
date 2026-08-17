namespace Jellyfin.Plugin.VaultVisionTV.Domain;

// days: 0=Sunday..6=Saturday, matching JS Date#getDay() (and .NET's own
// DayOfWeek enum, which uses the same convention — no translation needed).
// A window crossing midnight is written as two windows in channels.js's own
// source data (23-24 and 0-5), same as here.
public class DaypartWindow
{
    public List<int> Days { get; set; } = new();

    public int StartHour { get; set; }

    public int EndHour { get; set; }

    public bool Ordered { get; set; }

    public List<string> Pool { get; set; } = new();
}

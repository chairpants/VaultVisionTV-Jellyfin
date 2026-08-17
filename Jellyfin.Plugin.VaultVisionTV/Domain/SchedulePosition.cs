namespace Jellyfin.Plugin.VaultVisionTV.Domain;

// Ported from scheduler.js's locate()/getPositionAt() return shape: "what's
// airing on this channel right now, and how far into it are we."
public class SchedulePosition
{
    public required string ShowId { get; init; }

    public required Show Show { get; init; }

    public required Episode Episode { get; init; }

    public required double OffsetSec { get; init; }

    // Past the episode's airable runtime but still inside its scheduled slot:
    // dead air until the clock reaches the next grid mark (commercial-break
    // handling — Phase 2).
    public bool Padding { get; init; }

    // Only meaningful when Padding is relevant to the caller: seconds left in
    // the current slot, measured from the *scheduled* entry (see scheduler.js
    // locate() — a substitute must not shift the grid).
    public double SlotEndsInSec { get; init; }

    public NextProgramme? Next { get; init; }
}

public class NextProgramme
{
    public required string ShowId { get; init; }

    public required Show Show { get; init; }

    public required Episode Episode { get; init; }
}

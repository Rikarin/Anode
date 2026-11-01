using Anode.Contracts;
using Anode.Model;

namespace Anode.Notation;

/// <summary>
///     Display position ↔ tick (docs/10-phase-9.md, "Display quantise"): a region's grids are its
///     <see cref="RegionNotation" />'s, or its staff style's when its straight grid is 0; grids are counted from each bar
///     line. Display quantise moves where notes are drawn, never the notes.
/// </summary>
public static class DisplayGrid {
    /// <summary>The straight and triplet grids (ticks; triplet 0: none) a region of a track of <paramref name="style" /> uses.</summary>
    public static (int Straight, int Triplet) GridsOf(RegionNotation notation, StaffStyle style) =>
        throw Pending.WorkstreamA();

    /// <summary>Where a note starting at project tick <paramref name="tick" /> in <paramref name="region" /> is drawn.</summary>
    public static long DisplayTick(ProjectState state, MidiRegion region, StaffStyle style, long tick) =>
        throw Pending.WorkstreamA();

    /// <summary>
    ///     The MIDI start, a project tick, of a note at <paramref name="tick" /> moved <paramref name="steps" /> steps of the
    ///     region's display grid (a horizontal drag); a note off the grid keeps its offset from it.
    /// </summary>
    public static long Move(ProjectState state, MidiRegion region, StaffStyle style, long tick, int steps) =>
        throw Pending.WorkstreamA();
}

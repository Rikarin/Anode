namespace Anode.Model.Time;

/// <summary>
///     A tempo change: from <paramref name="Tick" /> on, the project runs at <paramref name="Bpm" /> quarter notes
///     per minute; with <paramref name="Ramp" />, the tempo then changes to the next change's along
///     <paramref name="Shape" /> (−1…1; 0 linear in BPM over ticks; docs/06-phase-5.md, "Tempo shape").
/// </summary>
public readonly record struct TempoPoint(long Tick, double Bpm, bool Ramp = false, double Shape = 0);

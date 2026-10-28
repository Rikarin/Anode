namespace Anode.Model.Time;

/// <summary>
///     A tempo change: from <paramref name="Tick" /> on, the project runs at <paramref name="Bpm" /> quarter notes
///     per minute; with <paramref name="Ramp" />, the tempo then changes linearly (in BPM over ticks) to the next
///     change's.
/// </summary>
public readonly record struct TempoPoint(long Tick, double Bpm, bool Ramp = false);

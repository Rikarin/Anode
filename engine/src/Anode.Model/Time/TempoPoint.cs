namespace Anode.Model.Time;

/// <summary>
///     A tempo change: from <paramref name="Tick" /> on, the project runs at <paramref name="Bpm" /> quarter notes
///     per minute.
/// </summary>
public readonly record struct TempoPoint(long Tick, double Bpm);

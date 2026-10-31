namespace Anode.Engine.Sync;

/// <summary>
///     The external tempo from MIDI Clock ticks (docs/09-phase-8.md, workstream A): a second-order loop over the ticks'
///     intervals, within 0.5 bpm after two beats and through a step change, ignoring jitter. No allocation.
/// </summary>
public sealed class TempoEstimator {
    /// <summary>The estimated tempo in bpm; 0 until two beats of ticks have arrived.</summary>
    public double Tempo => throw Pending.WorkstreamA();

    /// <summary>Whether ticks arrive steadily enough to follow (they stop for more than 3 ticks' time: not locked).</summary>
    public bool Locked => throw Pending.WorkstreamA();

    /// <summary>A clock tick at <paramref name="timeNs" />; returns the new estimate.</summary>
    public double Tick(long timeNs) => throw Pending.WorkstreamA();

    /// <summary>Whether ticks stopped arriving by <paramref name="nowNs" /> (updates <see cref="Locked" />).</summary>
    public bool Check(long nowNs) => throw Pending.WorkstreamA();

    /// <summary>Forgets the ticks (on Start, or when the source changes).</summary>
    public void Reset() => throw Pending.WorkstreamA();
}

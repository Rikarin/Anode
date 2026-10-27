namespace Anode.Engine;

/// <summary>What the transport is doing. A count-in is <see cref="Recording" /> with the playhead held.</summary>
public enum TransportState {
    Stopped,
    Playing,
    Recording
}

/// <summary>
///     The cycle (loop) region in project samples. Playing up to <see cref="End" /> continues from
///     <see cref="Start" />; a playhead at or after the end plays on linearly, as in Logic. Shared by the audio thread
///     and the disk thread, so both walk the same play order (spike S2).
/// </summary>
public readonly record struct Cycle(bool Enabled, long Start, long End) {
    public static Cycle Off => default;

    /// <summary>Enabled with a non-empty range at a non-negative start.</summary>
    public bool Active => Enabled && Start >= 0 && End > Start;

    /// <summary>Frames that play from <paramref name="position" /> before the jump (long.MaxValue: none comes).</summary>
    public long UntilJump(long position) => Active && position < End ? End - position : long.MaxValue;

    /// <summary>The position <paramref name="frames" /> after <paramref name="position" /> in play order.</summary>
    public long Advance(long position, long frames) {
        if (!Active || position >= End) {
            return position + frames;
        }

        var next = position + frames;
        return next < End ? next : Start + (next - End) % (End - Start);
    }
}

/// <summary>
///     Where play order starts: at <see cref="Clock" /> (frames the transport has rolled since the engine started) the
///     playhead is at <see cref="Position" />, and from there it follows <see cref="Cycle" />. The audio thread makes a
///     new anchor whenever play order changes (a locate, a cycle change, recording starting or stopping); the disk
///     thread maps clocks to positions with the latest one, so both agree on every frame. A <see cref="Hard" /> anchor
///     (a locate) means audio queued under older anchors must not play at all.
/// </summary>
public readonly record struct Anchor(long Id, long Clock, long Position, Cycle Cycle, bool Hard) {
    /// <summary>The playhead at <paramref name="clock" /> (at or after <see cref="Clock" />).</summary>
    public long PositionAt(long clock) => Cycle.Advance(Position, clock - Clock);
}

/// <summary>
///     The playhead, in samples, owned by the audio thread: commands arrive from the control thread through the
///     engine's command queue, never by calling these methods from another thread. It moves only while rolling, by whole
///     buffers, following the cycle except while recording (a recording pass is linear: one take per armed track).
/// </summary>
public sealed class Transport {
    long anchorIds;

    public TransportState State { get; private set; }

    /// <summary>The sample position of the next frame to render.</summary>
    public long Position { get; private set; }

    /// <summary>Frames rolled since the engine started; never goes back.</summary>
    public long Clock { get; private set; }

    /// <summary>The cycle as last set.</summary>
    public Cycle Cycle { get; private set; }

    /// <summary>A recording is about to start (a count-in runs): play order is already the recording's.</summary>
    public bool RecordPending { get; private set; }

    /// <summary>The play order in effect: the cycle, except while recording (or about to).</summary>
    public Cycle PlayOrder => State == TransportState.Recording || RecordPending ? Cycle.Off : Cycle;

    public bool Rolling => State != TransportState.Stopped;

    /// <summary>The current anchor (see <see cref="Engine.Anchor" />).</summary>
    public Anchor Anchor { get; private set; }

    public Transport() {
        Anchor = new(++anchorIds, 0, 0, Cycle.Off, true);
    }

    public void Play() => Set(TransportState.Playing, false);

    public void Record() => Set(TransportState.Recording, false);

    /// <summary>Holds the playhead (a count-in) with the recording's play order, so the disk thread prepares it.</summary>
    public void PrepareRecord() => Set(State, true);

    public void Stop() => Set(TransportState.Stopped, false);

    public void Locate(long position) {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        Position = position;
        Anchor = new(++anchorIds, Clock, Position, PlayOrder, true);
    }

    public void SetCycle(Cycle cycle) {
        if (cycle == Cycle) {
            return;
        }

        var before = PlayOrder;
        Cycle = cycle;
        if (PlayOrder != before) {
            Anchor = new(++anchorIds, Clock, Position, PlayOrder, false);
        }
    }

    /// <summary>Moves the playhead past <paramref name="frames" /> rendered frames, if rolling.</summary>
    public void Advance(int frames) {
        if (!Rolling) {
            return;
        }

        Position = PlayOrder.Advance(Position, frames);
        Clock += frames;
    }

    void Set(TransportState state, bool recordPending) {
        if (state == State && recordPending == RecordPending) {
            return;
        }

        var before = PlayOrder;
        State = state;
        RecordPending = recordPending;
        if (PlayOrder != before) {
            Anchor = new(++anchorIds, Clock, Position, PlayOrder, false);
        }
    }
}

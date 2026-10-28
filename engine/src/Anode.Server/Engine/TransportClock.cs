namespace Anode.Server.Engine;

/// <summary>
///     The playhead as the engine link last read it, for automation recording (docs/05-phase-4.md, "Automation"):
///     whether the transport moves, where (in ticks), and which pass it is — a pass runs from a start to the next stop,
///     numbered from 1. <see cref="Stopped" /> fires when a pass ends, with the tick it ended at.
/// </summary>
public sealed class TransportClock {
    readonly Lock gate = new();
    long tick;
    long pass;
    long startTick;
    bool moving;

    public event Action<long, long>? Stopped;

    /// <summary>The pass playing now (its number and start tick), or null when the transport stands.</summary>
    public (long Pass, long StartTick, long Tick)? Playing {
        get {
            lock (gate) {
                return moving ? (pass, startTick, tick) : null;
            }
        }
    }

    /// <summary>The transport moved to <paramref name="at" /> (<paramref name="playing" />: it is playing or recording).</summary>
    public void Update(bool playing, long at) {
        long? ended = null;
        long endedPass = 0;
        lock (gate) {
            if (playing && !moving) {
                pass++;
                startTick = at;
            } else if (!playing && moving) {
                ended = tick;
                endedPass = pass;
            }

            moving = playing;
            tick = at;
        }

        if (ended is { } end) {
            Stopped?.Invoke(endedPass, end);
        }
    }
}

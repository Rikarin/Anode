namespace Anode.Engine.Sync;

/// <summary>What a received MIDI Clock message means.</summary>
public enum ClockInputKind {
    /// <summary>Not a clock message.</summary>
    None,

    /// <summary>A clock (F8): one 24th of a quarter note.</summary>
    Tick,

    /// <summary>Start (FA): play from the project start.</summary>
    Start,

    /// <summary>Stop (FC).</summary>
    Stop,

    /// <summary>Continue (FB): play from the current position.</summary>
    Continue,

    /// <summary>Song Position Pointer (F2): locate to <c>SongPosition</c> 16th notes.</summary>
    SongPosition
}

/// <summary>A received clock message: its meaning, a Song Position Pointer's 16ths, and when it arrived.</summary>
public readonly record struct ClockInput(ClockInputKind Kind, int SongPosition, long TimeNs);

/// <summary>
///     MIDI Clock in (docs/09-phase-8.md, "Sync"): reads clock, Start, Stop, Continue and Song Position Pointer; the
///     ticks' times feed a <see cref="TempoEstimator" />. Runs on the host's MIDI thread: no allocation.
/// </summary>
public sealed class MidiClockDecoder {
    /// <summary>When the last clock arrived (0: none yet).</summary>
    public long LastTickNs { get; private set; }

    /// <summary>One message (1–3 bytes) that arrived at <paramref name="timeNs" />.</summary>
    public ClockInput Receive(ReadOnlySpan<byte> message, long timeNs) {
        if (message.IsEmpty) {
            return new(ClockInputKind.None, 0, timeNs);
        }

        if (message[0] == 0xF8) {
            LastTickNs = timeNs;
        }

        return message[0] switch {
            0xF8 => new(ClockInputKind.Tick, 0, timeNs),
            0xFA => new(ClockInputKind.Start, 0, timeNs),
            0xFB => new(ClockInputKind.Continue, 0, timeNs),
            0xFC => new(ClockInputKind.Stop, 0, timeNs),
            0xF2 when message.Length >= 3 => new(
                ClockInputKind.SongPosition,
                (message[1] & 0x7F) | ((message[2] & 0x7F) << 7),
                timeNs
            ),
            _ => new(ClockInputKind.None, 0, timeNs)
        };
    }
}

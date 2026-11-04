using Anode.Ipc;

namespace Anode.Engine.Sync;

/// <summary>
///     A timecode position read from MTC: seconds of timecode, when the frame it completes arrived, and whether it came
///     from a full frame (a locate) rather than quarter frames.
/// </summary>
public readonly record struct MtcPosition(double Seconds, long TimeNs, bool FullFrame);

/// <summary>
///     MTC in (docs/09-phase-8.md, "Sync"): assembles quarter frames (eight make a position, two frames behind the
///     sender, compensated) and full frames into positions for the chase; stops when they stop. Runs on the host's MIDI
///     thread: no allocation.
/// </summary>
/// <remarks>
///     Piece 0 leaves the sender as the labelled frame starts and piece 7 a quarter frame before the frame after next,
///     so the position at piece 7's arrival is the label plus 1¾ frames. Pieces must arrive in order from piece 0; a gap
///     waits for the next piece 0. The frame rate is the one the timecode carries (piece 7, the full frame), else the
///     configured one.
/// </remarks>
public sealed class MtcDecoder(EngineMtcRate rate) {
    readonly int[] nibbles = new int[8];
    int expected = -1;
    long lastQuarterNs;
    bool running;

    /// <summary>The frame rate: configured, then as the timecode says.</summary>
    public EngineMtcRate Rate { get; private set; } = rate;

    /// <summary>Whether quarter frames are arriving (none for two frames' time: stopped).</summary>
    public bool Running => running;

    /// <summary>One message (a quarter frame or a full-frame SysEx) that arrived at <paramref name="timeNs" />.</summary>
    public MtcPosition? Receive(ReadOnlySpan<byte> message, long timeNs) {
        if (message.Length >= 2 && message[0] == 0xF1) {
            return Quarter(message[1], timeNs);
        }

        if (message.Length >= 10
            && message[0] == 0xF0
            && message[1] == 0x7F
            && message[3] == 0x01
            && message[4] == 0x01
            && message[9] == 0xF7) {
            Rate = Timecode.FromRateCode(message[5] >> 5);
            var label = new TimecodeLabel(message[5] & 0x1F, message[6] & 0x3F, message[7] & 0x3F, message[8] & 0x1F);
            expected = -1;
            return new(Timecode.SecondsOf(label, Rate), timeNs, true);
        }

        return null;
    }

    /// <summary>Whether timecode stopped arriving by <paramref name="nowNs" /> (updates <see cref="Running" />).</summary>
    public bool Check(long nowNs) {
        if (running && nowNs - lastQuarterNs > 2e9 / Timecode.FramesPerSecond(Rate)) {
            running = false;
            expected = -1;
        }

        return !running;
    }

    /// <summary>Forgets the pieces so far (the source changed).</summary>
    public void Reset() {
        expected = -1;
        running = false;
    }

    MtcPosition? Quarter(byte data, long timeNs) {
        lastQuarterNs = timeNs;
        running = true;
        var piece = (data >> 4) & 7;
        if (piece == 0) {
            expected = 0;
        } else if (piece != expected) {
            expected = -1; // out of order (or reversed): wait for the next piece 0
            return null;
        }

        nibbles[piece] = data & 0xF;
        expected = piece + 1;
        if (piece != 7) {
            return null;
        }

        expected = -1;
        Rate = Timecode.FromRateCode(nibbles[7] >> 1);
        var label = new TimecodeLabel(
            nibbles[6] | ((nibbles[7] & 1) << 4),
            nibbles[4] | ((nibbles[5] & 3) << 4),
            nibbles[2] | ((nibbles[3] & 3) << 4),
            nibbles[0] | ((nibbles[1] & 1) << 4)
        );
        var seconds = Timecode.SecondsOf(label, Rate) + 1.75 / Timecode.FramesPerSecond(Rate);
        return new(seconds, timeNs, false);
    }
}

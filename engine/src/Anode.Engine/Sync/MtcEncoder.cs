using Anode.Ipc;

namespace Anode.Engine.Sync;

/// <summary>
///     MTC out (docs/09-phase-8.md, "Sync") at 24, 25, 29.97 drop or 30 fps: quarter frames (F1 nn), four per frame,
///     placed to the frame within each block while playing; a full frame (SysEx F0 7F 7F 01 01 hh mm ss ff F7) on a
///     locate. Positions are seconds of timecode (the project time plus the start offset). Called from the audio
///     callback: no allocation.
/// </summary>
/// <remarks>
///     Quarter frame <c>k</c> (counted from 00:00:00:00) is sent at <c>k / (4 × fps)</c> seconds and carries piece
///     <c>k mod 8</c> of the label of frame <c>2 × ⌊k / 8⌋</c>: piece 0 goes out as an even frame starts, and a receiver
///     has the whole label when piece 7 arrives, 1¾ frames later.
/// </remarks>
public sealed class MtcEncoder {
    const double Epsilon = 1e-9;

    public MtcEncoder(EngineMtcRate rate) {
        Rate = rate;
    }

    public EngineMtcRate Rate { get; }

    /// <summary>
    ///     One block of <paramref name="frames" /> frames over timecode <paramref name="startSeconds" /> …
    ///     <paramref name="endSeconds" /> while <paramref name="playing" />: writes its quarter frames to
    ///     <paramref name="output" /> and returns how many.
    /// </summary>
    public int Process(bool playing, double startSeconds, double endSeconds, int frames, Span<SyncMidiEvent> output) {
        if (!playing || endSeconds <= startSeconds) {
            return 0;
        }

        var quarterRate = 4 * Timecode.FramesPerSecond(Rate);
        var span = endSeconds - startSeconds;
        var k = (long)Math.Ceiling(Math.Max(0, startSeconds) * quarterRate - Epsilon);
        var count = 0;
        while (count < output.Length) {
            var at = k / quarterRate;
            if (at >= endSeconds - Epsilon) {
                break;
            }

            var frame = (int)Math.Floor((at - startSeconds) / span * frames + Epsilon);
            var piece = (int)(k & 7);
            var label = Timecode.Label((k >> 3) * 2, Rate);
            output[count++] = new(
                Math.Clamp(frame, 0, Math.Max(0, frames - 1)),
                0xF1,
                (byte)((piece << 4) | Nibble(label, piece)),
                0,
                2
            );
            k++;
        }

        return count;
    }

    /// <summary>The full-frame SysEx for timecode <paramref name="seconds" /> into <paramref name="output" /> (10 bytes).</summary>
    public int FullFrame(double seconds, Span<byte> output) {
        if (output.Length < 10) {
            return 0;
        }

        var label = Timecode.Label(Timecode.CountAt(Math.Max(0, seconds), Rate), Rate);
        output[0] = 0xF0;
        output[1] = 0x7F;
        output[2] = 0x7F; // all devices
        output[3] = 0x01; // MTC
        output[4] = 0x01; // full message
        output[5] = (byte)((Timecode.RateCode(Rate) << 5) | label.Hours);
        output[6] = (byte)label.Minutes;
        output[7] = (byte)label.Seconds;
        output[8] = (byte)label.Frames;
        output[9] = 0xF7;
        return 10;
    }

    int Nibble(in TimecodeLabel label, int piece) =>
        piece switch {
            0 => label.Frames & 0xF,
            1 => (label.Frames >> 4) & 0x1,
            2 => label.Seconds & 0xF,
            3 => (label.Seconds >> 4) & 0x3,
            4 => label.Minutes & 0xF,
            5 => (label.Minutes >> 4) & 0x3,
            6 => label.Hours & 0xF,
            _ => ((label.Hours >> 4) & 0x1) | (Timecode.RateCode(Rate) << 1)
        };
}

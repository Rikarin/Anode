using Anode.Ipc;

namespace Anode.Engine.Sync;

/// <summary>A timecode label: hours, minutes, seconds and frames.</summary>
public readonly record struct TimecodeLabel(int Hours, int Minutes, int Seconds, int Frames);

/// <summary>
///     SMPTE timecode arithmetic for MTC at 24, 25, 29.97 drop-frame and 30 fps. Positions are seconds of real time
///     ("seconds of timecode"); a frame count is the frames since 00:00:00:00, which at 29.97 drop-frame are 1001/30000 s
///     each and labelled with frames 0 and 1 skipped at every minute not divisible by ten. Labels wrap at 24 hours.
/// </summary>
public static class Timecode {
    /// <summary>Frames per day at 29.97 drop-frame (24 × 107 892).</summary>
    const long DropFramesPerDay = 24L * 107_892;

    /// <summary>Frames per second (29.97 drop: 30000/1001).</summary>
    public static double FramesPerSecond(EngineMtcRate rate) =>
        rate switch {
            EngineMtcRate.Fps24 => 24,
            EngineMtcRate.Fps25 => 25,
            EngineMtcRate.Fps2997Drop => 30000.0 / 1001,
            _ => 30
        };

    /// <summary>The frames a label counts up to (30 at 29.97).</summary>
    public static int NominalFrames(EngineMtcRate rate) =>
        rate switch {
            EngineMtcRate.Fps24 => 24,
            EngineMtcRate.Fps25 => 25,
            _ => 30
        };

    /// <summary>MTC's rate code (quarter frame 7, the full frame's hours byte): 0 24, 1 25, 2 29.97 drop, 3 30.</summary>
    public static int RateCode(EngineMtcRate rate) =>
        rate switch {
            EngineMtcRate.Fps24 => 0,
            EngineMtcRate.Fps25 => 1,
            EngineMtcRate.Fps2997Drop => 2,
            _ => 3
        };

    public static EngineMtcRate FromRateCode(int code) =>
        (code & 3) switch {
            0 => EngineMtcRate.Fps24,
            1 => EngineMtcRate.Fps25,
            2 => EngineMtcRate.Fps2997Drop,
            _ => EngineMtcRate.Fps30
        };

    static long FramesPerDay(EngineMtcRate rate) =>
        rate == EngineMtcRate.Fps2997Drop ? DropFramesPerDay : 86_400L * NominalFrames(rate);

    /// <summary>The label of frame <paramref name="count" /> (wrapping at 24 hours).</summary>
    public static TimecodeLabel Label(long count, EngineMtcRate rate) {
        var day = FramesPerDay(rate);
        count = ((count % day) + day) % day;
        if (rate == EngineMtcRate.Fps2997Drop) {
            // Every ten minutes hold 17 982 frames; the first minute of the ten keeps its two labels, the rest skip them.
            var tens = count / 17_982;
            var rest = count % 17_982;
            count += 18 * tens + (rest < 2 ? 0 : 2 * ((rest - 2) / 1_798));
        }

        var fps = NominalFrames(rate);
        var frames = (int)(count % fps);
        var seconds = (int)(count / fps % 60);
        var minutes = (int)(count / (fps * 60L) % 60);
        var hours = (int)(count / (fps * 3600L) % 24);
        return new(hours, minutes, seconds, frames);
    }

    /// <summary>The frame count a label names.</summary>
    public static long Count(in TimecodeLabel label, EngineMtcRate rate) {
        var fps = NominalFrames(rate);
        var count = ((label.Hours * 60L + label.Minutes) * 60 + label.Seconds) * fps + label.Frames;
        if (rate == EngineMtcRate.Fps2997Drop) {
            var minutes = label.Hours * 60L + label.Minutes;
            count -= 2 * (minutes - minutes / 10);
        }

        return count;
    }

    /// <summary>The frame at <paramref name="seconds" /> (the last that started).</summary>
    public static long CountAt(double seconds, EngineMtcRate rate) =>
        (long)Math.Floor(seconds * FramesPerSecond(rate) + 1e-6);

    /// <summary>When frame <paramref name="count" /> starts, in seconds.</summary>
    public static double SecondsOf(long count, EngineMtcRate rate) => count / FramesPerSecond(rate);

    /// <summary>When a label's frame starts, in seconds.</summary>
    public static double SecondsOf(in TimecodeLabel label, EngineMtcRate rate) => SecondsOf(Count(label, rate), rate);
}

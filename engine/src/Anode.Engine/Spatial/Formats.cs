using Anode.Dsp.Spatial;
using Anode.Ipc;

namespace Anode.Engine.Spatial;

/// <summary>
///     Channel formats as the engine uses them (docs/11-phase-10.md, "Formats and channels"): the IPC's
///     <see cref="EngineChannelFormat" /> is the DSP's <see cref="SpeakerFormat" /> (same members, same order; a test
///     checks), and a layout's channels are in ADM order in every buffer.
/// </summary>
public static class Formats {
    /// <summary>The most channels a format has (9.1.6).</summary>
    public const int MaxChannels = 16;

    public static SpeakerFormat Of(EngineChannelFormat format) =>
        Enum.IsDefined(format) ? (SpeakerFormat)(int)format : SpeakerFormat.Stereo;

    public static SpeakerLayout Layout(SpeakerFormat format) => SpeakerLayout.Of(format);

    public static int Channels(SpeakerFormat format) => SpeakerLayout.Of(format).ChannelCount;

    /// <summary>
    ///     The gains (output-major: <c>[o × from + i]</c>) that carry <paramref name="from" />'s channels into
    ///     <paramref name="to" />'s when a strip's signal reaches a summing point of another format: workstream D's
    ///     down-mix (ITU-R BS.775 and its extensions) to a smaller layout; to a larger one, each channel to the speaker
    ///     of its label, or else to the nearest speaker of its layer (the LFE to the LFE, or nowhere). Control thread.
    /// </summary>
    public static float[] Conversion(SpeakerLayout from, SpeakerLayout to, ISpatialFactory spatial) {
        var m = new float[to.ChannelCount * from.ChannelCount];
        if (to.ChannelCount < from.ChannelCount) {
            var downmix = spatial.CreateDownmix(from, to);
            for (var o = 0; o < to.ChannelCount; o++) {
                for (var i = 0; i < from.ChannelCount; i++) {
                    m[o * from.ChannelCount + i] = downmix.Gain(o, i);
                }
            }

            return m;
        }

        for (var i = 0; i < from.ChannelCount; i++) {
            if (Nearest(from, i, to) is var o and >= 0) {
                m[o * from.ChannelCount + i] = 1f;
            }
        }

        return m;
    }

    /// <summary>The channel of <paramref name="to" /> that takes channel <paramref name="i" /> of <paramref name="from" /> (−1: none).</summary>
    static int Nearest(SpeakerLayout from, int i, SpeakerLayout to) {
        var label = from.Label(i);
        for (var o = 0; o < to.ChannelCount; o++) {
            if (to.Label(o) == label) {
                return o;
            }
        }

        if (i == from.LfeIndex) {
            return to.LfeIndex;
        }

        var best = -1;
        var bestDistance = double.MaxValue;
        for (var o = 0; o < to.ChannelCount; o++) {
            if (o == to.LfeIndex) {
                continue;
            }

            var distance = Angle(from.Azimuths[i], from.Elevations[i], to.Azimuths[o], to.Elevations[o]);
            if (distance < bestDistance - 1e-9) {
                best = o;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>The angle between two directions, in radians.</summary>
    static double Angle(float az1, float el1, float az2, float el2) {
        var a1 = az1 * Math.PI / 180;
        var e1 = el1 * Math.PI / 180;
        var a2 = az2 * Math.PI / 180;
        var e2 = el2 * Math.PI / 180;
        var dot = Math.Cos(e1) * Math.Cos(e2) * Math.Cos(a1 - a2) + Math.Sin(e1) * Math.Sin(e2);
        return Math.Acos(Math.Clamp(dot, -1, 1));
    }

    /// <summary>
    ///     How a stereo effect covers a layout (linear effects, per channel): channels 0 and 1 (the front pair) for the
    ///     effect's own instance, then each left/right twin (<c>M+110</c> with <c>M-110</c>, …) as a pair and every other
    ///     channel (the centre, the LFE) alone (<c>B</c> −1), for one more instance each.
    /// </summary>
    public static (int A, int B)[] Pairs(SpeakerLayout layout) {
        var n = layout.ChannelCount;
        var pairs = new List<(int, int)>();
        var taken = new bool[n];
        if (n >= 2) {
            pairs.Add((0, 1));
            taken[0] = taken[1] = true;
        }

        for (var c = 0; c < n; c++) {
            if (taken[c]) {
                continue;
            }

            taken[c] = true;
            var label = layout.Label(c);
            var twin = -1;
            if (label.Length > 1 && label[1] == '+') {
                var other = string.Concat(label.AsSpan(0, 1), "-", label.AsSpan(2));
                for (var d = c + 1; d < n; d++) {
                    if (!taken[d] && layout.Label(d) == other) {
                        twin = d;
                        break;
                    }
                }
            }

            if (twin >= 0) {
                taken[twin] = true;
            }

            pairs.Add((c, twin));
        }

        return [..pairs];
    }
}

/// <summary>
///     WAVE_FORMAT_EXTENSIBLE's speaker positions for a layout (docs/11-phase-10.md, "Bounce"): the channel mask and the
///     order a file stores the channels in (by mask bit, so 7.x puts the rears, <c>M±135</c>, before the sides,
///     <c>M±090</c>), channels with no speaker position of their own last (9.1.6's top middle pair).
/// </summary>
public static class WaveChannels {
    public const uint FrontLeft = 0x1;
    public const uint FrontRight = 0x2;
    public const uint FrontCenter = 0x4;
    public const uint LowFrequency = 0x8;
    public const uint BackLeft = 0x10;
    public const uint BackRight = 0x20;
    public const uint FrontLeftOfCenter = 0x40;
    public const uint FrontRightOfCenter = 0x80;
    public const uint SideLeft = 0x200;
    public const uint SideRight = 0x400;
    public const uint TopFrontLeft = 0x1000;
    public const uint TopFrontRight = 0x4000;
    public const uint TopBackLeft = 0x8000;
    public const uint TopBackRight = 0x20000;

    /// <summary>
    ///     The mask, and <c>Order[k]</c> = the layout channel (ADM order) stored k-th in the file. Channels whose
    ///     position has no bit (or whose bit another channel took) are not in the mask and come last, in ADM order.
    /// </summary>
    public static (uint Mask, int[] Order) Of(SpeakerLayout layout) {
        var bits = new uint[layout.ChannelCount];
        var hasTopFront = false;
        for (var c = 0; c < layout.ChannelCount; c++) {
            hasTopFront |= layout.Label(c) is "U+045" or "U-045";
        }

        uint mask = 0;
        for (var c = 0; c < layout.ChannelCount; c++) {
            var bit = Bit(layout.Label(c), hasTopFront);
            if (bit != 0 && (mask & bit) == 0) {
                bits[c] = bit;
                mask |= bit;
            }
        }

        var order = Enumerable.Range(0, layout.ChannelCount)
            .OrderBy(c => bits[c] == 0 ? 1 : 0)
            .ThenBy(c => bits[c] == 0 ? c : (long)bits[c])
            .ToArray();
        return (mask, order);
    }

    static uint Bit(string label, bool hasTopFront) =>
        label switch {
            "M+030" or "M+045" => FrontLeft,
            "M-030" or "M-045" => FrontRight,
            "M+000" => FrontCenter,
            SpeakerLayout.LfeLabel => LowFrequency,
            "M+110" or "M+135" => BackLeft,
            "M-110" or "M-135" => BackRight,
            "M+060" => FrontLeftOfCenter,
            "M-060" => FrontRightOfCenter,
            "M+090" => SideLeft,
            "M-090" => SideRight,
            "U+045" => TopFrontLeft,
            "U-045" => TopFrontRight,
            "U+135" => TopBackLeft,
            "U-135" => TopBackRight,
            // 7.1.2's top middle pair takes the top front positions; with a top front pair (9.1.6) it has none.
            "U+090" => hasTopFront ? 0 : TopFrontLeft,
            "U-090" => hasTopFront ? 0 : TopFrontRight,
            _ => 0
        };
}

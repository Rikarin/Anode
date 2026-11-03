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
    public static float[] Conversion(SpeakerLayout from, SpeakerLayout to, SpatialContext spatial) {
        var m = new float[to.ChannelCount * from.ChannelCount];
        if (to.ChannelCount < from.ChannelCount
            && spatial.Try(() => spatial.Factory.CreateDownmix(from, to), "the down-mix") is { } downmix) {
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

    /// <summary>
    ///     The channel of <paramref name="to" /> that takes channel <paramref name="i" /> of <paramref name="from" />
    ///     (−1: none).
    /// </summary>
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
            var twin = Twin(layout, c, taken);
            if (twin >= 0) {
                taken[twin] = true;
            }

            pairs.Add((c, twin));
        }

        return [..pairs];
    }

    /// <summary>
    ///     The right-hand twin of a left-hand speaker <paramref name="c" /> not yet taken (<c>M+110</c>: <c>M-110</c>),
    ///     or −1.
    /// </summary>
    static int Twin(SpeakerLayout layout, int c, bool[] taken) {
        var label = layout.Label(c);
        if (label.Length < 2 || label[1] != '+') {
            return -1;
        }

        var other = string.Concat(label.AsSpan(0, 1), "-", label.AsSpan(2));
        for (var d = c + 1; d < layout.ChannelCount; d++) {
            if (!taken[d] && layout.Label(d) == other) {
                return d;
            }
        }

        return -1;
    }
}

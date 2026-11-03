namespace Anode.Dsp.Spatial;

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

    /// <summary>
    ///     The layout of <paramref name="channels" /> channels whose mask is <paramref name="mask" /> (a file
    ///     <see cref="Of" /> wrote), or null.
    /// </summary>
    public static SpeakerLayout? LayoutOf(uint mask, int channels) {
        if (mask == 0 || channels <= 2) {
            return null;
        }

        foreach (var format in Enum.GetValues<SpeakerFormat>()) {
            var layout = SpeakerLayout.Of(format);
            if (layout.ChannelCount == channels && Of(layout).Mask == mask) {
                return layout;
            }
        }

        return null;
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

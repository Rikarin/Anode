using Anode.Contracts;
using System.Globalization;

namespace Anode.Interchange.Adm;

/// <summary>
///     What Anode's ADM uses of the common definitions (ITU-R BS.2094-2): the DirectSpeakers channel format of each ADM
///     speaker label, the pack formats of the layouts BS.2094 defines in Anode's channel order, and ADM's time format.
/// </summary>
static class AdmDefinitions {
    public const string Namespace = "urn:ebu:metadata-schema:ebuCore_2017";
    public const string Version = "ITU-R_BS.2076-2";
    public const string LabelPrefix = "urn:itu:bs:2051:0:speaker:";

    /// <summary>BS.2094's DirectSpeakers channel formats by speaker label (Anode's <c>LFE1</c> is its <c>LFE</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> ChannelByLabel = new Dictionary<string, string>(
        StringComparer.Ordinal
    ) {
        ["M+030"] = "AC_00010001",
        ["M-030"] = "AC_00010002",
        ["M+000"] = "AC_00010003",
        [SpeakerLabels.Lfe] = "AC_00010004",
        ["M+110"] = "AC_00010005",
        ["M-110"] = "AC_00010006",
        ["M+090"] = "AC_0001000a",
        ["M-090"] = "AC_0001000b",
        ["U+090"] = "AC_00010013",
        ["U-090"] = "AC_00010014",
        ["M+060"] = "AC_00010018",
        ["M-060"] = "AC_00010019",
        ["M+135"] = "AC_0001001c",
        ["M-135"] = "AC_0001001d",
        ["U+135"] = "AC_0001001e",
        ["U-135"] = "AC_0001001f",
        ["U+045"] = "AC_00010022",
        ["U-045"] = "AC_00010023",
        ["M+045"] = "AC_00010026",
        ["M-045"] = "AC_00010027"
    };

    /// <summary>
    ///     BS.2094's pack formats whose channels are exactly a layout's, in its order (quad, 7.0, 7.1.2 and 9.1.6 have
    ///     none: Anode writes its own pack of the common channel formats).
    /// </summary>
    public static readonly IReadOnlyDictionary<ChannelFormat, string> PackByFormat =
        new Dictionary<ChannelFormat, string> {
            [ChannelFormat.Mono] = "AP_00010001",
            [ChannelFormat.Stereo] = "AP_00010002",
            [ChannelFormat.Lcr] = "AP_0001000a",
            [ChannelFormat.Surround50] = "AP_0001000c",
            [ChannelFormat.Surround51] = "AP_00010003",
            [ChannelFormat.Surround71] = "AP_0001000f",
            [ChannelFormat.Surround714] = "AP_00010017"
        };

    /// <summary>The speaker label of a common channel format, or null.</summary>
    public static string? LabelOfChannel(string id) {
        foreach (var (label, channel) in ChannelByLabel) {
            if (string.Equals(channel, id, StringComparison.OrdinalIgnoreCase)) {
                return label;
            }
        }

        return null;
    }

    /// <summary>
    ///     An ADM speaker label as a layout's: the URN prefix dropped, <c>LFE</c>, <c>LFEL</c> and <c>LFE1</c> as
    ///     <c>LFE1</c>.
    /// </summary>
    public static string NormaliseLabel(string label) {
        var plain = label.StartsWith(LabelPrefix, StringComparison.Ordinal) ? label[LabelPrefix.Length..] : label;
        return plain is "LFE" or "LFEL" or "LFE1" ? SpeakerLabels.Lfe : plain;
    }

    /// <summary>ADM's fractional time format (BS.2076-2): <c>hh:mm:ss.nnnnnSrate</c>, exact to the sample.</summary>
    public static string Time(long frames, int sampleRate) {
        var seconds = frames / sampleRate;
        var rest = frames % sampleRate;
        var digits = (sampleRate - 1).ToString(CultureInfo.InvariantCulture).Length;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}.{rest.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')}S{sampleRate}"
        );
    }

    /// <summary>An ADM time (decimal or fractional) in frames at <paramref name="sampleRate" />, rounded.</summary>
    public static long Frames(string time, int sampleRate) {
        var parts = time.Trim().Split(':');
        if (parts.Length != 3) {
            throw new FormatException($"'{time}' is not an ADM time.");
        }

        var hours = long.Parse(parts[0], CultureInfo.InvariantCulture);
        var minutes = long.Parse(parts[1], CultureInfo.InvariantCulture);
        var second = parts[2];
        var whole = (hours * 60 + minutes) * 60;
        var s = second.IndexOf('S', StringComparison.Ordinal);
        if (s >= 0) {
            var dot = second.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0) {
                throw new FormatException($"'{time}' is not an ADM time.");
            }

            var seconds = long.Parse(second[..dot], CultureInfo.InvariantCulture);
            var numerator = long.Parse(second[(dot + 1)..s], CultureInfo.InvariantCulture);
            var denominator = long.Parse(second[(s + 1)..], CultureInfo.InvariantCulture);
            if (denominator <= 0 || numerator >= denominator) {
                throw new FormatException($"'{time}' is not an ADM time.");
            }

            return (whole + seconds) * sampleRate + (long)Math.Round((double)numerator * sampleRate / denominator);
        }

        var value = decimal.Parse(second, NumberStyles.Float, CultureInfo.InvariantCulture);
        return (long)Math.Round((whole + value) * sampleRate);
    }
}

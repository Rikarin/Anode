namespace Anode.Dsp.Spatial;

/// <summary>
///     The channel formats as the engine numbers them: the same members, in the same order, as the contract's
///     <c>ChannelFormat</c> and the IPC's <c>EngineChannelFormat</c> (the host references neither Anode.Contracts nor
///     this table's twin, <c>Anode.Contracts.SpeakerLayouts</c>; a test checks they agree).
/// </summary>
public enum SpeakerFormat {
    Mono,
    Stereo,
    Lcr,
    Quad,
    Surround50,
    Surround51,
    Surround70,
    Surround71,
    Surround712,
    Surround714,
    Surround916
}

/// <summary>
///     A speaker layout as data (docs/11-phase-10.md, ITU-R BS.2051): the ADM label, azimuth and elevation (degrees,
///     azimuth positive to the left) of each channel in buffer order, and which channel is the LFE. Immutable and shared:
///     built once, read from any thread.
/// </summary>
public sealed class SpeakerLayout {
    static readonly SpeakerLayout[] Layouts = [
        new(SpeakerFormat.Mono, [("M+000", 0, 0)]),
        new(SpeakerFormat.Stereo, [("M+030", 30, 0), ("M-030", -30, 0)]),
        new(SpeakerFormat.Lcr, [("M+030", 30, 0), ("M-030", -30, 0), ("M+000", 0, 0)]),
        new(SpeakerFormat.Quad, [("M+045", 45, 0), ("M-045", -45, 0), ("M+135", 135, 0), ("M-135", -135, 0)]),
        new(SpeakerFormat.Surround50, [..Front(false), ("M+110", 110, 0), ("M-110", -110, 0)]),
        new(SpeakerFormat.Surround51, [..Front(true), ("M+110", 110, 0), ("M-110", -110, 0)]),
        new(SpeakerFormat.Surround70, [..Front(false), ..Seven()]),
        new(SpeakerFormat.Surround71, [..Front(true), ..Seven()]),
        new(SpeakerFormat.Surround712, [..Front(true), ..Seven(), ("U+090", 90, 30), ("U-090", -90, 30)]),
        new(
            SpeakerFormat.Surround714,
            [..Front(true), ..Seven(), ("U+045", 45, 30), ("U-045", -45, 30), ("U+135", 135, 30), ("U-135", -135, 30)]
        ),
        new(
            SpeakerFormat.Surround916,
            [
                ..Front(true),
                ..Seven(),
                ("M+060", 60, 0),
                ("M-060", -60, 0),
                ("U+045", 45, 30),
                ("U-045", -45, 30),
                ("U+090", 90, 30),
                ("U-090", -90, 30),
                ("U+135", 135, 30),
                ("U-135", -135, 30)
            ]
        )
    ];

    readonly string[] labels;
    readonly float[] azimuths;
    readonly float[] elevations;

    SpeakerLayout(SpeakerFormat format, (string Label, float Azimuth, float Elevation)[] speakers) {
        Format = format;
        labels = [..speakers.Select(static s => s.Label)];
        azimuths = [..speakers.Select(static s => s.Azimuth)];
        elevations = [..speakers.Select(static s => s.Elevation)];
        LfeIndex = Array.IndexOf(labels, LfeLabel);
    }

    public const string LfeLabel = "LFE1";

    public SpeakerFormat Format { get; }
    public int ChannelCount => labels.Length;

    /// <summary>The LFE channel's index, or −1.</summary>
    public int LfeIndex { get; }

    public ReadOnlySpan<float> Azimuths => azimuths;
    public ReadOnlySpan<float> Elevations => elevations;

    /// <summary>Whether a speaker is above ear height.</summary>
    public bool HasHeight => elevations.Any(static e => e > 0);

    public string Label(int channel) => labels[channel];

    public static SpeakerLayout Of(SpeakerFormat format) => Layouts[(int)format];

    /// <summary>L, R, C and, with <paramref name="lfe" />, the LFE (nominally at 45°, −30°).</summary>
    static (string, float, float)[] Front(bool lfe) =>
        lfe
            ? [("M+030", 30, 0), ("M-030", -30, 0), ("M+000", 0, 0), (LfeLabel, 45, -30)]
            : [("M+030", 30, 0), ("M-030", -30, 0), ("M+000", 0, 0)];

    /// <summary>7.x's sides and rears.</summary>
    static (string, float, float)[] Seven() =>
        [("M+090", 90, 0), ("M-090", -90, 0), ("M+135", 135, 0), ("M-135", -135, 0)];
}

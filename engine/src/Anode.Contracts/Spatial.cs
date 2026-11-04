using System.Text.Json.Serialization;

namespace Anode.Contracts;

// Surround and immersive audio (docs/11-phase-10.md, docs/adr/0012-spatial-audio.md): channel formats as ADM speaker
// layouts, the strip's panners, monitoring, and ADM BWF export and import. Angles are degrees in the ADM convention
// (ITU-R BS.2051, BS.2076): azimuth 0 straight ahead, positive to the left (anticlockwise seen from above), −180…180;
// elevation 0 at ear height, positive up, −90…90.

/// <summary>
///     A channel format: an ordered speaker layout (<see cref="SpeakerLayouts" />). The engine's buffers carry a strip's
///     channels in its layout's order. Ambisonics are out of scope.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChannelFormat>))]
public enum ChannelFormat {
    /// <summary>1.0: <c>M+000</c>.</summary>
    [JsonStringEnumMemberName("mono")]
    Mono,

    /// <summary>2.0 (ITU-R BS.2051 system A, 0+2+0): <c>M+030 M-030</c>. Every existing project is stereo throughout.</summary>
    [JsonStringEnumMemberName("stereo")]
    Stereo,

    /// <summary>3.0: <c>M+030 M-030 M+000</c>.</summary>
    [JsonStringEnumMemberName("lcr")]
    Lcr,

    /// <summary>4.0: <c>M+045 M-045 M+135 M-135</c>.</summary>
    [JsonStringEnumMemberName("quad")]
    Quad,

    /// <summary>5.0: <c>M+030 M-030 M+000 M+110 M-110</c>.</summary>
    [JsonStringEnumMemberName("surround50")]
    Surround50,

    /// <summary>5.1 (system B, 0+5+0): <c>M+030 M-030 M+000 LFE1 M+110 M-110</c>.</summary>
    [JsonStringEnumMemberName("surround51")]
    Surround51,

    /// <summary>7.0: <c>M+030 M-030 M+000 M+090 M-090 M+135 M-135</c>.</summary>
    [JsonStringEnumMemberName("surround70")]
    Surround70,

    /// <summary>7.1 (system I, 0+7+0): <c>M+030 M-030 M+000 LFE1 M+090 M-090 M+135 M-135</c>.</summary>
    [JsonStringEnumMemberName("surround71")]
    Surround71,

    /// <summary>7.1.2: 7.1 and <c>U+090 U-090</c> (top middle; not a BS.2051 system).</summary>
    [JsonStringEnumMemberName("surround712")]
    Surround712,

    /// <summary>7.1.4 (system J, 4+7+0): 7.1 and <c>U+045 U-045 U+135 U-135</c>.</summary>
    [JsonStringEnumMemberName("surround714")]
    Surround714,

    /// <summary>
    ///     9.1.6: 7.1, the wides <c>M+060 M-060</c>, and <c>U+045 U-045 U+090 U-090 U+135 U-135</c> (not a BS.2051
    ///     system).
    /// </summary>
    [JsonStringEnumMemberName("surround916")]
    Surround916
}

/// <summary>The ADM speaker labels (ITU-R BS.2051, BS.2094) the layouts use.</summary>
public static class SpeakerLabels {
    public const string Left = "M+030";
    public const string Right = "M-030";
    public const string Centre = "M+000";
    public const string Lfe = "LFE1";
    public const string LeftSurround = "M+110";
    public const string RightSurround = "M-110";
    public const string LeftSide = "M+090";
    public const string RightSide = "M-090";
    public const string LeftRear = "M+135";
    public const string RightRear = "M-135";
    public const string LeftWide = "M+060";
    public const string RightWide = "M-060";

    /// <summary>Quad's front pair.</summary>
    public const string LeftFrontQuad = "M+045";

    public const string RightFrontQuad = "M-045";
    public const string TopFrontLeft = "U+045";
    public const string TopFrontRight = "U-045";
    public const string TopMiddleLeft = "U+090";
    public const string TopMiddleRight = "U-090";
    public const string TopRearLeft = "U+135";
    public const string TopRearRight = "U-135";
}

/// <summary>
///     A speaker of a layout: its ADM label and nominal position (degrees, as the file header says); <c>lfe</c> marks the
///     low-frequency channel, which panners feed only through their LFE level and object rendering never uses.
/// </summary>
public sealed record Speaker(string Label, double Azimuth, double Elevation, bool Lfe = false);

/// <summary>
///     A channel format's speakers, in buffer order: its display name (<c>7.1.4</c>), its BS.2051 system name
///     (<c>4+7+0</c>; null for layouts BS.2051 does not define) and the speakers.
/// </summary>
public sealed record SpeakerLayout(
    ChannelFormat Format,
    string Name,
    string? ItuName,
    IReadOnlyList<Speaker> Speakers
) {
    [JsonIgnore]
    public int ChannelCount => Speakers.Count;

    /// <summary>Whether any speaker is above ear height (the Surround Panner's elevation applies).</summary>
    [JsonIgnore]
    public bool HasHeight => Speakers.Any(static s => s.Elevation > 0);

    [JsonIgnore]
    public bool HasLfe => Speakers.Any(static s => s.Lfe);

    /// <summary>The channel index of <paramref name="label" />, or −1.</summary>
    public int IndexOf(string label) {
        for (var i = 0; i < Speakers.Count; i++) {
            if (Speakers[i].Label == label) {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
///     The speaker layouts of every <see cref="ChannelFormat" /> (positions per ITU-R BS.2051 where it defines them:
///     the middle layer at 0°, the upper layer at 30°, the LFE nominally at 45°, −30°). Channel order is the ADM/SMPTE
///     order: L, R, C, LFE, then the surrounds (sides before rears), wides, and the upper layer front to back. A
///     WAVE_FORMAT_EXTENSIBLE file orders channels by its channel mask instead (rears before sides in 7.x): its writer
///     and reader reorder.
/// </summary>
public static class SpeakerLayouts {
    static readonly Speaker L = new(SpeakerLabels.Left, 30, 0);
    static readonly Speaker R = new(SpeakerLabels.Right, -30, 0);
    static readonly Speaker C = new(SpeakerLabels.Centre, 0, 0);
    static readonly Speaker Lfe = new(SpeakerLabels.Lfe, 45, -30, true);
    static readonly Speaker Ls = new(SpeakerLabels.LeftSurround, 110, 0);
    static readonly Speaker Rs = new(SpeakerLabels.RightSurround, -110, 0);
    static readonly Speaker Lss = new(SpeakerLabels.LeftSide, 90, 0);
    static readonly Speaker Rss = new(SpeakerLabels.RightSide, -90, 0);
    static readonly Speaker Lrs = new(SpeakerLabels.LeftRear, 135, 0);
    static readonly Speaker Rrs = new(SpeakerLabels.RightRear, -135, 0);
    static readonly Speaker Lw = new(SpeakerLabels.LeftWide, 60, 0);
    static readonly Speaker Rw = new(SpeakerLabels.RightWide, -60, 0);
    static readonly Speaker Ltf = new(SpeakerLabels.TopFrontLeft, 45, 30);
    static readonly Speaker Rtf = new(SpeakerLabels.TopFrontRight, -45, 30);
    static readonly Speaker Ltm = new(SpeakerLabels.TopMiddleLeft, 90, 30);
    static readonly Speaker Rtm = new(SpeakerLabels.TopMiddleRight, -90, 30);
    static readonly Speaker Ltr = new(SpeakerLabels.TopRearLeft, 135, 30);
    static readonly Speaker Rtr = new(SpeakerLabels.TopRearRight, -135, 30);

    /// <summary>Every layout, in <see cref="ChannelFormat" /> order.</summary>
    public static IReadOnlyList<SpeakerLayout> All { get; } = [
        new(ChannelFormat.Mono, "1.0", null, [C]),
        new(ChannelFormat.Stereo, "2.0", "0+2+0", [L, R]),
        new(ChannelFormat.Lcr, "3.0", null, [L, R, C]),
        new(
            ChannelFormat.Quad,
            "4.0",
            null,
            [
                new(SpeakerLabels.LeftFrontQuad, 45, 0), new(SpeakerLabels.RightFrontQuad, -45, 0), Lrs, Rrs
            ]
        ),
        new(ChannelFormat.Surround50, "5.0", null, [L, R, C, Ls, Rs]),
        new(ChannelFormat.Surround51, "5.1", "0+5+0", [L, R, C, Lfe, Ls, Rs]),
        new(ChannelFormat.Surround70, "7.0", null, [L, R, C, Lss, Rss, Lrs, Rrs]),
        new(ChannelFormat.Surround71, "7.1", "0+7+0", [L, R, C, Lfe, Lss, Rss, Lrs, Rrs]),
        new(ChannelFormat.Surround712, "7.1.2", null, [L, R, C, Lfe, Lss, Rss, Lrs, Rrs, Ltm, Rtm]),
        new(ChannelFormat.Surround714, "7.1.4", "4+7+0", [L, R, C, Lfe, Lss, Rss, Lrs, Rrs, Ltf, Rtf, Ltr, Rtr]),
        new(
            ChannelFormat.Surround916,
            "9.1.6",
            null,
            [L, R, C, Lfe, Lss, Rss, Lrs, Rrs, Lw, Rw, Ltf, Rtf, Ltm, Rtm, Ltr, Rtr]
        )
    ];

    public static SpeakerLayout Of(ChannelFormat format) => All[(int)format];

    public static int ChannelCount(ChannelFormat format) => Of(format).ChannelCount;

    /// <summary>Whether <paramref name="format" /> is wider than stereo (the Surround and Object Panners apply).</summary>
    public static bool IsSurround(ChannelFormat format) => format > ChannelFormat.Stereo;
}

/// <summary>How a strip's panner maps its input channels to its output format.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PannerMode>))]
public enum PannerMode {
    /// <summary>Pan or balance (<c>ChannelStrip.pan</c>), as before Phase 10: for a stereo (or mono) output.</summary>
    [JsonStringEnumMemberName("stereo")]
    Stereo,

    /// <summary>The Surround Panner (<c>panner.surround</c>) into a bed: the strip's output format (a surround bus or master).</summary>
    [JsonStringEnumMemberName("surround")]
    Surround,

    /// <summary>
    ///     A 3D object (<c>panner.object</c>): the strip's mono or stereo signal is an object the master renders to its
    ///     layout (after the bed sum, before the master's inserts), not a bed channel. Only for a strip whose output is
    ///     the master of a surround project.
    /// </summary>
    [JsonStringEnumMemberName("object")]
    Object
}

/// <summary>
///     The Surround Panner (2D, as Logic's): <c>angle</c> where the puck points (degrees, ADM azimuth); <c>diversity</c>
///     0…1, how far it is from the centre of the circle (0: every speaker equally, 1: a point source between the two
///     speakers at the angle); <c>elevation</c> 0…90 for formats with height (0 elsewhere); <c>centreLevelDb</c>
///     −144…0, how much of the front image the centre speaker takes (−144: a phantom centre); <c>lfeLevelDb</c> −144…0,
///     the low-passed send to the LFE (−144: none); <c>spread</c> 0…180, the angle between a stereo input's two channels
///     around the puck (a mono input ignores it).
/// </summary>
public sealed record SurroundPanner(
    double Angle,
    double Diversity,
    double Elevation,
    double CentreLevelDb,
    double LfeLevelDb,
    double Spread
) {
    /// <summary>A new strip's: front centre as a point, no elevation, centre at 0 dB, no LFE, a stereo input at ±30°.</summary>
    public static SurroundPanner Default { get; } = new(0, 1, 0, 0, -144, 60);
}

/// <summary>
///     The 3D Object Panner: an object's position, polar as ADM's (<c>audioBlockFormat</c> of type Objects):
///     <c>azimuth</c> and <c>elevation</c> in degrees, <c>distance</c> 0…1 (1: on the speakers' sphere), and
///     <c>size</c> 0…1 (its spread over neighbouring speakers; ADM width, height and depth together).
/// </summary>
public sealed record ObjectPanner(double Azimuth, double Elevation, double Distance, double Size) {
    /// <summary>A new strip's: straight ahead, at ear height, on the sphere, a point.</summary>
    public static ObjectPanner Default { get; } = new(0, 0, 1, 0);
}

/// <summary>
///     A strip's panner (<c>ChannelStrip.panner</c>): the mode that applies, and both panners' settings, which stay when
///     the mode changes. Automation addresses (<c>AutomationLane.target</c>): <c>surround:angle</c>,
///     <c>surround:diversity</c>, <c>surround:elevation</c>, <c>surround:centre</c> (dB), <c>surround:lfe</c> (dB),
///     <c>surround:spread</c>, <c>object:azimuth</c>, <c>object:elevation</c>, <c>object:distance</c>,
///     <c>object:size</c>, in the units above; the same strings are Smart Control mapping targets.
/// </summary>
public sealed record Panner(PannerMode Mode, SurroundPanner Surround, ObjectPanner Object) {
    /// <summary>A new strip's: stereo pan, the Surround and Object Panners at their defaults.</summary>
    public static Panner Default { get; } = new(PannerMode.Stereo, SurroundPanner.Default, ObjectPanner.Default);
}

/// <summary>The automation and Smart Control addresses of the panners' parameters (<see cref="Panner" />).</summary>
public static class PannerTargets {
    public const string SurroundAngle = "surround:angle";
    public const string SurroundDiversity = "surround:diversity";
    public const string SurroundElevation = "surround:elevation";
    public const string SurroundCentre = "surround:centre";
    public const string SurroundLfe = "surround:lfe";
    public const string SurroundSpread = "surround:spread";
    public const string ObjectAzimuth = "object:azimuth";
    public const string ObjectElevation = "object:elevation";
    public const string ObjectDistance = "object:distance";
    public const string ObjectSize = "object:size";

    public static IReadOnlyList<string> All { get; } = [
        SurroundAngle,
        SurroundDiversity,
        SurroundElevation,
        SurroundCentre,
        SurroundLfe,
        SurroundSpread,
        ObjectAzimuth,
        ObjectElevation,
        ObjectDistance,
        ObjectSize
    ];
}

/// <summary>
///     Monitoring (<c>GET/PUT /settings/monitoring</c>), stored in the user settings, not the project: the speaker map is
///     the machine's and binaural listening the listener's. It happens after the master and before the device, and never
///     changes bounces or ADM exports. <c>binaural</c> renders the master's speakers (and objects) through HRTFs to
///     outputs 1–2; otherwise <c>downmixTo</c> (a format smaller than the master's; null: none) down-mixes per ITU-R
///     BS.775 and sends its channels through the map. <c>speakerMap[i]</c> is the 1-based device output of channel i of
///     what is sent (0: not sent); an empty or short map sends channel i to output i + 1. <c>section</c> is the monitor
///     section (null in a request: as it is).
/// </summary>
public sealed record MonitoringSettings(
    bool Binaural,
    ChannelFormat? DownmixTo,
    IReadOnlyList<int> SpeakerMap,
    MonitorSection? Section = null
) {
    /// <summary>A new user's: speakers, no down-mix, channel i to output i + 1, the monitor section flat.</summary>
    public static MonitoringSettings Default { get; } = new(false, null, [], MonitorSection.Default);
}

/// <summary>
///     The monitor section: what the speakers get after the master, never what a bounce or the listen-in hears. Output
///     pairs a strip is routed to directly bypass it. The level is <c>levelDb</c>, or with <c>reference</c> the calibrated
///     <c>referenceDb</c>; <c>dim</c> takes <c>dimDb</c> (0 or less) off whichever applies; <c>mono</c> folds the front
///     pair
///     to check it in mono.
/// </summary>
public sealed record MonitorSection(
    bool Dim,
    double DimDb,
    bool Mono,
    bool Reference,
    double ReferenceDb,
    double LevelDb
) {
    /// <summary>Unity, the dim −20 dB, the reference −12 dB.</summary>
    public static MonitorSection Default { get; } = new(false, -20, false, false, -12, 0);
}

/// <summary>
///     ADM BWF export (<c>POST /project/export/adm</c>, job <c>admExport</c>): the master's mix as BW64 with
///     <c>chna</c> and <c>axml</c> (<c>ds64</c> above 4 GB) per the EBU ADM production profile: the bed (everything not
///     an object, in the project's format) as <c>DirectSpeakers</c>, and each object-mode track among
///     <c>trackIds</c> (null: every one) as <c>Objects</c> with an <c>audioBlockFormat</c> per automation change (with
///     interpolation) or per 20 ms at most. Range as a bounce's (null: the cycle when on, else the project); 24-bit or
///     32-bit float at the project's rate. <c>.wav</c> added when missing.
/// </summary>
public sealed record AdmExportRequest(
    string FileName,
    string? Directory = null,
    IReadOnlyList<string>? TrackIds = null,
    long? StartTick = null,
    long? EndTick = null,
    int BitDepth = 24
);

/// <summary>
///     ADM BWF import (<c>POST /project/import/adm</c>, job <c>admImport</c>): a BW64/RF64/WAV file with <c>chna</c> and
///     <c>axml</c> on the server's disk. Each bed becomes an audio track whose input format is the bed's (passed
///     straight through to the master) and each object a mono or stereo track in object mode with its positions as
///     automation, through an
///     <c>importTracks</c> command (one undo step); the media are copied into the project.
/// </summary>
public sealed record AdmImportRequest(string Path);

using System.Text.Json.Serialization;

namespace Anode.Contracts;

// The Mastering Assistant (docs/12-phase-11.md): a built-in insert (anode.mastering-assistant) on the master like any
// other (it bounces, freezes and automates), placed last by default. Its settings are its insert parameters
// (MasteringParameters, automatable); its analysis (a job over an offline render of the master before it) is stored in
// its insert (Insert.mastering). The chain (EQ matched to the character's curve, gentle compression, the character's
// colour, width and a true-peak limiter reaching the loudness target) follows from the analysis and the settings.

/// <summary>The Mastering Assistant's character: its reference curve and its colour stage.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MasteringCharacter>))]
public enum MasteringCharacter {
    /// <summary>No colour; a balanced curve.</summary>
    [JsonStringEnumMemberName("clean")]
    Clean,

    /// <summary>Tube-style even harmonics and a warmer curve.</summary>
    [JsonStringEnumMemberName("valve")]
    Valve,

    /// <summary>Transient-forward compression and a fuller low end.</summary>
    [JsonStringEnumMemberName("punch")]
    Punch,

    /// <summary>The lightest touch: the curve at half weight, no colour.</summary>
    [JsonStringEnumMemberName("transparent")]
    Transparent
}

/// <summary>
///     The Mastering Assistant insert's parameter ids (in <c>Insert.parameters</c>, automatable as
///     <c>insert:{insertId}:{parameterId}</c>) and their ranges: <c>character</c> (the index of
///     <see cref="MasteringCharacter" />, 0…3), <c>targetLufs</c> (−24…−6, default −14), <c>ceilingDbtp</c> (−3…0,
///     default −1), <c>eq</c> (how much of the analysis' EQ correction, 0…1, default 1), <c>dynamics</c> (how much of its
///     compression, 0…1, default 1), <c>width</c> (0…2, 1 unchanged), <c>matchedBypass</c> (0/1: the input, level-matched
///     to the output's loudness, for comparing).
/// </summary>
public static class MasteringParameters {
    public const string PluginId = "anode.mastering-assistant";
    public const string Character = "character";
    public const string TargetLufs = "targetLufs";
    public const string CeilingDbtp = "ceilingDbtp";
    public const string Eq = "eq";
    public const string Dynamics = "dynamics";
    public const string Width = "width";
    public const string MatchedBypass = "matchedBypass";

    public const double DefaultTargetLufs = -14;
    public const double DefaultCeilingDbtp = -1;
}

/// <summary>
///     The bands of the analysis' spectrum and the characters' curves: ISO 266 third-octave centres from 25 Hz to
///     20 kHz (30 bands). <c>Anode.Dsp.Mastering</c> uses the same.
/// </summary>
public static class MasteringBands {
    public static IReadOnlyList<double> CentresHz { get; } = [
        25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150,
        4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000
    ];
}

/// <summary>
///     What the analysis found over <c>startTick</c>…<c>endTick</c> of the master before the assistant: integrated
///     loudness (BS.1770, LUFS), the loudest short-term loudness, the loudness range (LU), true peak (dBTP, 4×
///     oversampled), crest factor (peak to RMS, dB), stereo width (0 mono … 1 uncorrelated; the side-to-mid energy
///     ratio, clamped), the smoothed spectrum per band of <see cref="MasteringBands" /> (dB, relative to its mean), and
///     the character's reference curve it was matched against (the same bands, dB). <c>revision</c> is the project
///     revision it rendered.
/// </summary>
public sealed record MasteringAnalysis(
    long StartTick,
    long EndTick,
    long Revision,
    double IntegratedLufs,
    double MaxShortTermLufs,
    double LoudnessRange,
    double TruePeakDbtp,
    double CrestFactorDb,
    double Width,
    IReadOnlyList<double> SpectrumDb,
    IReadOnlyList<double> CurveDb
);

/// <summary>The Mastering Assistant's settings, as its parameters hold them (<see cref="MasteringParameters" />).</summary>
public sealed record MasteringSettings(
    MasteringCharacter Character,
    double TargetLufs,
    double CeilingDbtp,
    double Eq,
    double Dynamics,
    double Width,
    bool MatchedBypass
) {
    public static MasteringSettings Default { get; } = new(
        MasteringCharacter.Clean,
        MasteringParameters.DefaultTargetLufs,
        MasteringParameters.DefaultCeilingDbtp,
        1,
        1,
        1,
        false
    );
}

/// <summary>
///     The master's Mastering Assistant (<c>GET /project/mastering</c>): its insert, settings and analysis (null: not
///     analysed yet; it then passes the audio through its limiter only).
/// </summary>
public sealed record MasteringState(string InsertId, MasteringSettings Settings, MasteringAnalysis? Analysis);

/// <summary>
///     Analyse (<c>POST /project/mastering/analyse</c>, job <c>masteringAnalysis</c>): renders the master before the
///     assistant offline over <c>startTick</c>…<c>endTick</c> (left out: the cycle when it is on, else the whole song),
///     analyses it, and stores the analysis in the assistant (one undo step, <c>setMasteringAnalysis</c>). Without an
///     assistant on the master, <c>add</c> adds one last first (else 404).
/// </summary>
public sealed record MasteringAnalysisRequest(long? StartTick = null, long? EndTick = null, bool Add = true);

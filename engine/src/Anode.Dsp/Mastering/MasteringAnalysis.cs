namespace Anode.Dsp.Mastering;

/// <summary>
///     The bands of the Mastering Assistant's spectrum and curves: ISO 266 third-octave centres, 25 Hz … 20 kHz (the
///     contract's <c>MasteringBands</c>; a test checks they agree). Real data, not a stub.
/// </summary>
public static class MasteringBands {
    public static IReadOnlyList<float> CentresHz { get; } = [
        25, 31.5f, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150,
        4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000
    ];
}

/// <summary>The Mastering Assistant's characters, in the contract's <c>MasteringCharacter</c> order.</summary>
public enum MasteringCharacterKind {
    Clean,
    Valve,
    Punch,
    Transparent
}

/// <summary>
///     What the analysis measured (the contract's <c>MasteringAnalysis</c> without its range): BS.1770 integrated
///     loudness (LUFS), the loudest short-term loudness, the loudness range (LU), true peak (dBTP), crest factor (dB),
///     width (0 mono … 1), and the smoothed spectrum per band of <see cref="MasteringBands" /> (dB, relative to its
///     mean).
/// </summary>
public sealed record MasteringMeasurement(
    double IntegratedLufs,
    double MaxShortTermLufs,
    double LoudnessRange,
    double TruePeakDbtp,
    double CrestFactorDb,
    double Width,
    float[] SpectrumDb
);

/// <summary>
///     The Mastering Assistant's analysis (docs/12-phase-11.md, workstream E): BS.1770 loudness and true peak from
///     Phase 4's meters, a smoothed spectrum, crest factor and width, over an offline render of the master before the
///     assistant.
/// </summary>
public static class MasteringAnalyzer {
    /// <summary>Measures a stereo render (<paramref name="left" />, <paramref name="right" />, equal lengths).</summary>
    public static MasteringMeasurement Analyse(ReadOnlySpan<float> left, ReadOnlySpan<float> right, float sampleRate) =>
        throw Pending.WorkstreamE();

    /// <summary>The reference curve of <paramref name="character" /> per band (dB).</summary>
    public static float[] Curve(MasteringCharacterKind character) => throw Pending.WorkstreamE();
}

/// <summary>
///     The chain the analysis sets (the IPC's <c>EngineMasteringChain</c>): the EQ's gain per band at full strength
///     (dB), the compressor's threshold (dBFS) and ratio, the analysed integrated loudness (LUFS) and the loudness the
///     EQ and compressor add at full strength (LU).
/// </summary>
public sealed record MasteringChainSettings(
    float[] EqGainsDb,
    float CompressorThresholdDb,
    float CompressorRatio,
    float LoudnessLufs,
    float ChainGainDb
);

/// <summary>Designs the chain from a measurement and a character (workstream E): EQ matched to the curve, limited.</summary>
public static class MasteringChainDesigner {
    public static MasteringChainSettings Design(MasteringMeasurement measurement, MasteringCharacterKind character) =>
        throw Pending.WorkstreamE();
}

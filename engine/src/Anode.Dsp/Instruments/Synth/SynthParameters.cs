namespace Anode.Dsp.Instruments.Synth;

/// <summary>
///     <c>anode.synth</c>'s parameters. The indices are the order of <see cref="All" />; the ids are stable (projects
///     store values by id), so new parameters go at the end.
/// </summary>
public static class SynthParameters {
    public const int Osc1Wave = 0;
    public const int Osc1Octave = 1;
    public const int Osc1Fine = 2;
    public const int Osc1Width = 3;
    public const int Osc2Wave = 4;
    public const int Osc2Octave = 5;
    public const int Osc2Fine = 6;
    public const int Osc2Width = 7;
    public const int OscMix = 8;
    public const int FilterMode = 9;
    public const int Cutoff = 10;
    public const int Resonance = 11;
    public const int KeyTracking = 12;
    public const int FilterEnvAmount = 13;
    public const int FilterAttack = 14;
    public const int FilterDecay = 15;
    public const int FilterSustain = 16;
    public const int FilterRelease = 17;
    public const int AmpAttack = 18;
    public const int AmpDecay = 19;
    public const int AmpSustain = 20;
    public const int AmpRelease = 21;
    public const int LfoShape = 22;
    public const int LfoRate = 23;
    public const int LfoPitch = 24;
    public const int LfoFilter = 25;
    public const int LfoAmp = 26;
    public const int LfoModWheel = 27;
    public const int Glide = 28;
    public const int Unison = 29;
    public const int UnisonDetune = 30;
    public const int VelocitySensitivity = 31;
    public const int BendRange = 32;
    public const int Volume = 33;

    /// <summary>The filter envelope's reach at ±100 %, in octaves.</summary>
    public const double EnvelopeOctaves = 6;

    /// <summary>The LFO's filter reach at 100 %, in octaves either way.</summary>
    public const double LfoFilterOctaves = 3;

    static readonly string[] Waves = ["Saw", "Pulse", "Triangle", "Sine", "Noise"];
    static readonly string[] Modes = ["Low-pass 24 dB", "Low-pass 12 dB", "High-pass 12 dB", "Band-pass 12 dB"];
    static readonly string[] LfoShapes = ["Sine", "Triangle", "Saw", "Square", "Sample & Hold"];

    public static IReadOnlyList<InstrumentParameter> All { get; } = [
        Choice(Osc1Wave, "osc1Wave", "Osc 1 Wave", Waves, 0),
        new(Osc1Octave, "osc1Octave", "Osc 1 Octave", -3, 3, 0, "oct", 6),
        new(Osc1Fine, "osc1Fine", "Osc 1 Fine", -100, 100, 0, "ct"),
        new(Osc1Width, "osc1Width", "Osc 1 Pulse Width", 5, 95, 50, "%"),
        Choice(Osc2Wave, "osc2Wave", "Osc 2 Wave", Waves, 0),
        new(Osc2Octave, "osc2Octave", "Osc 2 Octave", -3, 3, 0, "oct", 6),
        new(Osc2Fine, "osc2Fine", "Osc 2 Fine", -100, 100, 7, "ct"),
        new(Osc2Width, "osc2Width", "Osc 2 Pulse Width", 5, 95, 50, "%"),
        new(OscMix, "oscMix", "Osc Mix", 0, 100, 50, "%", Macro: 6),
        Choice(FilterMode, "filterMode", "Filter Mode", Modes, 0),
        new(Cutoff, "cutoff", "Cutoff", 20, 20_000, 1_500, "Hz", Macro: 1),
        new(Resonance, "resonance", "Resonance", 0, 100, 15, "%", Macro: 2),
        new(KeyTracking, "keyTracking", "Key Tracking", 0, 100, 50, "%"),
        new(FilterEnvAmount, "filterEnvAmount", "Filter Env Amount", -100, 100, 35, "%", Macro: 3),
        new(FilterAttack, "filterAttack", "Filter Attack", 0, 10_000, 5, "ms"),
        new(FilterDecay, "filterDecay", "Filter Decay", 1, 10_000, 600, "ms", Macro: 10),
        new(FilterSustain, "filterSustain", "Filter Sustain", 0, 100, 30, "%"),
        new(FilterRelease, "filterRelease", "Filter Release", 1, 10_000, 400, "ms"),
        new(AmpAttack, "ampAttack", "Amp Attack", 0, 10_000, 2, "ms", Macro: 4),
        new(AmpDecay, "ampDecay", "Amp Decay", 1, 10_000, 800, "ms", Macro: 11),
        new(AmpSustain, "ampSustain", "Amp Sustain", 0, 100, 80, "%"),
        new(AmpRelease, "ampRelease", "Amp Release", 1, 10_000, 300, "ms", Macro: 5),
        Choice(LfoShape, "lfoShape", "LFO Shape", LfoShapes, 0),
        new(LfoRate, "lfoRate", "LFO Rate", 0.05f, 20, 5, "Hz", Macro: 7),
        new(LfoPitch, "lfoPitch", "LFO to Pitch", 0, 12, 0.5f, "st"),
        new(LfoFilter, "lfoFilter", "LFO to Filter", 0, 100, 0, "%"),
        new(LfoAmp, "lfoAmp", "LFO to Amp", 0, 100, 0, "%"),
        new(LfoModWheel, "lfoModWheel", "LFO Mod Wheel", 0, 100, 100, "%"),
        new(Glide, "glide", "Glide", 0, 2_000, 0, "ms", Macro: 8),
        new(Unison, "unison", "Unison Voices", 1, 4, 1, string.Empty, 3),
        new(UnisonDetune, "unisonDetune", "Unison Detune", 0, 100, 15, "ct", Macro: 9),
        new(VelocitySensitivity, "velocitySensitivity", "Velocity Sensitivity", 0, 100, 70, "%"),
        new(BendRange, "bendRange", "Pitch Bend Range", 0, 24, 2, "st", 24),
        new(Volume, "volume", "Volume", -60, 6, -6, "dB", Macro: 12)
    ];

    static InstrumentParameter Choice(int index, string id, string name, string[] values, int value) =>
        new(index, id, name, 0, values.Length - 1, value, string.Empty, values.Length - 1, values);
}

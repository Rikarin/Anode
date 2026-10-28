using Anode.Dsp.Instruments;

namespace Anode.Dsp.Effects;

/// <summary>
///     Builds an effect's parameter list in index order: each call appends one parameter and returns its index, so the
///     effect's index constants and its list cannot drift apart (a test checks every index against its position).
/// </summary>
internal sealed class EffectParameters {
    public const string Vendor = "Anode";
    public const string Version = "1.0.0";

    static readonly string[] OffOn = ["Off", "On"];

    readonly List<InstrumentParameter> list = [];

    public IReadOnlyList<InstrumentParameter> All => list;

    public int Add(string id, string name, float min, float max, float value, string unit) {
        list.Add(new(list.Count, id, name, min, max, value, unit));
        return list.Count - 1;
    }

    /// <summary>A parameter in <paramref name="steps" /> equal steps from <paramref name="min" /> to <paramref name="max" />.</summary>
    public int Stepped(string id, string name, float min, float max, float value, string unit, int steps) {
        list.Add(new(list.Count, id, name, min, max, value, unit, steps));
        return list.Count - 1;
    }

    public int Choice(string id, string name, IReadOnlyList<string> values, int value) {
        list.Add(new(list.Count, id, name, 0, values.Count - 1, value, string.Empty, values.Count - 1, values));
        return list.Count - 1;
    }

    public int Toggle(string id, string name, bool value) => Choice(id, name, OffOn, value ? 1 : 0);

    /// <summary>The descriptor of an effect with these parameters.</summary>
    public EffectDescriptor Describe(string id, string name, string category, bool sidechain = false) =>
        new(id, name, Vendor, Version, category, list.ToArray(), sidechain);
}

/// <summary>The categories of <see cref="EffectDescriptor.Category" />.</summary>
public static class EffectCategories {
    public const string Eq = "EQ";
    public const string Dynamics = "Dynamics";
    public const string Delay = "Delay";
    public const string Reverb = "Reverb";
    public const string Modulation = "Modulation";
    public const string Distortion = "Distortion";
    public const string Filter = "Filter";
    public const string Utility = "Utility";
    public const string Metering = "Metering";
}

/// <summary>Decibel conversions.</summary>
public static class Decibels {
    public static float ToGain(float db) => MathF.Pow(10, db * 0.05f);

    public static double ToGain(double db) => Math.Pow(10, db * 0.05);

    /// <summary>A gain in dB, <see cref="AudioEffect.FloorDb" /> for silence.</summary>
    public static float FromGain(double gain) =>
        gain <= 1e-6 ? AudioEffect.FloorDb : Math.Max(AudioEffect.FloorDb, (float)(20 * Math.Log10(gain)));
}

/// <summary>
///     The note values of the tempo-synced parameters (<c>1/64</c> … <c>2/1</c>, with triplets and dotted values), and
///     their length in beats (quarter notes).
/// </summary>
public static class NoteValues {
    public static IReadOnlyList<string> Names { get; } = [
        "1/64", "1/32T", "1/32", "1/16T", "1/16", "1/16D", "1/8T", "1/8", "1/8D", "1/4T", "1/4", "1/4D", "1/2T", "1/2",
        "1/2D", "1/1", "2/1"
    ];

    static readonly double[] Beats = [
        1.0 / 16, 1.0 / 12, 1.0 / 8, 1.0 / 6, 1.0 / 4, 3.0 / 8, 1.0 / 3, 1.0 / 2, 3.0 / 4, 2.0 / 3, 1, 1.5, 4.0 / 3, 2, 3,
        4, 8
    ];

    /// <summary>The index of the quarter note.</summary>
    public const int Quarter = 10;

    /// <summary>The index of the eighth note.</summary>
    public const int Eighth = 7;

    /// <summary>The index of the dotted eighth.</summary>
    public const int DottedEighth = 8;

    /// <summary>The index of the sixteenth.</summary>
    public const int Sixteenth = 4;

    public static double BeatsOf(int index) => Beats[Math.Clamp(index, 0, Beats.Length - 1)];

    /// <summary>The length of note value <paramref name="index" /> at <paramref name="tempo" /> bpm, in seconds.</summary>
    public static double Seconds(int index, double tempo) => BeatsOf(index) * 60 / Math.Max(tempo, 1);
}

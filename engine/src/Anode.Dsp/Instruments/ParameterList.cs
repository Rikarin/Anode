namespace Anode.Dsp.Instruments;

/// <summary>
///     Builds a T2 instrument's or MIDI effect's parameter list in index order: each call appends one parameter and
///     returns its index, so the index constants and the list cannot drift apart. Ids are stable (projects store values
///     by id); new parameters go at the end.
/// </summary>
internal sealed class ParameterList {
    static readonly string[] OffOn = ["Off", "On"];

    readonly List<InstrumentParameter> list = [];

    public IReadOnlyList<InstrumentParameter> All => list;

    public int Add(string id, string name, float min, float max, float value, string unit, int? macro = null) {
        list.Add(new(list.Count, id, name, min, max, value, unit, Macro: macro));
        return list.Count - 1;
    }

    /// <summary>A parameter in <paramref name="steps" /> equal steps from <paramref name="min" /> to <paramref name="max" />.</summary>
    public int Stepped(
        string id,
        string name,
        float min,
        float max,
        float value,
        string unit,
        int steps,
        int? macro = null
    ) {
        list.Add(new(list.Count, id, name, min, max, value, unit, steps, Macro: macro));
        return list.Count - 1;
    }

    public int Choice(string id, string name, IReadOnlyList<string> values, int value, int? macro = null) {
        list.Add(new(list.Count, id, name, 0, values.Count - 1, value, string.Empty, values.Count - 1, values, macro));
        return list.Count - 1;
    }

    public int Toggle(string id, string name, bool value, int? macro = null) =>
        Choice(id, name, OffOn, value ? 1 : 0, macro);
}

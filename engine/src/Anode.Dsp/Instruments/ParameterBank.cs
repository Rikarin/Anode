namespace Anode.Dsp.Instruments;

/// <summary>
///     An instrument's parameter values, by index, clamped to their ranges (a discrete one snapped to its steps). Set
///     and read on the audio thread; it allocates nothing after construction. Smoothing is the instrument's job.
/// </summary>
internal sealed class ParameterBank {
    readonly IReadOnlyList<InstrumentParameter> parameters;
    readonly float[] values;

    public ParameterBank(IReadOnlyList<InstrumentParameter> parameters) {
        this.parameters = parameters;
        values = new float[parameters.Count];
        for (var i = 0; i < values.Length; i++) {
            values[i] = parameters[i].Default;
        }
    }

    public int Count => values.Length;

    public float this[int index] => values[index];

    /// <summary>
    ///     Sets <paramref name="index" /> to <paramref name="value" />, clamped and snapped; an index out of range or a
    ///     value that is not a number is ignored.
    /// </summary>
    public void Set(int index, float value) {
        if ((uint)index < (uint)values.Length && !float.IsNaN(value)) {
            values[index] = Sanitize(parameters[index], value);
        }
    }

    /// <summary>The value of <paramref name="index" />, 0 for an index out of range.</summary>
    public float Get(int index) => (uint)index < (uint)values.Length ? values[index] : 0f;

    /// <summary>The index of the parameter with id <paramref name="id" />, or −1.</summary>
    public int IndexOf(string id) {
        for (var i = 0; i < parameters.Count; i++) {
            if (parameters[i].Id == id) {
                return i;
            }
        }

        return -1;
    }

    public static float Sanitize(InstrumentParameter p, float value) {
        value = Math.Clamp(value, p.Min, p.Max);
        if (p.Steps > 0) {
            var step = (p.Max - p.Min) / p.Steps;
            value = p.Min + MathF.Round((value - p.Min) / step) * step;
        }

        return value;
    }
}

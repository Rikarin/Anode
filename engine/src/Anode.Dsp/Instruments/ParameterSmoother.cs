namespace Anode.Dsp.Instruments;

/// <summary>
///     Control-rate smoothing of an instrument's parameters, as <c>anode.synth</c> does it: continuous parameters glide
///     toward their values with a 10 ms time constant at every control point, discrete ones jump; with no voice
///     sounding, everything jumps (<see cref="Snap" />). Allocation-free after construction.
/// </summary>
internal sealed class ParameterSmoother(IReadOnlyList<InstrumentParameter> definitions) {
    const double SmoothingSeconds = 0.01;

    readonly float[] values = new float[definitions.Count];
    double coefficient = 1;

    public float this[int index] => values[index];

    /// <summary>Sets the time constant for a control interval of <paramref name="interval" /> samples.</summary>
    public void Prepare(double sampleRate, int interval) =>
        coefficient = 1 - Math.Exp(-interval / (SmoothingSeconds * sampleRate));

    public void Snap(ParameterBank parameters) {
        for (var i = 0; i < values.Length; i++) {
            values[i] = parameters[i];
        }
    }

    public void Tick(ParameterBank parameters) {
        for (var i = 0; i < values.Length; i++) {
            var target = parameters[i];
            values[i] = definitions[i].Steps > 0 ? target : values[i] + (float)((target - values[i]) * coefficient);
        }
    }
}

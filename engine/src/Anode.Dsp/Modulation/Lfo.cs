namespace Anode.Dsp.Modulation;

/// <summary>The shapes an <see cref="Lfo" /> produces.</summary>
public enum LfoShape {
    Sine,
    Triangle,
    Saw,
    Square,
    SampleAndHold
}

/// <summary>
///     A low-frequency oscillator in −1 … 1, advanced by a number of samples at a time (it runs at control rate). The
///     saw rises; sample-and-hold takes a new random level at the start of each cycle.
/// </summary>
public struct Lfo {
    const uint Seed = 0x2545F491;

    double phase;
    uint random;
    float held;

    /// <summary>Back to the start of a cycle, with the random sequence restarted.</summary>
    public void Reset() {
        phase = 0;
        random = Seed;
        held = Oscillators.PolyBlep.White(ref random);
    }

    /// <summary>The value at the current phase, then the phase advanced by <paramref name="cycles" />.</summary>
    public float Next(LfoShape shape, double cycles) {
        if (random == 0) {
            Reset();
        }

        var p = phase;
        var value = shape switch {
            LfoShape.Sine => (float)Math.Sin(2 * Math.PI * p),
            LfoShape.Triangle => (float)(p < 0.25 ? 4 * p : p < 0.75 ? 2 - 4 * p : 4 * p - 4),
            LfoShape.Saw => (float)(2 * p - 1),
            LfoShape.Square => p < 0.5 ? 1f : -1f,
            _ => held
        };
        phase += cycles;
        if (phase >= 1) {
            phase -= Math.Floor(phase);
            held = Oscillators.PolyBlep.White(ref random);
        }

        return value;
    }
}

using System.Runtime.CompilerServices;

namespace Anode.Dsp.Instruments;

/// <summary>
///     A sine by table lookup with linear interpolation (4,096 points: the error is under 3e-7), for instruments that
///     run many sine oscillators a voice: the FM synth, the organ, the electric piano and the drum synth.
/// </summary>
internal static class SineTable {
    const int Size = 4_096;
    static readonly float[] Table = Build();

    /// <summary>sin(2π·<paramref name="phase" />), the phase in cycles (any value).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sin(double phase) {
        var x = (phase - Math.Floor(phase)) * Size;
        var i = (int)x;
        var t = (float)(x - i);
        var a = Table[i];
        return a + (Table[i + 1] - a) * t;
    }

    static float[] Build() {
        var table = new float[Size + 1];
        for (var i = 0; i <= Size; i++) {
            table[i] = (float)Math.Sin(2 * Math.PI * i / Size);
        }

        return table;
    }
}

/// <summary>Pitch helpers.</summary>
internal static class Pitch {
    /// <summary>The frequency of MIDI note <paramref name="note" /> (fractional), A4 = 440 Hz.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Frequency(double note) => 440 * Math.Pow(2, (note - 69) / 12);

    /// <summary>A velocity curve: <c>1 − s + s·(v/127)²</c> for a sensitivity <c>s</c> in 0 … 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Velocity(int velocity, float sensitivity) {
        var v = velocity / 127f;
        return 1 - sensitivity + sensitivity * v * v;
    }

    /// <summary>Constant-power pan gains for <paramref name="pan" /> in −1 … 1 (both √½·√2 = 1 at the centre).</summary>
    public static (float Left, float Right) Pan(double pan) {
        var angle = (Math.Clamp(pan, -1, 1) + 1) * Math.PI / 4;
        return ((float)(Math.Sqrt(2) * Math.Cos(angle)), (float)(Math.Sqrt(2) * Math.Sin(angle)));
    }
}

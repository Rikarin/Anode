using Anode.Dsp.Metering;
using System.Runtime.CompilerServices;

namespace Anode.Dsp.Instruments.Wavetable;

/// <summary>
///     The wavetable synth's wavetables, generated in code at first use (about 3 MB, shared by every instance): five
///     banks of eight single-cycle frames each. Each frame is stored band-limited at ten levels, the first with 512
///     harmonics and each next with half as many, so an oscillator reads the level whose top harmonic stays under
///     Nyquist. Frames are normalised to a peak of 1 at the full-band level.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>Basic: sine, sine/triangle, triangle, triangle/saw, saw, saw/square, square, 25 % pulse.</item>
///         <item>Harmonic: additive, 1, 2, 4 … 128 harmonics at 1/n^0.8, the odd ones stronger in the later frames.</item>
///         <item>Formant: a 100 Hz voice's harmonics under two formants moving through the vowels a, e, i, o, u.</item>
///         <item>Sync: a saw hard-synced to the cycle at 1 … 8 times its rate.</item>
///         <item>FM: a sine phase-modulated by its second harmonic at indices 0 … 4.9.</item>
///     </list>
/// </remarks>
internal static class Wavetables {
    public const int Size = 2_048;
    public const int Frames = 8;
    public const int Levels = 10;

    /// <summary>The harmonics of level 0.</summary>
    public const int TopHarmonics = 512;

    /// <summary>The banks (their names are <see cref="WavetableParameters.TableNames" />).</summary>
    public const int Banks = 5;

    /// <summary>[bank][frame][level]: <see cref="Size" /> + 1 samples, the last a copy of the first.</summary>
    static readonly float[][][][] Tables = Build();

    /// <summary>Builds the tables now (call on a control thread, before audio processing first needs them).</summary>
    public static void Ensure() => _ = Tables.Length;

    /// <summary>The band-limited level for a phase increment (cycles per sample).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Level(double increment) {
        // The most harmonics under Nyquist, then the first level with no more than that.
        var allowed = 0.5 / Math.Max(increment, 1e-9);
        var level = 0;
        var harmonics = TopHarmonics;
        while (harmonics > allowed && level < Levels - 1) {
            harmonics >>= 1;
            level++;
        }

        return level;
    }

    /// <summary>
    ///     Bank <paramref name="bank" />'s waveform at <paramref name="position" /> (0 … 1 across its frames, linearly
    ///     between them) and <paramref name="phase" /> (0 … 1), at band-limited level <paramref name="level" />.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Read(int bank, double position, double phase, int level) {
        var frames = Tables[bank];
        var at = Math.Clamp(position, 0, 1) * (Frames - 1);
        var frame = Math.Min((int)at, Frames - 2);
        var blend = (float)(at - frame);
        var x = phase * Size;
        var i = (int)x;
        var t = (float)(x - i);
        var a = frames[frame][level];
        var b = frames[frame + 1][level];
        var fromA = a[i] + (a[i + 1] - a[i]) * t;
        var fromB = b[i] + (b[i + 1] - b[i]) * t;
        return fromA + (fromB - fromA) * blend;
    }

    static float[][][][] Build() {
        var tables = new float[Banks][][][];
        var cycle = new double[Size];
        for (var bank = 0; bank < tables.Length; bank++) {
            tables[bank] = new float[Frames][][];
            for (var frame = 0; frame < Frames; frame++) {
                for (var n = 0; n < Size; n++) {
                    cycle[n] = Shape(bank, frame, (double)n / Size);
                }

                tables[bank][frame] = BandLimit(cycle);
            }
        }

        return tables;
    }

    /// <summary>One cycle's levels from its full-band samples: the spectrum cut at each level's harmonics.</summary>
    static float[][] BandLimit(double[] cycle) {
        var real = new double[Size];
        var imaginary = new double[Size];
        cycle.CopyTo(real, 0);
        SplitFft.Forward(real, imaginary);
        var levels = new float[Levels][];
        double scale = 0;
        var re = new double[Size];
        var im = new double[Size];
        for (var level = 0; level < Levels; level++) {
            var harmonics = TopHarmonics >> level;
            Array.Clear(re);
            Array.Clear(im);
            for (var k = 1; k <= harmonics; k++) {
                // The inverse transform as a forward one on the swapped parts.
                re[k] = imaginary[k];
                im[k] = real[k];
                re[Size - k] = imaginary[Size - k];
                im[Size - k] = real[Size - k];
            }

            SplitFft.Forward(re, im);
            var table = new float[Size + 1];
            for (var n = 0; n < Size; n++) {
                table[n] = (float)(im[n] / Size);
            }

            if (level == 0) {
                double peak = 0;
                for (var n = 0; n < Size; n++) {
                    peak = Math.Max(peak, Math.Abs(table[n]));
                }

                scale = peak > 0 ? 1 / peak : 1;
            }

            for (var n = 0; n < Size; n++) {
                table[n] = (float)(table[n] * scale);
            }

            table[Size] = table[0];
            levels[level] = table;
        }

        return levels;
    }

    /// <summary>The full-band waveform of a frame at phase <paramref name="t" /> (0 … 1).</summary>
    static double Shape(int bank, int frame, double t) {
        var sine = Math.Sin(2 * Math.PI * t);
        var triangle = t < 0.25 ? 4 * t : t < 0.75 ? 2 - 4 * t : 4 * t - 4;
        var saw = 2 * ((t + 0.5) % 1) - 1;
        var square = t < 0.5 ? 1.0 : -1.0;
        switch (bank) {
            case 0:
                return frame switch {
                    0 => sine,
                    1 => 0.5 * (sine + triangle),
                    2 => triangle,
                    3 => 0.5 * (triangle + saw),
                    4 => saw,
                    5 => 0.5 * (saw + square),
                    6 => square,
                    _ => t < 0.25 ? 1.0 : -1.0
                };
            case 1: {
                var top = 1 << frame;
                double sum = 0;
                for (var n = 1; n <= top; n++) {
                    var odd = (n & 1) == 1 ? 1.0 : 1 - frame / 10.0;
                    sum += odd * Math.Sin(2 * Math.PI * n * t) / Math.Pow(n, 0.8);
                }

                return sum;
            }
            case 2: {
                // Formants (F1, F2) of a, e, i, o, u, a 100 Hz voice, morphed across the frames.
                double[] f1 = [800, 400, 300, 450, 325];
                double[] f2 = [1_150, 1_900, 2_300, 800, 700];
                var at = frame * 4.0 / (Frames - 1);
                var v = Math.Min((int)at, 3);
                var blend = at - v;
                var a = f1[v] + (f1[v + 1] - f1[v]) * blend;
                var b = f2[v] + (f2[v + 1] - f2[v]) * blend;
                double sum = 0;
                for (var n = 1; n <= 60; n++) {
                    var f = n * 100.0;
                    var gain = Math.Exp(-Math.Pow((f - a) / 120, 2))
                        + 0.6 * Math.Exp(-Math.Pow((f - b) / 160, 2))
                        + 0.02 / n;
                    sum += gain * Math.Sin(2 * Math.PI * n * t);
                }

                return sum;
            }
            case 3: {
                var ratio = 1 + frame;
                return 2 * (ratio * t % 1) - 1;
            }
            default:
                return Math.Sin(2 * Math.PI * t + frame * 0.7 * Math.Sin(4 * Math.PI * t));
        }
    }
}

namespace Anode.Model.Tests;

/// <summary>Test signals for the export processing tests.</summary>
static class Signals {
    public static double Amplitude(double dbfs) => Math.Pow(10, dbfs / 20);

    /// <summary>A sine of peak <paramref name="dbfs" /> (0 dBFS is a full-scale sine).</summary>
    public static float[] Sine(int rate, double frequency, double dbfs, double seconds, double phase = 0) =>
        Sine(rate, frequency, dbfs, (int)Math.Round(seconds * rate), phase);

    public static float[] Sine(int rate, double frequency, double dbfs, int frames, double phase = 0) {
        var amplitude = Amplitude(dbfs);
        var samples = new float[frames];
        for (var i = 0; i < frames; i++) {
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate + phase));
        }

        return samples;
    }

    /// <summary>1 kHz sine segments, one after another, each (level in dBFS, seconds).</summary>
    public static float[] Sequence(int rate, params (double Dbfs, double Seconds)[] parts) {
        var samples = new List<float>();
        var phase = 0.0;
        foreach (var (dbfs, seconds) in parts) {
            var frames = (int)Math.Round(seconds * rate);
            samples.AddRange(Sine(rate, 1000, dbfs, frames, phase));
            phase += 2 * Math.PI * 1000 * frames / rate;
        }

        return [..samples];
    }

    /// <summary>Fades the first and last <paramref name="frames" /> samples in and out (raised cosine).</summary>
    public static float[] Faded(float[] samples, int frames) {
        for (var i = 0; i < frames; i++) {
            var gain = (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / frames));
            samples[i] *= gain;
            samples[^(i + 1)] *= gain;
        }

        return samples;
    }

    public static double Db(double ratio) => 20 * Math.Log10(ratio);

    /// <summary>
    ///     The RMS of <paramref name="samples" /> between <paramref name="from" /> and <paramref name="to" />.
    /// </summary>
    public static double Rms(float[] samples, int from, int to) {
        var sum = 0.0;
        for (var i = from; i < to; i++) {
            sum += (double)samples[i] * samples[i];
        }

        return Math.Sqrt(sum / (to - from));
    }
}

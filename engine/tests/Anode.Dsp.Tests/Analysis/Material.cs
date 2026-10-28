namespace Anode.Dsp.Tests.Analysis;

/// <summary>
///     Synthesized test material for the analyses, with its ground truth: clicks, kick and hat loops (steady, humanised,
///     ramped), harmonic tones with vibrato and glides, noise, silence and a chord. Seeded, so every run is the same.
/// </summary>
static class Material {
    public const int Rate = 48_000;

    /// <summary>A drum loop and where its hits and beats are.</summary>
    /// <param name="Audio">The mono signal.</param>
    /// <param name="Onsets">Every hit (each eighth note), as played, in frames.</param>
    /// <param name="Beats">The quarter notes, as played, in frames.</param>
    /// <param name="Downbeats">The first beat of each bar, as played, in frames.</param>
    public sealed record Loop(float[] Audio, long[] Onsets, long[] Beats, long[] Downbeats);

    /// <summary>A melody and its truth.</summary>
    /// <param name="Audio">The mono signal.</param>
    /// <param name="Cents">The true pitch at every frame in MIDI cents, NaN where silent.</param>
    /// <param name="Notes">The notes: frames, and their centre pitch in cents.</param>
    public sealed record Melody(float[] Audio, double[] Cents, (long Start, long End, double Cents)[] Notes);

    /// <summary>A note to synthesize: its start and length in seconds, its pitch (cents, gliding to <c>GlideTo</c>).</summary>
    public sealed record Note(
        double Start,
        double Length,
        double Cents,
        double VibratoCents = 0,
        double VibratoHz = 5.5,
        double GlideTo = double.NaN
    );

    /// <summary>Short decaying noise bursts (3 ms) at <paramref name="at" /> frames.</summary>
    public static float[] Clicks(long length, long[] at, double amplitude = 0.8, int seed = 1) {
        var audio = new float[length];
        var random = new Random(seed);
        foreach (var start in at) {
            var count = (int)(0.003 * Rate);
            for (var i = 0; i < count && start + i < length; i++) {
                audio[start + i] += (float)(amplitude * (random.NextDouble() * 2 - 1) * Math.Exp(-i / (0.0008 * Rate)));
            }
        }

        return audio;
    }

    /// <summary>
    ///     A kick and hat loop: kicks on beats 1 and 3 and the "and" of 3, hats on every eighth (accented on beats). The
    ///     tempo goes linearly (in beats) from <paramref name="bpm" /> to <paramref name="endBpm" />; each eighth is
    ///     played <paramref name="humaniseMs" /> early or late at most (a normal spread of a third of that).
    /// </summary>
    public static Loop DrumLoop(double bpm, int bars, double endBpm = double.NaN, double humaniseMs = 0, int seed = 7) {
        if (double.IsNaN(endBpm)) {
            endBpm = bpm;
        }

        var random = new Random(seed);
        var eighths = bars * 8;
        var beats = bars * 4;
        var slope = (endBpm - bpm) / beats;

        // The time in seconds of beat position b (fractional beats).
        double Time(double b) => slope == 0 ? 60 * b / bpm : 60 / slope * Math.Log((bpm + slope * b) / bpm);

        var length = (long)Math.Ceiling((Time(beats) + 0.5) * Rate);
        var audio = new float[length];
        var onsets = new long[eighths];
        for (var e = 0; e < eighths; e++) {
            var offset = humaniseMs > 0
                ? Math.Clamp(Gaussian(random) * humaniseMs / 3, -humaniseMs, humaniseMs) / 1000
                : 0;
            var at = Math.Max(0, (long)Math.Round((Time(e / 2.0) + offset) * Rate));
            onsets[e] = at;
            var velocity = humaniseMs > 0 ? 0.9 + 0.2 * random.NextDouble() : 1;
            var slot = e % 8;
            var kick = slot switch {
                0 => 1.0,
                4 => 0.8,
                5 => 0.5,
                _ => 0
            };
            if (kick > 0) {
                Kick(audio, at, 0.7 * kick * velocity);
            }

            Hat(audio, at, (slot % 2 == 0 ? 0.25 : 0.15) * velocity, random);
        }

        var beatFrames = Enumerable.Range(0, beats).Select(b => onsets[b * 2]).ToArray();
        var downbeats = Enumerable.Range(0, bars).Select(b => onsets[b * 8]).ToArray();
        return new(audio, onsets, beatFrames, downbeats);
    }

    static void Kick(float[] audio, long at, double amplitude) {
        var phase = 0.0;
        var count = (int)(0.3 * Rate);
        for (var i = 0; i < count && at + i < audio.Length; i++) {
            var t = (double)i / Rate;
            var frequency = 50 + 100 * Math.Exp(-t / 0.03);
            audio[at + i] += (float)(amplitude * Math.Sin(phase) * Math.Exp(-t / 0.08));
            phase += 2 * Math.PI * frequency / Rate;
        }
    }

    static void Hat(float[] audio, long at, double amplitude, Random random) {
        var count = (int)(0.06 * Rate);
        var previous = 0.0;
        for (var i = 0; i < count && at + i < audio.Length; i++) {
            var white = random.NextDouble() * 2 - 1;
            audio[at + i] += (float)(amplitude * (white - previous) * Math.Exp(-i / (0.015 * Rate)));
            previous = white;
        }
    }

    /// <summary>
    ///     Harmonic tones (six partials at 1/k) with 10 ms attack and release, vibrato and glides, as
    ///     <paramref name="notes" />
    ///     say, in <paramref name="seconds" /> of audio.
    /// </summary>
    public static Melody Tones(double seconds, Note[] notes, int partials = 6, double amplitude = 0.3) {
        var length = (long)Math.Round(seconds * Rate);
        var audio = new float[length];
        var cents = new double[length];
        Array.Fill(cents, double.NaN);
        var truth = new List<(long, long, double)>();
        foreach (var note in notes) {
            var start = (long)Math.Round(note.Start * Rate);
            var count = (long)Math.Round(note.Length * Rate);
            var ramp = 0.01 * Rate;
            var phase = 0.0;
            var target = double.IsNaN(note.GlideTo) ? note.Cents : note.GlideTo;
            for (var i = 0L; i < count && start + i < length; i++) {
                var t = (double)i / Rate;
                var pitch = note.Cents
                    + (target - note.Cents) * i / count
                    + note.VibratoCents * Math.Sin(2 * Math.PI * note.VibratoHz * t);
                var frequency = 440 * Math.Pow(2, (pitch - 6_900) / 1_200);
                var envelope = Math.Min(1, Math.Min(i / ramp, (count - i) / ramp));
                double sample = 0;
                for (var k = 1; k <= partials; k++) {
                    if (k * frequency < Rate / 2.0) {
                        sample += Math.Sin(k * phase) / k;
                    }
                }

                audio[start + i] += (float)(amplitude * envelope * sample);
                cents[start + i] = pitch;
                phase += 2 * Math.PI * frequency / Rate;
                if (phase > 2 * Math.PI * 1_000) {
                    phase -= 2 * Math.PI * 1_000;
                }
            }

            truth.Add((start, start + count, 0.5 * (note.Cents + target)));
        }

        return new(audio, cents, [..truth]);
    }

    /// <summary>A melody of eight notes, some with vibrato, some a few cents off, some legato, with silences between others.</summary>
    public static Melody Phrase() =>
        Tones(
            4.6,
            [
                new(0.2, 0.4, 6_000 + 7), // C4, 7 cents sharp
                new(0.7, 0.4, 6_400 - 12, 30), // E4, vibrato
                new(1.2, 0.3, 6_700 + 3), // G4
                new(1.5, 0.3, 6_900 - 5), // A4, legato from G4
                new(2.0, 0.5, 7_200 + 9, 40, 6), // C5, wide vibrato
                new(2.7, 0.35, 5_500), // G3
                new(3.05, 0.4, 5_700, 0, 5.5, 5_900), // A3 gliding to B3, legato
                new(3.7, 0.6, 4_500 - 4, 25, 5) // A2, low with vibrato
            ]
        );

    public static float[] Noise(double seconds, double amplitude = 0.3, int seed = 3) {
        var random = new Random(seed);
        var audio = new float[(long)Math.Round(seconds * Rate)];
        for (var i = 0; i < audio.Length; i++) {
            audio[i] = (float)(amplitude * (random.NextDouble() * 2 - 1));
        }

        return audio;
    }

    /// <summary>A C major triad (C4, E4, G4) of harmonic tones.</summary>
    public static float[] Chord(double seconds) {
        var notes = new[] { 6_000.0, 6_400, 6_700 }.Select(c => new Note(0.1, seconds - 0.2, c)).ToArray();
        return Tones(seconds, notes, amplitude: 0.2).Audio;
    }

    static double Gaussian(Random random) {
        var u = 1 - random.NextDouble();
        var v = random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
    }
}

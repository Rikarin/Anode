using Anode.Dsp.IO;
using Anode.Dsp.Instruments.Alchemy;
using Anode.Dsp.Instruments.Physical;
using Anode.Dsp.Instruments.Tape;
using Anode.Dsp.Tests.Support;
using System.Buffers.Binary;
using A = Anode.Dsp.Instruments.Alchemy.SampleAlchemyParameters;
using P = Anode.Dsp.Instruments.Physical.PhysicalModelParameters;
using T = Anode.Dsp.Instruments.Tape.TapeReplayParameters;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>
///     The T3 instruments' sound (docs/12-phase-11.md, exit criterion step 6): the physical model in tune from C2 to C6
///     and decaying longer with less damping, the tape stopping at its length and in tune without wow and flutter,
///     Sample Alchemy in tune in its modes; and what each does besides.
/// </summary>
public sealed class T3InstrumentTests(ITestOutputHelper output) : IDisposable {
    const float Rate = Play.Rate;
    readonly string folder = Play.TempFolder();

    public void Dispose() => Directory.Delete(folder, true);

    /// <summary>C2 … C6.</summary>
    public static TheoryData<int> Notes => [36, 43, 48, 55, 60, 67, 72, 79, 84];

    [Theory]
    [MemberData(nameof(Notes))]
    public void Physical_model_plays_in_tune(int note) {
        foreach (var kind in Enum.GetValues<PhysicalObject>()) {
            using var instrument = T3Kit.Instrument(
                PhysicalModel.Id,
                (P.Object, (int)kind),
                (P.ExciterType(1),
                    kind == PhysicalObject.String ? (int)PhysicalExciter.Pick : (int)PhysicalExciter.Strike)
            );
            var (left, _) = Play.Render(instrument, 60_000, [TimedEvent.On(0, note)]);
            var cents = Cents(T3Kit.PartialFrequency(left, Rate, NoteFrequency(note), 4_800), NoteFrequency(note));
            output.WriteLine($"{kind} {note}: {cents:0.000} ct");
            Assert.InRange(cents, -5, 5);
        }
    }

    [Fact]
    public void Physical_model_bowed_and_blown_notes_sustain_in_tune() {
        foreach (var (kind, exciter) in new[] {
                     (PhysicalObject.String, PhysicalExciter.Bow), (PhysicalObject.Tube, PhysicalExciter.Blow)
                 }) {
            foreach (var note in new[] { 36, 60, 84 }) {
                using var instrument = T3Kit.Instrument(
                    PhysicalModel.Id,
                    (P.Object, (int)kind),
                    (P.ExciterType(1), (int)exciter)
                );
                var (left, _) = Play.Render(instrument, 72_000, [TimedEvent.On(0, note), TimedEvent.Off(60_000, note)]);
                var cents = Cents(T3Kit.PartialFrequency(left, Rate, NoteFrequency(note), 12_000), NoteFrequency(note));
                output.WriteLine($"{kind} {exciter} {note}: {cents:0.000} ct");
                Assert.InRange(cents, -5, 5);
                Assert.True(Rms(left.AsSpan(40_000, 20_000)) > 0.02, "a held bow or breath sustains");
                Assert.True(Rms(left.AsSpan(70_000)) < 0.1 * Rms(left.AsSpan(40_000, 20_000)), "and stops when let go");
            }
        }
    }

    [Fact]
    public void Physical_model_decays_longer_with_less_damping() {
        foreach (var kind in new[] { PhysicalObject.String, PhysicalObject.Bar }) {
            foreach (var note in new[] { 36, 60, 84 }) {
                double Decay(float damping) {
                    using var instrument = T3Kit.Instrument(
                        PhysicalModel.Id,
                        (P.Object, (int)kind),
                        (P.Damping, damping),
                        (P.ExciterType(1), (int)PhysicalExciter.Strike)
                    );
                    var (left, _) = Play.Render(instrument, 12 * 48_000, [TimedEvent.On(0, note)]);
                    return T3Kit.DecaySeconds(left, Rate);
                }

                var damped = Decay(60);
                var free = Decay(40);
                output.WriteLine($"{kind} {note}: −60 dB in {damped:0.000} s at 60 %, {free:0.000} s at 40 %");
                Assert.True(free >= 1.5 * damped, $"{free} s against {damped} s");
            }
        }
    }

    [Fact]
    public void Physical_model_objects_exciters_and_pickups_change_the_sound() {
        static float[] Render(params (int, float)[] parameters) {
            using var instrument = T3Kit.Instrument(PhysicalModel.Id, parameters);
            return Play.Render(instrument, 24_000, [TimedEvent.On(0, 57)]).Left;
        }

        var plain = Render();
        foreach (var other in new[] {
                     Render((P.Object, (int)PhysicalObject.Bar)), Render((P.Object, (int)PhysicalObject.Membrane)),
                     Render((P.ExciterType(1), (int)PhysicalExciter.Strike)), Render((P.ExciterType(1) + 1, 50)),
                     Render((P.Pickup1Position, 50)), Render((P.Inharmonicity, 80)), Render((P.Stiffness, 0)),
                     Render((P.Body, (int)PhysicalBody.Off))
                 }) {
            Assert.True(NullResidualDb(plain, other) > -60);
        }

        // A pick at the middle of a string leaves its even modes still: the second harmonic all but vanishes.
        var middle = Render((P.ExciterType(1) + 1, 50), (P.Body, (int)PhysicalBody.Off), (P.Pickup1Position, 30));
        var spectrum = Spectrum(middle, 0, 16_384);
        double At(double hz) => spectrum[(int)Math.Round(hz * 16_384 / Rate)].Magnitude;
        Assert.True(At(2 * 220) < 0.05 * At(220));

        // The second exciter adds to the first; the pickups' spread makes stereo.
        using var two = T3Kit.Instrument(PhysicalModel.Id, (P.ExciterType(2), (int)PhysicalExciter.Strike));
        var (twoLeft, twoRight) = Play.Render(two, 24_000, [TimedEvent.On(0, 57)]);
        Assert.True(NullResidualDb(plain, twoLeft) > -60);
        Assert.True(NullResidualDb(twoLeft, twoRight) > -40);
    }

    [Fact]
    public void Physical_model_modes_follow_the_objects() {
        Assert.Equal(1, PhysicalModel.Ratio(PhysicalObject.Bar, 1, 0.01));
        Assert.InRange(PhysicalModel.Ratio(PhysicalObject.Bar, 2, 0), 2.755, 2.758); // a free bar's second mode
        Assert.Equal(1.5933, PhysicalModel.Ratio(PhysicalObject.Membrane, 2, 0), 4);
        Assert.Equal(3, PhysicalModel.Ratio(PhysicalObject.String, 3, 0));
        Assert.True(PhysicalModel.Ratio(PhysicalObject.String, 10, 0.001) > 10.4); // stretched by inharmonicity
        Assert.True(PhysicalModel.DecaySeconds(0, 261.63) > 10 * PhysicalModel.DecaySeconds(0.5, 261.63));
    }

    /// <summary>A tape set of sines, one a key from G2, <paramref name="seconds" /> long.</summary>
    string SineTapes(int keys = 35, double seconds = 10, int rate = 32_000) {
        var recordings = new float[keys][];
        for (var k = 0; k < keys; k++) {
            var hz = NoteFrequency(43 + k);
            recordings[k] = [
                ..Enumerable.Range(0, (int)(seconds * rate))
                    .Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate)))
            ];
        }

        var path = Path.Combine(folder, "sines" + TapeSet.Extension);
        File.WriteAllBytes(path, TapeSet.From("Sines", rate, 43, recordings).ToBytes());
        return path;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(200)]
    public void A_held_tape_stops_at_its_length(float motorMs) {
        using var tape = T3Kit.Instrument(TapeReplay.Id, (T.MotorStart, motorMs));
        Assert.True(tape.LoadFile(SineTapes()));
        var (left, _) = Play.Render(tape, 9 * 48_000, [TimedEvent.On(0, 60)]);
        var last = left.Length - 1;
        while (last > 0 && left[last] == 0) {
            last--;
        }

        // The tape's 8 s, and half the motor's start time it took to come up to speed.
        var expected = TapeSet.MaxSeconds + motorMs * 0.001 / 2;
        output.WriteLine($"motor {motorMs} ms: the tape ends at {last / Rate:0.0000} s (expected {expected:0.0000} s)");
        Assert.InRange(last / Rate, expected - 0.002, expected + 0.002);
        Assert.True(Rms(left.AsSpan((int)(7.9 * Rate), 2_400)) > 0.05);
    }

    [Theory]
    [MemberData(nameof(Notes))]
    public void The_tape_plays_in_tune_without_wow_and_flutter(int note) {
        using var tape = T3Kit.Instrument(TapeReplay.Id, (T.Wow, 0), (T.Flutter, 0));
        using var builtin = T3Kit.Instrument(TapeReplay.Id, (T.Wow, 0), (T.Flutter, 0));
        Assert.True(tape.LoadFile(SineTapes()));
        foreach (var instrument in new[] { tape, builtin }) {
            var (left, _) = Play.Render(instrument, 72_000, [TimedEvent.On(0, note)]);
            var cents = Cents(T3Kit.PartialFrequency(left, Rate, NoteFrequency(note), 24_000), NoteFrequency(note));
            output.WriteLine($"{(instrument == tape ? "sines" : "default")} {note}: {cents:0.000} ct");
            Assert.InRange(cents, -5, 5);
        }
    }

    [Fact]
    public void Wow_flutter_and_speed_move_the_pitch() {
        double Pitch(params (int, float)[] parameters) {
            using var tape = T3Kit.Instrument(TapeReplay.Id, parameters);
            Assert.True(tape.LoadFile(SineTapes(35, 3)));
            var (left, _) = Play.Render(tape, 96_000, [TimedEvent.On(0, 69)]);
            return Cents(T3Kit.PartialFrequency(left, Rate, 440 * 1.1, 4_800, 65_536), 440);
        }

        Assert.InRange(
            Pitch((T.Speed, 110), (T.Wow, 0), (T.Flutter, 0)),
            1_200 * Math.Log2(1.1) - 1,
            1_200 * Math.Log2(1.1) + 1
        );

        // Wow moves the pitch slowly: the zero-crossing pitch of 100 ms windows wanders by a few cents.
        float[] Render(float wow, float flutter) {
            using var tape = T3Kit.Instrument(TapeReplay.Id, (T.Wow, wow), (T.Flutter, flutter), (T.Hiss, 0));
            Assert.True(tape.LoadFile(SineTapes(35, 3)));
            return Play.Render(tape, 96_000, [TimedEvent.On(0, 69)]).Left;
        }

        static double Spread(float[] signal) {
            var cents = Enumerable.Range(1, 18)
                .Select(w => Cents(ZeroCrossingFrequency(signal, w * 4_800, 4_800, Rate), 440))
                .ToArray();
            return cents.Max() - cents.Min();
        }

        var still = Spread(Render(0, 0));
        var wobbly = Spread(Render(100, 100));
        output.WriteLine($"pitch spread {still:0.00} ct still, {wobbly:0.00} ct with wow and flutter");
        Assert.True(still < 0.5);
        Assert.True(wobbly > 3);
    }

    [Fact]
    public void Tape_sets_round_trip_and_keys_fold_into_the_set() {
        var path = SineTapes(5, 1, 24_000);
        var set = TapeSet.Load(path);
        Assert.Equal(("Sines", 24_000, 43, 47), (set.Name, set.SampleRate, set.LowKey, set.HighKey));
        Assert.Equal(set.ToBytes(), File.ReadAllBytes(path));
        Assert.Equal(0.5 * Math.Sin(2 * Math.PI * NoteFrequency(45) * 100 / 24_000), set.Read(2, 100), 4);
        Assert.Throws<InvalidDataException>(static () => TapeSet.Read([1, 2, 3]));

        // A recording longer than the tape is cut to it.
        var longer = TapeSet.From("Long", 8_000, 60, [new float[10 * 8_000]]);
        Assert.Equal(8 * 8_000, longer.Frames(0));

        // A key above the set plays its octave below, read twice as fast: in tune, and over half the time.
        using var tape = T3Kit.Instrument(TapeReplay.Id, (T.Wow, 0), (T.Flutter, 0));
        Assert.True(tape.LoadFile(path));
        Assert.Equal(path, ((TapeReplay)tape).File);
        var (left, _) = Play.Render(tape, 48_000, [TimedEvent.On(0, 57)]);
        Assert.InRange(
            Cents(T3Kit.PartialFrequency(left, Rate, NoteFrequency(57), 4_800, 16_384), NoteFrequency(57)),
            -5,
            5
        );
        Assert.Equal(0, Peak(left.AsSpan(26_000)));

        // The state holds the file; a state without one plays the default set again.
        using var restored = T3Kit.Instrument(TapeReplay.Id);
        Assert.True(restored.LoadState(tape.SaveState()));
        Assert.Equal(path, ((TapeReplay)restored).File);
        using var fresh = T3Kit.Instrument(TapeReplay.Id);
        Assert.True(restored.LoadState(fresh.SaveState()));
        Assert.Same(TapeSet.Default, ((TapeReplay)restored).Tapes);
        Assert.False(restored.LoadFile(Path.Combine(folder, "none.antape")));
    }

    [Theory]
    [InlineData(AlchemyMode.Additive)]
    [InlineData(AlchemyMode.Granular)]
    [InlineData(AlchemyMode.Spectral)]
    public void Sample_alchemy_plays_at_the_keys_pitch(AlchemyMode mode) {
        foreach (var note in new[] { 36, 48, 60, 72, 84 }) {
            using var alchemy = T3Kit.Instrument(SampleAlchemy.Id, (A.Mode, (int)mode));
            var (left, _) = Play.Render(alchemy, 80_000, [TimedEvent.On(0, note)]);
            var cents = Cents(
                T3Kit.AveragedPeakFrequency(left, Rate, NoteFrequency(note), 12_000, 64_000),
                NoteFrequency(note)
            );
            output.WriteLine($"{mode} {note}: {cents:0.000} ct");
            Assert.InRange(cents, -5, 5);
        }
    }

    [Fact]
    public void Sample_alchemy_loads_a_sample_and_tunes_it_to_the_keys() {
        // A slightly sharp A (445 Hz) with a few harmonics, as a WAV and as an AIFF: either plays C4 at C4's pitch.
        const int rate = 44_100;
        var tone = Enumerable.Range(0, 2 * rate)
            .Select(static i => (float)(0.4 * Math.Sin(2 * Math.PI * 445 * i / rate)
                    + 0.15 * Math.Sin(4 * Math.PI * 445 * i / rate))
            )
            .ToArray();
        var wav = Path.Combine(folder, "a.wav");
        TestWav.Write(wav, rate, [tone]);
        var aiff = Path.Combine(folder, "a.aiff");
        File.WriteAllBytes(aiff, Aiff(tone, rate, 69));
        var read = AiffReader.Read(aiff);
        Assert.Equal((rate, 69), (read.SampleRate, read.RootNote));
        Assert.Equal(tone[1_000], read.Channels[0][1_000], 4);
        foreach (var path in new[] { wav, aiff }) {
            foreach (var mode in new[] { AlchemyMode.Additive, AlchemyMode.Granular }) {
                using var alchemy = (SampleAlchemy)T3Kit.Instrument(SampleAlchemy.Id, (A.Mode, (int)mode));
                Assert.True(alchemy.LoadFile(path));
                Assert.InRange(Cents(alchemy.SourcePitch, 445), -3, 3);
                var (left, _) = Play.Render(alchemy, 72_000, [TimedEvent.On(0, 60)]);
                var cents = Cents(
                    T3Kit.AveragedPeakFrequency(left, Rate, NoteFrequency(60), 12_000, 56_000),
                    NoteFrequency(60)
                );
                output.WriteLine($"{Path.GetExtension(path)} {mode}: {cents:0.000} ct");
                Assert.InRange(cents, -5, 5);
            }
        }

        using var restored = (SampleAlchemy)T3Kit.Instrument(SampleAlchemy.Id);
        using var loaded = (SampleAlchemy)T3Kit.Instrument(SampleAlchemy.Id);
        Assert.True(loaded.LoadFile(aiff));
        Assert.Equal(69, loaded.GetParameter(A.RootNote)); // the INST note
        Assert.True(restored.LoadState(loaded.SaveState()));
        Assert.Equal(aiff, restored.File);
        Assert.False(restored.LoadFile(Path.Combine(folder, "none.wav")));
    }

    [Fact]
    public void Sample_alchemy_modes_and_controls_change_the_sound() {
        static float[] Render(params (int, float)[] parameters) {
            using var instrument = T3Kit.Instrument(SampleAlchemy.Id, parameters);
            return Play.Render(instrument, 48_000, [TimedEvent.On(0, 60)]).Left;
        }

        var additive = Render();
        foreach (var other in new[] {
                     Render((A.Mode, (int)AlchemyMode.Granular)), Render((A.Mode, (int)AlchemyMode.Spectral)),
                     Render((A.Partials, 4)), Render((A.PartialSpread, 60)), Render((A.Speed, 0)),
                     Render((A.Position, 50)), Render((A.Cutoff, 1_000))
                 }) {
            Assert.True(NullResidualDb(additive, other) > -60);
        }

        // One partial is a sine at the key.
        var one = Render((A.Partials, 1));
        var spectrum = Spectrum(one, 12_000, 16_384);
        double At(double hz) => spectrum[(int)Math.Round(hz * 16_384 / Rate)].Magnitude;
        Assert.True(At(2 * NoteFrequency(60)) < 0.01 * At(NoteFrequency(60)));

        // Without a loop the note ends with the sample (2.5 s).
        using var once = T3Kit.Instrument(SampleAlchemy.Id, (A.Loop, 0));
        var (left, _) = Play.Render(once, 3 * 48_000, [TimedEvent.On(0, 60)]);
        Assert.True(Rms(left.AsSpan(48_000, 24_000)) > 0.01);
        Assert.Equal(0, Peak(left.AsSpan((int)(2.52 * Rate))));
        Assert.Equal(2.5 * 48_000, SampleAlchemy.DefaultSample(48_000).Length);
    }

    /// <summary>A mono 16-bit AIFF of <paramref name="samples" /> with an INST chunk's base note.</summary>
    static byte[] Aiff(float[] samples, int rate, int baseNote) {
        using var stream = new MemoryStream();
        var b = new byte[10];

        void Chunk(string id, byte[] body) {
            stream.Write(System.Text.Encoding.ASCII.GetBytes(id));
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)body.Length);
            stream.Write(b, 0, 4);
            stream.Write(body);
            if (body.Length % 2 == 1) {
                stream.WriteByte(0);
            }
        }

        var comm = new byte[18];
        BinaryPrimitives.WriteInt16BigEndian(comm, 1);
        BinaryPrimitives.WriteUInt32BigEndian(comm.AsSpan(2), (uint)samples.Length);
        BinaryPrimitives.WriteInt16BigEndian(comm.AsSpan(6), 16);
        // The rate as an 80-bit extended number: exponent and the mantissa with its leading one.
        var exponent = (int)Math.Floor(Math.Log2(rate));
        BinaryPrimitives.WriteUInt16BigEndian(comm.AsSpan(8), (ushort)(16_383 + exponent));
        BinaryPrimitives.WriteUInt64BigEndian(comm.AsSpan(10), (ulong)rate << (63 - exponent));
        var ssnd = new byte[8 + 2 * samples.Length];
        for (var i = 0; i < samples.Length; i++) {
            BinaryPrimitives.WriteInt16BigEndian(ssnd.AsSpan(8 + 2 * i), (short)Math.Round(samples[i] * 32_767));
        }

        var inst = new byte[20];
        inst[0] = (byte)baseNote;
        stream.Write("FORM"u8);
        stream.Write(new byte[4]);
        stream.Write("AIFF"u8);
        Chunk("COMM", comm);
        Chunk("INST", inst);
        Chunk("SSND", ssnd);
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }
}

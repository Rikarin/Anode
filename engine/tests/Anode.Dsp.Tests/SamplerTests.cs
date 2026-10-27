using Anode.Dsp.Instruments.Sampler;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

public sealed class SamplerTests : IDisposable {
    const float Rate = Play.Rate;
    readonly string folder = Play.TempFolder();

    public void Dispose() => Directory.Delete(folder, true);

    string Wav(
        string name,
        float[] samples,
        int rate = 44_100,
        int root = -1,
        double cents = 0,
        long loopStart = -1,
        long loopEnd = -1
    ) {
        var path = Path.Combine(folder, name);
        TestWav.Write(path, rate, [samples], rootNote: root, rootCents: cents, loopStart: loopStart, loopEnd: loopEnd);
        return path;
    }

    string Sfz(string text, string name = "test.sfz") {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    static AnodeSampler Load(string path, params (int, float)[] parameters) {
        var sampler = (AnodeSampler)Play.Create(AnodeSampler.Id, parameters);
        Assert.True(sampler.LoadFile(path), path);
        return sampler;
    }

    [Theory]
    [InlineData(57)]
    [InlineData(60)]
    [InlineData(69)]
    [InlineData(76)]
    [InlineData(81)]
    public void A_single_wav_maps_chromatically_around_its_smpl_root_within_a_cent(int note) {
        // 440 Hz at 44.1 kHz, root A4 from the smpl chunk, played at 48 kHz.
        using var sampler = Load(Wav("a440.wav", TestWav.Sine(440, 44_100, 4), root: 69));
        var (left, _) = Play.Note(sampler, note, 45_000, 45_000);
        var cents = Cents(PeakFrequency(left, Rate, 2_000), NoteFrequency(note));
        Assert.True(Math.Abs(cents) < 1, $"note {note}: {cents:0.000} cents");
    }

    [Fact]
    public void A_wav_without_a_smpl_chunk_has_its_root_on_middle_c() {
        using var sampler = Load(Wav("c.wav", TestWav.Sine(NoteFrequency(60), 48_000, 2), 48_000));
        var (left, _) = Play.Note(sampler, 72, 45_000, 45_000);
        Assert.True(Math.Abs(Cents(PeakFrequency(left, Rate, 2_000), NoteFrequency(72))) < 1);
    }

    [Fact]
    public void The_smpl_fine_tune_tune_parameter_and_pitch_bend_shift_the_pitch() {
        // The sample is 30 cents sharp of A4 and says so: it plays in tune.
        var sharp = Wav("sharp.wav", TestWav.Sine(NoteFrequency(69.3), 48_000, 2), 48_000, 69, 30);
        using (var sampler = Load(sharp)) {
            var (left, _) = Play.Note(sampler, 69, 45_000, 45_000);
            Assert.True(Math.Abs(Cents(PeakFrequency(left, Rate, 2_000), 440)) < 1);
        }

        using (var sampler = Load(sharp, (AnodeSampler.Tune, -1.5f))) {
            var (left, _) = Play.Render(sampler, 45_000, [TimedEvent.Bend(0, 8191), TimedEvent.On(0, 69)]);
            Assert.True(Math.Abs(Cents(PeakFrequency(left, Rate, 2_000), NoteFrequency(69.5))) < 1);
        }
    }

    [Fact]
    public void Sfz_tune_transpose_and_key_center_set_the_pitch() {
        Wav("a.wav", TestWav.Sine(440, 48_000, 2), 48_000);
        using var sampler = Load(
            Sfz("<region> sample=a.wav pitch_keycenter=a4 transpose=12 tune=-50 lokey=0 hikey=127")
        );
        var (left, _) = Play.Note(sampler, 60, 45_000, 45_000);
        Assert.True(Math.Abs(Cents(PeakFrequency(left, Rate, 2_000), NoteFrequency(71.5))) < 1);
    }

    [Fact]
    public void Velocity_layers_pick_their_regions() {
        Wav("soft.wav", TestWav.Constant(0.25f, 48_000), 48_000);
        Wav("loud.wav", TestWav.Constant(0.5f, 48_000), 48_000);
        using var sampler = Load(
            Sfz(
                """
                <group> lokey=c4 hikey=c5 amp_veltrack=0
                <region> sample=soft.wav hivel=63
                <region> sample=loud.wav lovel=64
                """
            )
        );
        Play.Render(sampler, 64, [TimedEvent.On(0, 60, 40)]);
        Assert.EndsWith("soft.wav", Assert.Single(sampler.SoundingRegions).Sample, StringComparison.Ordinal);
        Play.Render(sampler, 64, [TimedEvent.Off(0, 60), TimedEvent.On(0, 64)]);
        Assert.Contains(sampler.SoundingRegions, static r => r.Sample.EndsWith("loud.wav", StringComparison.Ordinal));
        Play.Render(sampler, 64, [TimedEvent.On(0, 80)]); // outside every key range
        Assert.False(sampler.IsSounding(80));
    }

    [Fact]
    public void Round_robin_cycles_through_its_regions() {
        Wav("rr1.wav", TestWav.Constant(0.1f, 4_800), 48_000);
        Wav("rr2.wav", TestWav.Constant(0.2f, 4_800), 48_000);
        Wav("rr3.wav", TestWav.Constant(0.3f, 4_800), 48_000);
        using var sampler = Load(
            Sfz(
                """
                <group> key=60 seq_length=3 amp_veltrack=0
                <region> sample=rr1.wav seq_position=1
                <region> sample=rr2.wav seq_position=2
                <region> sample=rr3.wav seq_position=3
                """
            )
        );
        var played = new List<string>();
        for (var i = 0; i < 7; i++) {
            Play.Render(sampler, 64, [TimedEvent.On(0, 60)]);
            played.Add(Path.GetFileName(Assert.Single(sampler.SoundingRegions).Sample));
            Play.Render(sampler, 4_800, [TimedEvent.Off(0, 60)]);
        }

        Assert.Equal(["rr1.wav", "rr2.wav", "rr3.wav", "rr1.wav", "rr2.wav", "rr3.wav", "rr1.wav"], played);
    }

    [Fact]
    public void Loop_modes_play_as_sfz_says() {
        // 0.1 s of a constant level with a loop over its middle.
        Wav("tone.wav", TestWav.Constant(0.5f, 4_800), 48_000, loopStart: 1_000, loopEnd: 2_999);
        using var sampler = Load(
            Sfz(
                """
                <group> sample=tone.wav amp_veltrack=0 ampeg_release=0.01
                <region> key=60 loop_mode=no_loop
                <region> key=61 loop_mode=one_shot
                <region> key=62 loop_mode=loop_continuous
                <region> key=63 loop_mode=loop_sustain
                <region> key=64
                """
            )
        );

        // no_loop: ends with the sample even while the key is down.
        var (noLoop, _) = Play.Render(sampler, 9_600, [TimedEvent.On(0, 60)]);
        Assert.True(Rms(noLoop.AsSpan(4_000, 700)) > 0.4);
        Assert.Equal(0, Peak(noLoop.AsSpan(4_801)));
        Play.Render(sampler, 64, [TimedEvent.Off(0, 60)]);

        // one_shot: plays to the end after the key goes up at once.
        var (oneShot, _) = Play.Render(sampler, 9_600, [TimedEvent.On(0, 61), TimedEvent.Off(10, 61)]);
        Assert.True(Rms(oneShot.AsSpan(4_000, 700)) > 0.4);
        Assert.False(sampler.IsSounding(61));

        // loop_continuous: still sounding long after the sample's length, and after the key goes up it releases.
        var (continuous, _) = Play.Render(sampler, 48_000, [TimedEvent.On(0, 62)]);
        Assert.True(Rms(continuous.AsSpan(40_000)) > 0.4);
        Play.Render(sampler, 4_800, [TimedEvent.Off(0, 62)]);
        Assert.False(sampler.IsSounding(62));

        // loop_sustain: loops while held, then plays on to its end.
        var (sustained, _) = Play.Render(sampler, 24_000, [TimedEvent.On(0, 63)]);
        Assert.True(Rms(sustained.AsSpan(20_000)) > 0.4);
        var (after, _) = Play.Render(sampler, 4_800, [TimedEvent.Off(0, 63)]);
        Assert.False(sampler.IsSounding(63));
        Assert.True(Peak(after.AsSpan(0, 50)) > 0.3);

        // No loop_mode and a loop in the smpl chunk: it loops.
        var (inherited, _) = Play.Render(sampler, 24_000, [TimedEvent.On(0, 64)]);
        Assert.True(Rms(inherited.AsSpan(20_000)) > 0.4);
    }

    [Fact]
    public void A_loop_plays_seamlessly() {
        // Exactly 100 cycles of 480 Hz in the loop: looping it must not glitch.
        var samples = TestWav.Sine(480, 48_000, 0.5);
        Wav("cycles.wav", samples, 48_000, loopStart: 2_000, loopEnd: 2_000 + 10_000 - 1);
        using var sampler = Load(
            Sfz("<region> sample=cycles.wav key=69 pitch_keycenter=69 loop_mode=loop_continuous amp_veltrack=0")
        );
        var (left, _) = Play.Note(sampler, 69, 48_000, 48_000);
        var largestStep = 0f;
        for (var i = 1; i < left.Length; i++) {
            largestStep = Math.Max(largestStep, Math.Abs(left[i] - left[i - 1]));
        }

        Assert.True(largestStep < 0.5f * 2 * MathF.PI * 480 / 48_000 * 1.05f, $"step {largestStep}");
    }

    [Fact]
    public void Velocity_volume_and_pan_set_the_level() {
        Wav("dc.wav", TestWav.Constant(0.5f, 48_000), 48_000);
        using var sampler = Load(Sfz("<region> sample=dc.wav volume=-6 pan=100"));
        var (left, right) = Play.Note(sampler, 60, 4_800, 4_800, 127);
        Assert.InRange(right[2_400], 0.5f * 0.501f * 1.41f, 0.5f * 0.501f * 1.42f);
        Assert.InRange(left[2_400], -1e-6f, 1e-6f);

        var soft = Play.Note(sampler, 60, 4_800, 4_800, 64).Right[2_400] / right[2_400];
        Assert.InRange(soft, 0.25f, 0.26f);
        sampler.SetParameter(AnodeSampler.VelocitySensitivity, 0);
        Assert.InRange(Play.Note(sampler, 60, 4_800, 4_800, 64).Right[2_400] / right[2_400], 0.999f, 1.001f);
    }

    [Fact]
    public void Envelope_and_the_attack_and_release_parameters() {
        Wav("dc.wav", TestWav.Constant(0.5f, 96_000), 48_000);
        using var sampler = Load(
            Sfz(
                "<region> sample=dc.wav ampeg_attack=0.05 ampeg_hold=0.05 ampeg_decay=0.1 ampeg_sustain=50 amp_veltrack=0"
            )
        );
        var (left, _) = Play.Note(sampler, 60, 48_000, 24_000);
        Assert.InRange(left[1_200], 0.24f, 0.26f); // half way up the 50 ms attack
        Assert.InRange(left[2_400 + 2_000], 0.49f, 0.5f); // holding
        Assert.InRange(left[4_800 + 4_800], 0.25f, 0.2504f); // decay is a T60: 60 dB of the way to the sustain
        Assert.InRange(ToDb(left[24_000 + 48] / left[24_000 - 1]), -61.5, -58.5); // the SFZ default release: a 1 ms T60
        Assert.Equal(0, Peak(left.AsSpan(24_000 + 240)));

        sampler.SetParameter(AnodeSampler.Attack, 50);
        sampler.SetParameter(AnodeSampler.Release, 100);
        var (slow, _) = Play.Note(sampler, 60, 48_000, 24_000);
        Assert.InRange(slow[2_400], 0.24f, 0.26f); // 100 ms attack now
        Assert.InRange(ToDb(slow[24_000 + 4_848] / slow[24_000 - 1]), -61, -59); // 101 ms release
    }

    [Fact]
    public void Thirty_two_voices_then_stealing() {
        Wav("long.wav", TestWav.Constant(0.01f, 96_000), 48_000);
        using var sampler = Load(Sfz("<region> sample=long.wav ampeg_release=5"));
        Play.Render(sampler, 64, Enumerable.Range(30, 32).Select(static n => TimedEvent.On(0, n)).ToList());
        Assert.All(Enumerable.Range(30, 32), n => Assert.True(sampler.IsSounding(n)));
        Play.Render(sampler, 64, [TimedEvent.Off(0, 40), TimedEvent.On(10, 90)]);
        Assert.False(sampler.IsSounding(40));
        Play.Render(sampler, 64, [TimedEvent.On(0, 91)]);
        Assert.False(sampler.IsSounding(30));
        Assert.True(sampler.IsSounding(91) && sampler.IsSounding(31));
    }

    [Fact]
    public void Sustain_pedal_holds_notes() {
        Wav("long.wav", TestWav.Constant(0.5f, 96_000), 48_000);
        using var sampler = Load(Sfz("<region> sample=long.wav"));
        Play.Render(sampler, 4_800, [TimedEvent.Cc(0, 64, 100), TimedEvent.On(0, 60), TimedEvent.Off(100, 60)]);
        Assert.True(sampler.IsSounding(60));
        Play.Render(sampler, 4_800, [TimedEvent.Cc(0, 64, 0)]);
        Assert.False(sampler.IsSounding(60));
    }

    [Fact]
    public void Offset_starts_into_the_sample() {
        var ramp = Enumerable.Range(0, 48_000).Select(static i => i / 48_000f).ToArray();
        Wav("ramp.wav", ramp, 48_000);
        using var sampler = Load(Sfz("<region> sample=ramp.wav offset=24000 amp_veltrack=0 pitch_keycenter=60"));
        var (left, _) = Play.Note(sampler, 60, 100, 100);
        Assert.InRange(left[10], 0.5f, 0.5005f);
    }

    [Fact]
    public void Stereo_and_float_wavs_load() {
        var path = Path.Combine(folder, "stereo.wav");
        TestWav.Write(path, 48_000, [TestWav.Constant(0.25f, 4_800), TestWav.Constant(-0.5f, 4_800)], true);
        using var sampler = Load(path, (AnodeSampler.VelocitySensitivity, 0));
        var (left, right) = Play.Note(sampler, 60, 1_000, 1_000);
        Assert.Equal(0.25f, left[500], 4);
        Assert.Equal(-0.5f, right[500], 4);
    }

    [Fact]
    public void Missing_files_fail_to_load_and_keep_the_last_program() {
        Wav("ok.wav", TestWav.Constant(0.5f, 4_800), 48_000);
        using var sampler = Load(Path.Combine(folder, "ok.wav"));
        Assert.False(sampler.LoadFile(Path.Combine(folder, "missing.wav")));
        Assert.False(sampler.LoadFile(Sfz("<region> sample=missing.wav", "broken.sfz")));
        File.WriteAllText(Path.Combine(folder, "junk.wav"), "not a wav");
        Assert.False(sampler.LoadFile(Path.Combine(folder, "junk.wav")));
        Assert.EndsWith("ok.wav", sampler.File, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_sampler_is_silent() {
        using var sampler = Play.Create(AnodeSampler.Id);
        var (left, right) = Play.Note(sampler, 60, 1_000, 500);
        Assert.Equal(0, Peak(left) + Peak(right));
    }
}

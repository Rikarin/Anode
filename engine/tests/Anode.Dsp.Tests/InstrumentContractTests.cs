using Anode.Dsp.Instruments;
using Anode.Dsp.Instruments.Drums;
using Anode.Dsp.Instruments.Fm;
using Anode.Dsp.Instruments.Organ;
using Anode.Dsp.Instruments.Piano;
using Anode.Dsp.Instruments.Quick;
using Anode.Dsp.Instruments.Sampler;
using Anode.Dsp.Instruments.Synth;
using Anode.Dsp.Instruments.Wavetable;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>What the <see cref="IInstrument" /> contract asks of every built-in instrument (T1 and T2).</summary>
public sealed class InstrumentContractTests : IDisposable {
    readonly string folder = Play.TempFolder();

    public void Dispose() => Directory.Delete(folder, true);

    public static TheoryData<string> Ids => [
        AnodeSynth.Id, AnodeSampler.Id, DrumSynth.Id, FmSynth.Id, DrawbarOrgan.Id, ElectricPiano.Id, WavetableSynth.Id,
        QuickSampler.Id
    ];

    /// <summary>
    ///     A prepared instrument; the sampler with a two-layer SFZ of plucks loaded, the Quick Sampler with a stereo
    ///     pluck.
    /// </summary>
    IInstrument Create(string id) {
        var instrument = Play.Create(id);
        if (id == QuickSampler.Id) {
            var wav = Path.Combine(folder, "pluck.wav");
            TestWav.Write(wav, 44_100, [TestWav.Pluck(261.63, 44_100, 2, 4), TestWav.Pluck(261.63, 44_100, 2, 5)]);
            Assert.True(instrument.LoadFile(wav));
        }

        if (id == AnodeSampler.Id) {
            TestWav.Write(Path.Combine(folder, "low.wav"), 44_100, [TestWav.Pluck(130.81, 44_100, 1, 1)], rootNote: 48);
            TestWav.Write(
                Path.Combine(folder, "high.wav"),
                44_100,
                [TestWav.Pluck(523.25, 44_100, 0.5, 2), TestWav.Pluck(523.25, 44_100, 0.5, 3)]
            );
            var sfz = Path.Combine(folder, "plucks.sfz");
            File.WriteAllText(
                sfz,
                """
                <global> ampeg_release=0.2
                <region> sample=low.wav lokey=0 hikey=65 pitch_keycenter=c3
                <region> sample=high.wav lokey=66 hikey=127 pitch_keycenter=c5 loop_mode=loop_continuous loop_start=1000 loop_end=20000
                """
            );
            Assert.True(instrument.LoadFile(sfz));
        }

        return instrument;
    }

    static List<TimedEvent> Phrase() => [
        TimedEvent.On(0, 48, 110), TimedEvent.On(37, 55, 90), TimedEvent.On(100, 64, 70), TimedEvent.On(101, 72, 127),
        TimedEvent.Cc(500, 1, 64), TimedEvent.Bend(700, 3000), TimedEvent.Off(2_000, 48), TimedEvent.Cc(2_100, 64, 127),
        TimedEvent.Off(2_200, 55), TimedEvent.On(3_000, 79), TimedEvent.Cc(4_000, 64, 0),
        TimedEvent.Off(5_000, 64),
        TimedEvent.Off(5_000, 72), TimedEvent.Off(6_000, 79)
    ];

    [Fact]
    public void Descriptors_are_consistent_and_creatable() {
        Assert.Contains(BuiltinInstruments.All, static d => d.Id == BuiltinInstruments.DefaultId);
        Assert.Null(BuiltinInstruments.Create("nope"));
        Assert.Equal(BuiltinInstruments.All.Count, BuiltinInstruments.All.Select(static d => d.Id).Distinct().Count());
        foreach (var descriptor in BuiltinInstruments.All) {
            using var instrument = BuiltinInstruments.Create(descriptor.Id);
            Assert.NotNull(instrument);
            Assert.Same(descriptor, instrument.Descriptor);
            Assert.Equal(
                descriptor.Parameters.Count,
                descriptor.Parameters.Select(static p => p.Id).Distinct().Count()
            );
            for (var i = 0; i < descriptor.Parameters.Count; i++) {
                var p = descriptor.Parameters[i];
                Assert.Equal(i, p.Index);
                Assert.InRange(p.Default, p.Min, p.Max);
                Assert.True(p.Max > p.Min, p.Id);
                Assert.Equal(p.Default, instrument.GetParameter(i));
                if (p.ValueNames is not null) {
                    Assert.Equal(p.Steps + 1, p.ValueNames.Count);
                }

                if (p.Steps > 0) {
                    var step = (p.Max - p.Min) / p.Steps;
                    Assert.Equal(0, (p.Default - p.Min) / step % 1, 4);
                }
            }
        }

        Assert.Equal([".sfz", ".wav"], AnodeSampler.Info.FileTypes);
        Assert.Empty(AnodeSynth.Info.FileTypes);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Parameters_are_clamped_and_snapped(string id) {
        using var instrument = Create(id);
        var parameters = instrument.Descriptor.Parameters;
        for (var i = 0; i < parameters.Count; i++) {
            var p = parameters[i];
            instrument.SetParameter(i, p.Max + 1_000);
            Assert.Equal(p.Max, instrument.GetParameter(i));
            instrument.SetParameter(i, p.Min - 1_000);
            Assert.Equal(p.Min, instrument.GetParameter(i));
            instrument.SetParameter(i, float.NaN);
            Assert.Equal(p.Min, instrument.GetParameter(i));
        }

        instrument.SetParameter(-1, 1);
        instrument.SetParameter(parameters.Count, 1);
        Assert.Equal(0, instrument.GetParameter(parameters.Count));

        var unison = id == AnodeSynth.Id ? SynthParameters.Unison : -1;
        if (unison >= 0) {
            instrument.SetParameter(unison, 2.4f);
            Assert.Equal(2, instrument.GetParameter(unison));
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Processing_allocates_nothing(string id) {
        using var instrument = Create(id);
        if (id == AnodeSynth.Id) {
            instrument.SetParameter(SynthParameters.Unison, 4);
            instrument.SetParameter(SynthParameters.LfoFilter, 50);
        }

        var events = new MidiEvent[] {
            new(0, 0x90, 60, 100), new(3, 0x90, 64, 90), new(10, 0xB0, 1, 100), new(20, 0xE0, 0, 80),
            new(30, 0xB0, 64, 127), new(40, 0x80, 60, 0), new(50, 0xB0, 64, 0), new(63, 0x90, 67, 80)
        };
        var offs = new MidiEvent[] { new(0, 0x80, 64, 0), new(1, 0x80, 67, 0), new(2, 0xB0, 123, 0) };
        var many = Enumerable.Range(0, 40).Select(static n => new MidiEvent(n, 0x90, (byte)(30 + n), 100)).ToArray();
        var left = new float[64];
        var right = new float[64];
        var allocated = AllocationProbe.Measure(() => {
                for (var block = 0; block < 200; block++) {
                    instrument.SetParameter(0, block % 3);
                    instrument.SetParameter(1, block % 5);
                    var e = (block % 50) switch {
                        0 => events,
                        25 => offs,
                        30 => many,
                        _ => []
                    };
                    instrument.Process(e, left, right);
                }

                instrument.Reset();
                _ = instrument.GetParameter(2);
            }
        );
        Assert.Equal(0, allocated);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Process_overwrites_its_buffers(string id) {
        using var instrument = Create(id);
        var left = Enumerable.Repeat(1f, 64).ToArray();
        var right = Enumerable.Repeat(-1f, 64).ToArray();
        instrument.Process([], left, right);
        Assert.Equal(0, Peak(left) + Peak(right));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Reset_silences_at_once(string id) {
        using var instrument = Create(id);
        var (playing, _) = Play.Render(instrument, 1_024, [TimedEvent.On(0, 60), TimedEvent.On(0, 70)]);
        Assert.True(Peak(playing.AsSpan(512)) > 0.01);
        instrument.Reset();
        var (after, afterRight) = Play.Render(instrument, 1_024, []);
        Assert.Equal(0, Peak(after) + Peak(afterRight));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Events_play_on_their_frame(string id) {
        // A note at frame 100 of a buffer sounds exactly as the same note at frame 0, 100 frames later.
        using var late = Create(id);
        using var early = Create(id);
        var (shifted, shiftedRight) = Play.Render(
            late,
            8_192,
            [TimedEvent.On(100, 60), TimedEvent.Off(3_000, 60)],
            512
        );
        var (plain, plainRight) = Play.Render(early, 8_092, [TimedEvent.On(0, 60), TimedEvent.Off(2_900, 60)], 512);
        Assert.Equal(0, Peak(shifted.AsSpan(0, 100)));
        Assert.True(Peak(shifted.AsSpan(100, 4)) > 0 || (id == DrawbarOrgan.Id && Peak(shifted.AsSpan(100, 200)) > 0)); // the rotary delays by 2 ms
        Assert.Equal(-300, NullResidualDb(shifted.AsSpan(100), plain));
        Assert.Equal(-300, NullResidualDb(shiftedRight.AsSpan(100), plainRight));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Output_does_not_depend_on_the_buffer_size(string id) {
        using var a = Create(id);
        using var b = Create(id);
        using var c = Create(id);
        var (one, _) = Play.Render(a, 12_000, Phrase());
        var (two, _) = Play.Render(b, 12_000, Phrase(), 1_000);
        var (three, threeRight) = Play.Render(c, 12_000, Phrase(), blocks: [1, 17, 128, 3, 512, 64]);
        Assert.True(Rms(one) > 0.01);
        Assert.Equal(-300, NullResidualDb(one, two));
        Assert.Equal(-300, NullResidualDb(one, three));
        Assert.True(Rms(threeRight) > 0.01);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void State_round_trips(string id) {
        using var original = Create(id);
        var parameters = original.Descriptor.Parameters;
        for (var i = 0; i < parameters.Count; i++) {
            var p = parameters[i];
            // A quarter of the way from the default to somewhere in the range: different, still a playable patch.
            var elsewhere = p.Min + (p.Max - p.Min) * (0.3f + 0.1f * (i % 4));
            original.SetParameter(i, p.Default + (elsewhere - p.Default) * 0.25f);
        }

        var state = original.SaveState();
        Assert.NotNull(state);
        using var restored = BuiltinInstruments.Create(id)!;
        restored.Prepare(Play.Rate, 1_024);
        Assert.True(restored.LoadState(state));
        for (var i = 0; i < parameters.Count; i++) {
            Assert.Equal(original.GetParameter(i), restored.GetParameter(i));
        }

        Assert.Equal(state, restored.SaveState());
        original.Reset();
        var (a, _) = Play.Render(original, 9_600, Phrase());
        var (b, _) = Play.Render(restored, 9_600, Phrase());
        Assert.True(Rms(a) > 1e-4);
        Assert.Equal(-300, NullResidualDb(a, b));

        Assert.False(restored.LoadState([1, 2, 3]));
        Assert.False(restored.LoadState([]));
        using var other = BuiltinInstruments.Create(id == AnodeSynth.Id ? AnodeSampler.Id : AnodeSynth.Id)!;
        Assert.False(restored.LoadState(other.SaveState()));
        Assert.Equal(state, restored.SaveState()); // a rejected state changes nothing
    }

    [Fact]
    public void Sampler_state_holds_its_file() {
        using var sampler = (AnodeSampler)Create(AnodeSampler.Id);
        using var restored = (AnodeSampler)Play.Create(AnodeSampler.Id);
        Assert.True(restored.LoadState(sampler.SaveState()));
        Assert.Equal(sampler.File, restored.File);

        // A state whose file is gone still restores the parameters, and says it could not load.
        sampler.SetParameter(AnodeSampler.Volume, -12);
        var state = sampler.SaveState()!;
        using var copy = (AnodeSampler)Play.Create(AnodeSampler.Id);
        Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        Assert.False(copy.LoadState(state));
        Assert.Equal(-12, copy.GetParameter(AnodeSampler.Volume));

        // An empty sampler's state has no file.
        using var empty = (AnodeSampler)Play.Create(AnodeSampler.Id);
        Assert.True(sampler.LoadState(empty.SaveState()));
        Assert.Null(sampler.File);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Unprepared_instruments_render_silence(string id) {
        using var instrument = BuiltinInstruments.Create(id)!;
        var left = new float[64];
        var right = new float[64];
        left[3] = 1;
        instrument.Process([new(0, 0x90, 60, 100)], left, right);
        Assert.Equal(0, Peak(left));
    }
}

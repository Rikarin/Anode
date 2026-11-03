using Anode.Dsp.Effects;
using Anode.Dsp.Effects.Drive;
using Anode.Dsp.Effects.Surround;
using Anode.Dsp.Instruments;
using Anode.Dsp.Instruments.Alchemy;
using Anode.Dsp.Instruments.Physical;
using Anode.Dsp.Instruments.Synth;
using Anode.Dsp.Instruments.Tape;
using Anode.Dsp.Mastering;
using Anode.Dsp.Spatial;
using Anode.Dsp.Tests.Spatial;
using Anode.Dsp.Tests.Support;
using static Anode.Dsp.Tests.Support.Analysis;

namespace Anode.Dsp.Tests;

/// <summary>
///     What <see cref="InstrumentContractTests" /> and <see cref="EffectContractTests" /> ask of the registered
///     built-ins, asked of the T3 instruments and effects before they are registered (docs/12-phase-11.md: the engine
///     registers them at integration), and the same of the format-changing inserts (<see cref="IFormatEffect" />).
/// </summary>
public sealed class T3ContractTests {
    public static TheoryData<string> InstrumentIds => [PhysicalModel.Id, TapeReplay.Id, SampleAlchemy.Id];

    public static TheoryData<string> EffectIds => [Saturation.Id, MasteringAssistant.Id, Chained];

    /// <summary>The Mastering Assistant with a chain (the others run it as it is before an analysis: the limiter alone).</summary>
    const string Chained = MasteringAssistant.Id + "+chain";

    static readonly Lazy<MasteringChainSettings> Designed = new(static () => {
            var (left, right) = Fx.Music();
            return MasteringChainDesigner.Design(
                MasteringAnalyzer.Analyse(left, right, Fx.Rate),
                MasteringCharacterKind.Valve
            );
        }
    );

    static IInstrument Instrument(string id) => T3Kit.Instrument(id);

    static IAudioEffect Effect(string id) {
        var effect = T3Kit.Effect(id == Chained ? MasteringAssistant.Id : id);
        Chain(effect, id);
        return effect;
    }

    static void Chain(IAudioEffect effect, string id) {
        if (id == Chained) {
            ((MasteringAssistant)effect).SetChain(Designed.Value);
        }
    }

    static List<TimedEvent> Phrase() => [
        TimedEvent.On(0, 48, 110), TimedEvent.On(37, 55, 90), TimedEvent.On(100, 64, 70), TimedEvent.On(101, 72, 127),
        TimedEvent.Cc(500, 1, 64), TimedEvent.Bend(700, 3000), TimedEvent.Off(2_000, 48), TimedEvent.Cc(2_100, 64, 127),
        TimedEvent.Off(2_200, 55), TimedEvent.On(3_000, 79), TimedEvent.Cc(4_000, 64, 0),
        TimedEvent.Off(5_000, 64),
        TimedEvent.Off(5_000, 72), TimedEvent.Off(6_000, 79)
    ];

    [Fact]
    public void Instrument_descriptors_are_consistent() {
        foreach (var descriptor in T3Kit.Instruments) {
            using var instrument = T3Kit.CreateInstrument(descriptor.Id)!;
            Assert.Same(descriptor, instrument.Descriptor);
            Assert.StartsWith("anode.", descriptor.Id, StringComparison.Ordinal);
            Assert.Equal(
                descriptor.Parameters.Count,
                descriptor.Parameters.Select(static p => p.Id).Distinct().Count()
            );
            Assert.True(descriptor.Parameters.Count(static p => p.Macro is not null) >= 4, descriptor.Id);
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

        Assert.Empty(PhysicalModel.Info.FileTypes);
        Assert.Equal([".antape"], TapeReplay.Info.FileTypes);
        Assert.Equal([".wav", ".aif", ".aiff"], SampleAlchemy.Info.FileTypes);
    }

    [Theory]
    [MemberData(nameof(InstrumentIds))]
    public void Instrument_parameters_are_clamped_and_snapped(string id) {
        using var instrument = Instrument(id);
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
    }

    [Theory]
    [MemberData(nameof(InstrumentIds))]
    public void Instrument_processing_allocates_nothing(string id) {
        using var instrument = Instrument(id);
        var events = new MidiEvent[] {
            new(0, 0x90, 60, 100), new(3, 0x90, 64, 90), new(10, 0xB0, 1, 100), new(20, 0xE0, 0, 80),
            new(30, 0xB0, 64, 127), new(40, 0x80, 60, 0), new(50, 0xB0, 64, 0), new(63, 0x90, 67, 80)
        };
        var offs = new MidiEvent[] { new(0, 0x80, 64, 0), new(1, 0x80, 67, 0), new(2, 0xB0, 123, 0) };
        var many = Enumerable.Range(0, 40).Select(static n => new MidiEvent(n, 0x90, (byte)(30 + n), 100)).ToArray();
        var left = new float[64];
        var right = new float[64];
        var parameters = instrument.Descriptor.Parameters;
        var allocated = AllocationProbe.Measure(() => {
                for (var block = 0; block < 300; block++) {
                    // Every parameter moves across its range while notes play (modes, objects, exciters included).
                    var p = parameters[block % parameters.Count];
                    instrument.SetParameter(p.Index, p.Min + (p.Max - p.Min) * (block % 7) / 6f);
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
    [MemberData(nameof(InstrumentIds))]
    public void Instrument_process_overwrites_its_buffers(string id) {
        using var instrument = Instrument(id);
        var left = Enumerable.Repeat(1f, 64).ToArray();
        var right = Enumerable.Repeat(-1f, 64).ToArray();
        instrument.Process([], left, right);
        Assert.Equal(0, Peak(left) + Peak(right));
    }

    [Theory]
    [MemberData(nameof(InstrumentIds))]
    public void Instrument_reset_silences_at_once(string id) {
        using var instrument = Instrument(id);
        var (playing, _) = Play.Render(instrument, 1_024, [TimedEvent.On(0, 60), TimedEvent.On(0, 70)]);
        Assert.True(Peak(playing.AsSpan(512)) > 0.01);
        instrument.Reset();
        var (after, afterRight) = Play.Render(instrument, 1_024, []);
        Assert.Equal(0, Peak(after) + Peak(afterRight));
    }

    [Theory]
    [MemberData(nameof(InstrumentIds))]
    public void Instrument_events_play_on_their_frame(string id) {
        using var late = Instrument(id);
        using var early = Instrument(id);
        var (shifted, shiftedRight) = Play.Render(
            late,
            8_192,
            [TimedEvent.On(100, 60), TimedEvent.Off(3_000, 60)],
            512
        );
        var (plain, plainRight) = Play.Render(early, 8_092, [TimedEvent.On(0, 60), TimedEvent.Off(2_900, 60)], 512);
        Assert.Equal(0, Peak(shifted.AsSpan(0, 100)));
        Assert.True(Peak(shifted.AsSpan(100, 200)) > 0);
        Assert.Equal(-300, NullResidualDb(shifted.AsSpan(100), plain));
        Assert.Equal(-300, NullResidualDb(shiftedRight.AsSpan(100), plainRight));
    }

    [Theory]
    [MemberData(nameof(InstrumentIds))]
    public void Instrument_output_does_not_depend_on_the_buffer_size(string id) {
        using var a = Instrument(id);
        using var b = Instrument(id);
        using var c = Instrument(id);
        var (one, _) = Play.Render(a, 12_000, Phrase());
        var (two, _) = Play.Render(b, 12_000, Phrase(), 1_000);
        var (three, threeRight) = Play.Render(c, 12_000, Phrase(), blocks: [1, 17, 128, 3, 512, 64]);
        Assert.True(Rms(one) > 0.01);
        Assert.Equal(-300, NullResidualDb(one, two));
        Assert.Equal(-300, NullResidualDb(one, three));
        Assert.True(Rms(threeRight) > 0.01);
    }

    [Theory]
    [MemberData(nameof(InstrumentIds))]
    public void Instrument_state_round_trips(string id) {
        using var original = Instrument(id);
        var parameters = original.Descriptor.Parameters;
        for (var i = 0; i < parameters.Count; i++) {
            var p = parameters[i];
            var elsewhere = p.Min + (p.Max - p.Min) * (0.3f + 0.1f * (i % 4));
            original.SetParameter(i, p.Default + (elsewhere - p.Default) * 0.25f);
        }

        var state = original.SaveState();
        Assert.NotNull(state);
        using var restored = T3Kit.CreateInstrument(id)!;
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
        using var other = Play.Create(AnodeSynth.Id);
        Assert.False(restored.LoadState(other.SaveState()));
        Assert.Equal(state, restored.SaveState());
    }

    [Theory]
    [MemberData(nameof(InstrumentIds))]
    public void Unprepared_instruments_render_silence(string id) {
        using var instrument = T3Kit.CreateInstrument(id)!;
        var left = new float[64];
        var right = new float[64];
        left[3] = 1;
        instrument.Process([new(0, 0x90, 60, 100)], left, right);
        Assert.Equal(0, Peak(left));
    }

    [Fact]
    public void Effect_descriptors_are_consistent() {
        foreach (var descriptor in T3Kit.Effects) {
            using var effect = T3Kit.CreateEffect(descriptor.Id)!;
            Assert.Same(descriptor, effect.Descriptor);
            Assert.Equal("Anode", descriptor.Vendor);
            Assert.False(descriptor.HasSidechain);
            Assert.Contains(descriptor.Parameters, static p => p.Macro is not null);
            for (var i = 0; i < descriptor.Parameters.Count; i++) {
                var p = descriptor.Parameters[i];
                Assert.Equal(i, p.Index);
                Assert.InRange(p.Default, p.Min, p.Max);
                Assert.Equal(p.Default, effect.GetParameter(i));
            }
        }

        Assert.Equal(["model", "drive", "tone", "mix", "output"], Saturation.Info.Parameters.Select(static p => p.Id));
        Assert.Equal(EffectCategories.Distortion, Saturation.Info.Category);
    }

    [Theory]
    [MemberData(nameof(EffectIds))]
    public void Effect_processing_allocates_nothing(string id) {
        using var effect = Effect(id);
        var parameters = effect.Descriptor.Parameters;
        var (music, musicRight) = Fx.Music(64 * 200);
        var left = new float[64];
        var right = new float[64];
        var allocated = AllocationProbe.Measure(() => {
                for (var block = 0; block < 200; block++) {
                    var p = parameters[block % parameters.Count];
                    effect.SetParameter(p.Index, p.Min + (p.Max - p.Min) * (block % 7) / 6f);
                    music.AsSpan(block * 64, 64).CopyTo(left);
                    musicRight.AsSpan(block * 64, 64).CopyTo(right);
                    effect.Process(left, right, default, default);
                    _ = effect.Reading;
                    _ = effect.LatencySamples;
                }

                effect.Reset();
                _ = effect.GetParameter(0);
            }
        );
        Assert.Equal(0, allocated);
    }

    [Theory]
    [MemberData(nameof(EffectIds))]
    public void Effect_output_is_finite_under_extreme_settings(string id) {
        using var effect = Effect(id);
        var parameters = effect.Descriptor.Parameters;
        var (music, musicRight) = Fx.Music(24_000);
        foreach (var extreme in new[] { 0f, 1f }) {
            for (var i = 0; i < parameters.Count; i++) {
                var p = parameters[i];
                effect.SetParameter(i, extreme == 0 ? p.Min : p.Max);
            }

            var (l, r) = Fx.Run(effect, [..music.Select(static s => s * 4)], musicRight);
            Assert.All(l, static s => Assert.True(float.IsFinite(s)));
            Assert.All(r, static s => Assert.True(float.IsFinite(s)));
        }
    }

    [Theory]
    [MemberData(nameof(EffectIds))]
    public void Effect_output_does_not_depend_on_the_buffer_size(string id) {
        using var a = Effect(id);
        using var b = Effect(id);
        using var c = Effect(id);
        var (left, right) = Fx.Music(20_000);
        var one = Fx.Run(a, left, right);
        var two = Fx.Run(b, left, right, 1_000);
        var three = Fx.Run(c, left, right, blocks: [1, 17, 128, 3, 512, 64]);
        Assert.True(Rms(one.Left) > 1e-3);
        Assert.Equal(-300, NullResidualDb(one.Left, two.Left));
        Assert.Equal(-300, NullResidualDb(one.Right, three.Right));
        Assert.Equal(-300, NullResidualDb(one.Left, three.Left));
    }

    [Theory]
    [MemberData(nameof(EffectIds))]
    public void Effect_reset_clears_history(string id) {
        using var effect = Effect(id);
        var (left, right) = Fx.Music(12_000);
        var first = Fx.Run(effect, left, right);
        var other = TestWav.Pluck(220, 48_000, 0.25, 7);
        Fx.Run(effect, other, other);
        effect.Reset();
        var again = Fx.Run(effect, left, right);
        Assert.Equal(-300, NullResidualDb(first.Left, again.Left));
        Assert.Equal(-300, NullResidualDb(first.Right, again.Right));
    }

    [Theory]
    [MemberData(nameof(EffectIds))]
    public void Effect_state_round_trips(string id) {
        using var original = Effect(id);
        var parameters = original.Descriptor.Parameters;
        for (var i = 0; i < parameters.Count; i++) {
            var p = parameters[i];
            var elsewhere = p.Min + (p.Max - p.Min) * (0.3f + 0.1f * (i % 4));
            original.SetParameter(i, p.Default + (elsewhere - p.Default) * 0.25f);
        }

        var state = original.SaveState();
        Assert.NotNull(state);
        using var restored = T3Kit.CreateEffect(original.Descriptor.Id)!;
        restored.Prepare(Fx.Rate, 1_024);
        Chain(restored, id);
        Assert.True(restored.LoadState(state));
        for (var i = 0; i < parameters.Count; i++) {
            Assert.Equal(original.GetParameter(i), restored.GetParameter(i));
        }

        Assert.Equal(state, restored.SaveState());
        Assert.Equal(original.LatencySamples, restored.LatencySamples);
        original.Reset();
        var (left, right) = Fx.Music(9_600);
        Assert.Equal(-300, NullResidualDb(Fx.Run(original, left, right).Left, Fx.Run(restored, left, right).Left));
        Assert.False(restored.LoadState([1, 2, 3]));
        using var gain = Fx.Create("anode.gain");
        Assert.False(restored.LoadState(gain.SaveState()));
        Assert.Equal(state, restored.SaveState());
    }

    [Theory]
    [MemberData(nameof(EffectIds))]
    public void Unprepared_effects_pass_audio_through(string id) {
        using var effect = T3Kit.CreateEffect(id == Chained ? MasteringAssistant.Id : id)!;
        var (left, right) = Fx.Music(256);
        var (l, r) = Fx.Run(effect, left, right);
        Assert.Equal(left, l);
        Assert.Equal(right, r);
        Assert.Equal(0, effect.LatencySamples);
    }

    [Fact]
    public void Saturation_at_no_mix_is_the_dry_signal_and_has_no_latency() {
        using var effect = T3Kit.Effect(Saturation.Id, (Saturation.Mix, 0), (Saturation.Drive, 100));
        var input = Fx.Impulse(4_096, 1_000, 0.25f);
        var (l, _) = Fx.Run(effect, input, input);
        Assert.Equal(0, effect.LatencySamples);
        Assert.Equal(-300, NullResidualDb(input, l));
    }

    [Fact]
    public void An_unanalysed_assistant_delays_by_its_latency_and_passes_a_quiet_impulse() {
        using var effect = T3Kit.Effect(MasteringAssistant.Id);
        var input = Fx.Impulse(4_096, 1_000, 0.25f);
        var (l, r) = Fx.Run(effect, input, input);
        var peak = 0;
        for (var i = 1; i < l.Length; i++) {
            if (Math.Abs(l[i]) > Math.Abs(l[peak])) {
                peak = i;
            }
        }

        Assert.Equal(1_000 + effect.LatencySamples, peak);
        Assert.Equal(0.25, l[peak], 3);
        Assert.Equal(0.25, r[peak], 3);
        Assert.Equal(96 + 16, effect.LatencySamples); // 2 ms of lookahead and the true-peak detector's delay
    }

    // ---- Format-changing inserts ----------------------------------------------------------------------------------

    public static TheoryData<string, SpeakerFormat> FormatCases =>
        new() {
            { DownMixerInsert.Id, SpeakerFormat.Surround51 },
            { DownMixerInsert.Id, SpeakerFormat.Surround714 },
            { BinauralInsert.Id, SpeakerFormat.Surround714 },
            { BinauralInsert.Id + "+fallback", SpeakerFormat.Surround51 }
        };

    static readonly Lazy<HrtfSet> Hrtfs = new(static () => HrtfSet.Load(HrtfSet.Find(Ear.RepositoryRoot())!));

    static IFormatEffect Format(string id, SpeakerFormat input, int maxFrames = 1_024) {
        IFormatEffect effect = id == DownMixerInsert.Id ? new DownMixerInsert() : new BinauralInsert();
        if (id == BinauralInsert.Id) {
            ((BinauralInsert)effect).UseHrtfs(Hrtfs.Value);
        }

        var layout = SpeakerLayout.Of(input);
        effect.Prepare(layout, SpeakerLayout.Of(effect.OutputFormat(input)), Fx.Rate, maxFrames);
        return effect;
    }

    /// <summary>A planar multichannel signal: a different tone and noise in every channel.</summary>
    static float[][] Surround(SpeakerFormat format, int frames) {
        var channels = SpeakerLayout.Of(format).ChannelCount;
        var signal = new float[channels][];
        var random = 0x9E3779B9u;
        for (var c = 0; c < channels; c++) {
            signal[c] = new float[frames];
            for (var i = 0; i < frames; i++) {
                random ^= random << 13;
                random ^= random >> 17;
                random ^= random << 5;
                signal[c][i] = 0.2f * MathF.Sin(2 * MathF.PI * (110 + 70 * c) * i / Fx.Rate)
                    + 0.05f * (random * (2f / uint.MaxValue) - 1);
            }
        }

        return signal;
    }

    /// <summary>Processes <paramref name="signal" /> in blocks of the sizes <paramref name="blocks" /> cycles through.</summary>
    static float[][] Run(IFormatEffect effect, SpeakerFormat input, float[][] signal, params int[] blocks) {
        var frames = signal[0].Length;
        var outputs = SpeakerLayout.Of(effect.OutputFormat(input)).ChannelCount;
        var result = new float[outputs][];
        for (var c = 0; c < outputs; c++) {
            result[c] = new float[frames];
        }

        var inBuffer = new float[signal.Length * 1_024];
        var outBuffer = new float[outputs * 1_024];
        var cycle = 0;
        for (var at = 0; at < frames;) {
            var n = Math.Min(blocks.Length == 0 ? 64 : blocks[cycle++ % blocks.Length], frames - at);
            var inBlock = new PlanarBlock(inBuffer, signal.Length, n);
            var outBlock = new PlanarBlock(outBuffer, outputs, n);
            for (var c = 0; c < signal.Length; c++) {
                signal[c].AsSpan(at, n).CopyTo(inBlock.Channel(c));
            }

            effect.Process(inBlock, outBlock);
            for (var c = 0; c < outputs; c++) {
                outBlock.Channel(c).CopyTo(result[c].AsSpan(at, n));
            }

            at += n;
        }

        return result;
    }

    [Fact]
    public void Format_effects_say_their_output_format() {
        using var down = new DownMixerInsert();
        Assert.Equal(SpeakerFormat.Stereo, down.OutputFormat(SpeakerFormat.Surround51));
        Assert.Equal(SpeakerFormat.Stereo, down.OutputFormat(SpeakerFormat.Stereo));
        Assert.Equal(SpeakerFormat.Mono, down.OutputFormat(SpeakerFormat.Mono));
        down.SetParameter(DownMixerInsert.Format, (int)SpeakerFormat.Surround51);
        Assert.Equal(SpeakerFormat.Surround51, down.OutputFormat(SpeakerFormat.Surround714));
        Assert.Equal(SpeakerFormat.Stereo, down.OutputFormat(SpeakerFormat.Stereo)); // not narrower: passes
        Assert.Equal(SpeakerFormat.Surround51, down.OutputFormat(SpeakerFormat.Surround51));
        using var binaural = new BinauralInsert();
        Assert.Equal(SpeakerFormat.Stereo, binaural.OutputFormat(SpeakerFormat.Surround916));
        Assert.Equal(["format", "lfe"], DownMixerInsert.Info.Parameters.Select(static p => p.Id));
        Assert.Equal(
            Enum.GetNames<SpeakerFormat>(),
            DownMixerInsert.Info.Parameters[DownMixerInsert.Format].ValueNames
        );
        Assert.Empty(BinauralInsert.Info.Parameters);
        Assert.Equal(0, binaural.LatencySamples);
    }

    [Theory]
    [MemberData(nameof(FormatCases))]
    public void Format_effects_allocate_nothing(string id, SpeakerFormat input) {
        using var effect = Format(id, input);
        var signal = Surround(input, 64);
        var inBuffer = new float[signal.Length * 64];
        var outBuffer = new float[SpeakerLayout.Of(effect.OutputFormat(input)).ChannelCount * 64];
        for (var c = 0; c < signal.Length; c++) {
            signal[c].CopyTo(inBuffer, c * 64);
        }

        var outputs = outBuffer.Length / 64;
        var allocated = AllocationProbe.Measure(() => {
                for (var block = 0; block < 200; block++) {
                    effect.SetParameter(DownMixerInsert.Lfe, -144 + block % 7 * 24);
                    effect.Process(new(inBuffer, signal.Length, 64), new(outBuffer, outputs, 64));
                    _ = effect.LatencySamples;
                }

                effect.Reset();
            }
        );
        Assert.Equal(0, allocated);
    }

    [Theory]
    [MemberData(nameof(FormatCases))]
    public void Format_effect_output_does_not_depend_on_the_buffer_size(string id, SpeakerFormat input) {
        using var a = Format(id, input);
        using var b = Format(id, input);
        if (id == DownMixerInsert.Id) {
            a.SetParameter(DownMixerInsert.Lfe, -6);
            b.SetParameter(DownMixerInsert.Lfe, -6);
        }

        var signal = Surround(input, 9_000);
        var one = Run(a, input, signal, 64);
        var two = Run(b, input, signal, 1, 17, 128, 3, 512, 64);
        Assert.True(Rms(one[0]) > 1e-3);
        for (var c = 0; c < one.Length; c++) {
            Assert.Equal(-300, NullResidualDb(one[c], two[c]));
        }
    }

    [Theory]
    [MemberData(nameof(FormatCases))]
    public void Format_effect_reset_clears_history_and_state_round_trips(string id, SpeakerFormat input) {
        using var effect = Format(id, input);
        if (id == DownMixerInsert.Id) {
            effect.SetParameter(DownMixerInsert.Lfe, -3);
        }

        var signal = Surround(input, 4_000);
        var first = Run(effect, input, signal);
        Run(effect, input, Surround(input, 1_500));
        effect.Reset();
        var again = Run(effect, input, signal);
        Assert.Equal(-300, NullResidualDb(first[0], again[0]));

        var state = effect.SaveState();
        Assert.NotNull(state);
        using var restored = Format(id, input);
        Assert.True(restored.LoadState(state));
        Assert.Equal(state, restored.SaveState());
        Assert.Equal(-300, NullResidualDb(Run(restored, input, signal)[1], first[1]));
        Assert.False(restored.LoadState([1, 2, 3]));
        using var other = T3Kit.Effect(Saturation.Id);
        Assert.False(restored.LoadState(other.SaveState()));
    }
}

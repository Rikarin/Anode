using Anode.Dsp.Effects;
using Anode.Dsp.Instruments;
using Anode.Dsp.Spatial;
using Anode.Engine.Mixing;
using Anode.Engine.Spatial;
using Anode.Ipc;
using Anode.Ipc.Segments;
using static Anode.Engine.Tests.MultichannelTests;

namespace Anode.Engine.Tests;

/// <summary>
///     A Down Mixer as workstream E's will be (docs/12-phase-11.md): Phase 10's BS.775 <see cref="Downmixer" /> from its
///     input to its <c>format</c>, its output <c>latency</c> frames late (for the delay compensation tests).
/// </summary>
sealed class TestDownMixer : IFormatEffect {
    public const string Id = "test.down-mixer";

    public static readonly EffectDescriptor Info = new(
        Id,
        "Test Down Mixer",
        "Anode tests",
        "1.0",
        EffectCategories.Utility,
        [
            new(0, "format", "Format", 0, 10, (int)SpeakerFormat.Stereo, string.Empty, 10),
            new(1, "latency", "Latency", 0, 1_000, 0, "frames")
        ],
        false
    );

    readonly float[] values = [(int)SpeakerFormat.Stereo, 0];
    Downmixer? downmix;
    float[] delay = [];
    int channels;
    int write;

    public EffectDescriptor Descriptor => Info;

    public int LatencySamples => (int)values[1];

    public int Prepares { get; private set; }

    public bool Disposed { get; private set; }

    public SpeakerFormat OutputFormat(SpeakerFormat input) {
        var to = (SpeakerFormat)(int)values[0];
        return SpeakerLayout.Of(to).ChannelCount < SpeakerLayout.Of(input).ChannelCount ? to : input;
    }

    public void Prepare(SpeakerLayout input, SpeakerLayout output, float sampleRate, int maxFrames) {
        downmix = output.ChannelCount < input.ChannelCount ? new(input, output) : null;
        channels = output.ChannelCount;
        delay = new float[channels * 2_048];
        Prepares++;
    }

    public void SetParameter(int index, float value) => values[index] = value;

    public float GetParameter(int index) => values[index];

    public void Process(PlanarBlock input, PlanarBlock output) {
        if (downmix is { } d) {
            d.Process(input, output);
        } else {
            for (var c = 0; c < channels; c++) {
                input.Channel(c).CopyTo(output.Channel(c));
            }
        }

        var latency = LatencySamples;
        if (latency == 0) {
            return;
        }

        for (var c = 0; c < channels; c++) {
            var ring = delay.AsSpan(c * 2_048, 2_048);
            var x = output.Channel(c);
            for (var i = 0; i < x.Length; i++) {
                var at = (write + i) & 2_047;
                ring[at] = x[i];
                x[i] = ring[(at - latency) & 2_047];
            }
        }

        write = (write + output.Frames) & 2_047;
    }

    public void Reset() {
        Array.Clear(delay);
        write = 0;
    }

    public byte[]? SaveState() => null;

    public bool LoadState(ReadOnlySpan<byte> state) => true;

    public void Dispose() => Disposed = true;
}

/// <summary>
///     Inserts that change their strip's format (docs/12-phase-11.md, workstream A's tests), with a Down Mixer double
///     on Phase 10's real BS.775 down-mix: the strip after it (its inserts, fader, sends and output) carries its output
///     format; delay compensation counts its latency; a bypassed one mixes by the edges' gains; a parameter that changes
///     its output is structure; on a surround master it changes what the device gets.
/// </summary>
public sealed class FormatInsertTests : IDisposable {
    const int Rate = 48_000;
    const int Seconds = 2;
    const float Tolerance = 1e-5f;
    readonly string dir = TestAudio.TempDirectory();
    readonly string mono;
    readonly string stereo;

    public FormatInsertTests() {
        mono = TestAudio.Write24(dir, "mono.wav", 1, Seconds * Rate, 1);
        stereo = TestAudio.Write24(dir, "stereo.wav", 2, Seconds * Rate, 10);
    }

    public void Dispose() => Directory.Delete(dir, true);

    EngineFile[] Files => [new(0, mono, 1, Rate, Seconds * Rate), new(1, stereo, 2, Rate, Seconds * Rate)];

    static float M(long n) => n is >= 0 and < Seconds * Rate ? TestAudio.Sample(n, 1) : 0f;

    static (float L, float R) S(long n) =>
        n is >= 0 and < Seconds * Rate ? (TestAudio.Sample(n, 10), TestAudio.Sample(n, 11)) : (0f, 0f);

    static TrackModel Model(int id, int file) => new(id, [], [TestAudio.Region(0, Seconds * Rate, file)]);

    static EngineInsert DownMixer(SpeakerFormat to, int latency = 0, bool bypassed = false) =>
        TestEffects.Insert(
            "dm",
            TestDownMixer.Id,
            new Dictionary<string, double> { ["format"] = (int)to, ["latency"] = latency },
            bypassed
        );

    static EngineInsert Doubled =>
        TestEffects.Insert("g", TestEffects.Gain, new Dictionary<string, double> { ["gain"] = 2 });

    sealed class FormatRig(
        string dir,
        TrackModel[] models,
        EngineTrack[] tracks,
        EngineFile[] files,
        EngineStrip master,
        int outputs
    ) : IDisposable {
        readonly Rig rig = new(dir, 64, models, tracks, files, master, outputs, registry: Registry());

        public EngineController Controller => rig.Controller;

        public float[][] Render(int frames) => rig.Render(frames);

        static Plugins.PluginRegistry Registry() {
            var registry = SpatialFixtures.Registry();
            registry.AddBuiltinFormatEffect(TestDownMixer.Info, static () => new TestDownMixer());
            return registry;
        }

        public void Dispose() => rig.Dispose();
    }

    static void Near(float expected, float actual, int frame, string what) {
        if (Math.Abs(expected - actual) > Tolerance * Math.Max(1, Math.Abs(expected))) {
            Assert.Fail($"{what}, frame {frame}: expected {expected}, got {actual}");
        }
    }

    /// <summary>The 5.1 bus of the tests: the stereo file in L and R, the mono one in Ls.</summary>
    static float[] Bus(int f) {
        var (l, r) = S(f);
        return [l, r, 0, 0, M(f), 0];
    }

    static (float L, float R) Downmixed(float[] bus) {
        var d = new Downmixer(SpeakerLayout.Of(SpeakerFormat.Surround51), SpeakerLayout.Of(SpeakerFormat.Stereo));
        float l = 0, r = 0;
        for (var i = 0; i < bus.Length; i++) {
            l += d.Gain(0, i) * bus[i];
            r += d.Gain(1, i) * bus[i];
        }

        return (l, r);
    }

    static EngineTrack[] Feeders(EngineChannelFormat bus) => [
        TestAudio.Track(1, true, Strip(1f, 1, outputFormat: bus, panner: Surround(0))),
        TestAudio.Track(2, false, Strip(1f, 1, outputFormat: bus, panner: Surround(4)))
    ];

    [Fact]
    public void A_down_mixer_on_a_surround_bus_makes_the_rest_of_the_strip_stereo() {
        const EngineChannelFormat f51 = EngineChannelFormat.Surround51;
        EngineTrack[] tracks = [
            ..Feeders(f51),
            // The 5.1 aux: down-mixed, then doubled in stereo, to the stereo master and (post-fader, half) a stereo bus.
            TestAudio.Track(
                10,
                true,
                Strip(
                    0.5f,
                    0,
                    f51,
                    inserts: [DownMixer(SpeakerFormat.Stereo), Doubled],
                    sends: [new("s", 2, 0.5f, 0f, false)]
                )
            ) with {
                InputBus = 1
            },
            TestAudio.Track(11, true, Strip(1f, -2)) with { InputBus = 2 } // to outputs 3-4
        ];
        using var rig = new FormatRig(
            dir,
            [Model(1, 1), Model(2, 0), new(10, [], []), new(11, [], [])],
            tracks,
            Files,
            Strip(),
            4
        );
        var plan = rig.Controller.Engine.Root.Mix;
        var aux = plan.Order.Single(static n => n.TrackId == 10);
        Assert.True(aux.Multi);
        Assert.True(aux.FormatInserts);
        Assert.Equal((6, 2, SpeakerFormat.Stereo), (aux.Width, aux.SignalWidth, aux.SignalFormat));
        Assert.Equal(EdgeKind.Stereo, aux.OutputEdge!.Kind); // stereo into the stereo master: no down-mix edge
        var frames = 4_800;
        var o = rig.Render(frames);
        for (var f = 0; f < frames; f++) {
            var (l, r) = Downmixed(Bus(f));
            Near(l * 2 * 0.5f, o[0][f], f, "master left");
            Near(r * 2 * 0.5f, o[1][f], f, "master right");
            Near(l * 2 * 0.5f * 0.5f, o[2][f], f, "the send's bus, left");
            Near(r * 2 * 0.5f * 0.5f, o[3][f], f, "the send's bus, right");
        }
    }

    [Fact]
    public void Delay_compensation_counts_a_format_inserts_latency() {
        const EngineChannelFormat f51 = EngineChannelFormat.Surround51;
        EngineTrack[] tracks = [
            ..Feeders(f51),
            TestAudio.Track(10, true, Strip(1f, 0, f51, inserts: [DownMixer(SpeakerFormat.Stereo, 100)])) with {
                InputBus = 1
            },
            TestAudio.Track(3, false, Strip()) // straight to the master: waits for the latent aux
        ];
        using var rig = new FormatRig(
            dir,
            [Model(1, 1), Model(2, 0), new(10, [], []), Model(3, 0)],
            tracks,
            Files,
            Strip(),
            2
        );
        rig.Controller.SetMixer("all", false, 0);
        var frames = 4_800;
        var o = rig.Render(frames);
        for (var f = 0; f < frames; f++) {
            var (l, r) = f >= 100 ? Downmixed(Bus(f - 100)) : (0f, 0f);
            Near(l + M(f - 100), o[0][f], f, "left");
            Near(r + M(f - 100), o[1][f], f, "right");
        }
    }

    [Fact]
    public void A_bypassed_one_mixes_by_the_edges_gains_and_a_parameter_that_changes_its_output_is_structure() {
        const EngineChannelFormat f51 = EngineChannelFormat.Surround51;
        EngineTrack[] tracks = [
            ..Feeders(f51),
            TestAudio.Track(
                10,
                true,
                Strip(1f, 0, f51, inserts: [DownMixer(SpeakerFormat.Stereo, bypassed: true)])
            ) with {
                InputBus = 1
            }
        ];
        using var rig = new FormatRig(dir, [Model(1, 1), Model(2, 0), new(10, [], [])], tracks, Files, Strip(), 2);
        var matrix = Formats.Conversion(
            SpeakerLayout.Of(SpeakerFormat.Surround51),
            SpeakerLayout.Of(SpeakerFormat.Stereo),
            rig.Controller.Engine.Spatial
        );
        var frames = 2_048;
        var o = rig.Render(frames);
        for (var f = 0; f < frames; f++) {
            var bus = Bus(f);
            float l = 0, r = 0;
            for (var i = 0; i < 6; i++) {
                l += matrix[i] * bus[i];
                r += matrix[6 + i] * bus[i];
            }

            Near(l, o[0][f], f, "bypassed, left");
            Near(r, o[1][f], f, "bypassed, right");
        }

        // To mono: a new stage, a mono strip after it (its pan law's centre on both sides).
        var root = rig.Controller.Engine.Root;
        var slot = root.Tracks[root.IndexOf(10)].Channel.Config.Inserts[0].Slot;
        var before = slot.Format;
        Assert.True(rig.Controller.SetChannel(10, Strip(1f, 0, f51, inserts: [DownMixer(SpeakerFormat.Stereo)])));
        Assert.True(rig.Controller.SetInsertParameter(10, "dm", "format", (int)SpeakerFormat.Mono));
        Assert.NotSame(before, slot.Format);
        Assert.Equal(SpeakerFormat.Mono, slot.Format!.Out);
        Assert.Equal(1, rig.Controller.Engine.Root.Mix.Order.Single(static n => n.TrackId == 10).SignalWidth);
        var d = new Downmixer(SpeakerLayout.Of(SpeakerFormat.Surround51), SpeakerLayout.Of(SpeakerFormat.Mono));
        o = rig.Render(frames);
        for (var f = 0; f < frames; f++) {
            var bus = Bus(f);
            var m = bus.Select((x, i) => d.Gain(0, i) * x).Sum();
            Near(m, o[0][f], f, "mono, left");
            Near(m, o[1][f], f, "mono, right");
        }
    }

    [Fact]
    public void A_down_mixer_on_a_surround_master_sends_the_device_its_format() {
        const EngineChannelFormat f51 = EngineChannelFormat.Surround51;
        using var rig = new FormatRig(
            dir,
            [Model(1, 1), Model(2, 0)],
            [
                TestAudio.Track(1, true, Strip(1f, 0, outputFormat: f51, panner: Surround(0))),
                TestAudio.Track(2, false, Strip(1f, 0, outputFormat: f51, panner: Surround(4)))
            ],
            Files,
            Strip(0.5f, input: f51, outputFormat: f51, inserts: [DownMixer(SpeakerFormat.Stereo), Doubled]),
            6
        );
        var plan = rig.Controller.Engine.Root.Mix;
        Assert.True(plan.MasterMulti);
        Assert.Equal((SpeakerFormat.Surround51, SpeakerFormat.Stereo), (plan.MasterFormat, plan.MasterOutputFormat));
        var frames = 2_048;
        var o = rig.Render(frames);
        for (var f = 0; f < frames; f++) {
            var (l, r) = Downmixed(Bus(f));
            Near(l, o[0][f], f, "device left");
            Near(r, o[1][f], f, "device right");
            for (var c = 2; c < 6; c++) {
                Near(0, o[c][f], f, $"device output {c}");
            }
        }
    }

    [Fact]
    public void Format_inserts_are_in_the_catalogue_and_unknown_ones_pass_the_signal() {
        using var rig = new FormatRig(dir, [Model(1, 1)], [TestAudio.Track(1, true, Strip())], Files, Strip(), 2);
        var catalog = rig.Controller.Plugins.Catalog();
        Assert.Contains(catalog, static p => p.Id == TestDownMixer.Id && !p.IsInstrument);
        Assert.True(rig.Controller.Plugins.IsFormatEffect(TestDownMixer.Id));
        Assert.False(rig.Controller.Plugins.IsFormatEffect(TestEffects.Gain));
        // The T3 ones are registered once workstream E's are in (Anode.Dsp.T3.Ready).
        Assert.Equal(Dsp.T3.Ready, BuiltinFormatEffects.All.Count == 2);
        Assert.Equal(Dsp.T3.Ready, BuiltinEffects.All.Contains(Dsp.Mastering.MasteringAssistant.Info));
        Assert.Equal(Dsp.T3.Ready, BuiltinInstruments.All.Contains(Dsp.Instruments.Physical.PhysicalModel.Info));
    }
}

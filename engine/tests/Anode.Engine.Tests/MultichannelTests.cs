using Anode.Dsp.Effects.Dynamics;
using Anode.Dsp.Spatial;
using Anode.Engine.Spatial;
using Anode.Ipc;
using Anode.Ipc.Segments;
using System.Buffers.Binary;

namespace Anode.Engine.Tests;

/// <summary>
///     The multichannel graph (docs/11-phase-10.md, workstream A's tests) with doubles of workstream D's spatial DSP
///     (<see cref="TestSpatial" />): routing and sums into every layout, beds, buses and sends across formats, delay
///     compensation across formats, effects on a surround bus (per channel, linked, front pair), panner automation at
///     the automation path's resolution, objects at the master, and the device stage's monitoring and speaker map.
/// </summary>
public sealed class MultichannelTests : IDisposable {
    const int Rate = 48_000;
    const int Seconds = 2;
    const float Tolerance = 1e-6f;
    readonly string dir = TestAudio.TempDirectory();
    readonly string mono;
    readonly string stereo;
    readonly string quiet;

    public MultichannelTests() {
        mono = TestAudio.Write24(dir, "mono.wav", 1, Seconds * Rate, 1);
        stereo = TestAudio.Write24(dir, "stereo.wav", 2, Seconds * Rate, 10);
        quiet = TestAudio.Write24(dir, "quiet.wav", 1, Seconds * Rate, 20);
    }

    public void Dispose() => Directory.Delete(dir, true);

    EngineFile[] Files => [
        new(0, mono, 1, Rate, Seconds * Rate), new(1, stereo, 2, Rate, Seconds * Rate),
        new(2, quiet, 1, Rate, Seconds * Rate)
    ];

    static float M(long n) => n is >= 0 and < Seconds * Rate ? TestAudio.Sample(n, 1) : 0f;

    static float Q(long n) => n is >= 0 and < Seconds * Rate ? TestAudio.Sample(n, 20) : 0f;

    static (float L, float R) S(long n) =>
        n is >= 0 and < Seconds * Rate ? (TestAudio.Sample(n, 10), TestAudio.Sample(n, 11)) : (0f, 0f);

    internal static EnginePanner Surround(float angle, float diversity = 1f, float lfe = 0f) =>
        new(
            EnginePannerMode.Surround,
            new(angle, diversity, 0, 1, lfe, 60),
            new(0, 0, 1, 0)
        );

    internal static EnginePanner Object(float azimuth, float distance = 1f) =>
        new(EnginePannerMode.Object, new(0, 1, 0, 1, 0, 60), new(azimuth, 0, distance, 0));

    internal static EngineStrip Strip(
        float gain = 1f,
        int output = 0,
        EngineChannelFormat input = EngineChannelFormat.Stereo,
        EngineChannelFormat outputFormat = EngineChannelFormat.Stereo,
        EnginePanner? panner = null,
        EngineInsert[]? inserts = null,
        EngineSend[]? sends = null
    ) =>
        new(
            gain,
            0f,
            false,
            false,
            inserts,
            sends,
            output,
            InputFormat: input,
            OutputFormat: outputFormat,
            Panner: panner
        );

    static TrackModel Model(int id, int file, PlaybackEvent[]? events = null) =>
        new(id, events ?? [], [TestAudio.Region(0, Seconds * Rate, file)]);

    static PlaybackEvent Automation(long position, int target, float value, int track, bool step = false) =>
        new() {
            SamplePos = position,
            Track = track,
            Kind = PlaybackEventKind.Automation,
            Data1 = step ? (byte)1 : (byte)0,
            Value = value,
            Target = target
        };

    internal sealed class Rig : IDisposable {
        public Rig(
            string dir,
            int block,
            TrackModel[] models,
            EngineTrack[] tracks,
            EngineFile[] files,
            EngineStrip master,
            int outputs,
            TestSpatial? spatial = null,
            EngineAutomationTarget[]? targets = null,
            Plugins.PluginRegistry? registry = null
        ) {
            Spatial = spatial ?? new TestSpatial();
            Controller = new(new(Rate, block), registry ?? SpatialFixtures.Registry()) { SpatialFactory = Spatial };
            using var segment = TestAudio.Segment(models, 1);
            Controller.LoadModel(
                TestAudio.Load(segment, 1, true, tracks, files, dir, master) with { AutomationTargets = targets }
            );
            Driver = new(Controller, block, 2, outputs);
        }

        public TestSpatial Spatial { get; }

        public EngineController Controller { get; }

        public OfflineDriver Driver { get; }

        public float[][] Render(int frames) {
            Driver.Send(new(EngineCommandKind.Stop)); // a render after a render plays from the start again
            Driver.Process(false);
            Driver.PlayFrom(0);
            var underruns = Controller.Engine.Snapshot.Underruns;
            frames = (frames + Driver.Block - 1) / Driver.Block * Driver.Block;
            var channels = new float[Driver.OutputChannels][];
            for (var c = 0; c < channels.Length; c++) {
                channels[c] = new float[frames];
            }

            for (var at = 0; at < frames; at += Driver.Block) {
                Driver.Process();
                for (var c = 0; c < channels.Length; c++) {
                    Driver.Output(c).CopyTo(channels[c].AsSpan(at));
                }
            }

            Assert.Equal(underruns, Controller.Engine.Snapshot.Underruns);
            return channels;
        }

        public void Dispose() {
            Driver.Dispose();
            Controller.Dispose();
        }
    }

    static void Near(float expected, float actual, int frame, string what) {
        if (Math.Abs(expected - actual) > Tolerance * Math.Max(1, Math.Abs(expected))) {
            Assert.Fail($"{what}, frame {frame}: expected {expected}, got {actual}");
        }
    }

    public static TheoryData<EngineChannelFormat> Layouts =>
        new(
            EngineChannelFormat.Mono,
            EngineChannelFormat.Lcr,
            EngineChannelFormat.Quad,
            EngineChannelFormat.Surround50,
            EngineChannelFormat.Surround51,
            EngineChannelFormat.Surround70,
            EngineChannelFormat.Surround71,
            EngineChannelFormat.Surround712,
            EngineChannelFormat.Surround714,
            EngineChannelFormat.Surround916
        );

    [Fact]
    public void The_engine_formats_are_the_dsp_layouts_in_the_contract_order() {
        foreach (var format in Enum.GetValues<EngineChannelFormat>()) {
            Assert.Equal(format.ToString(), Formats.Of(format).ToString());
            Assert.Equal(SpeakerLayout.Of(Formats.Of(format)).ChannelCount, Formats.Channels(Formats.Of(format)));
        }

        Assert.Equal(16, SpeakerLayout.Of(SpeakerFormat.Surround916).ChannelCount);
        Assert.Equal(Formats.MaxChannels, SpeakerLayout.Of(SpeakerFormat.Surround916).ChannelCount);
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Mono_and_stereo_tracks_pan_into_a_master_of_every_layout(EngineChannelFormat format) {
        var n = SpeakerLayout.Of(Formats.Of(format)).ChannelCount;
        EngineTrack[] tracks = [
            TestAudio.Track(1, false, Strip(0.5f, outputFormat: format, panner: Surround(n - 1, 0.8f))),
            TestAudio.Track(2, true, Strip(1f, outputFormat: format, panner: Surround(0)))
        ];
        using var rig = new Rig(
            dir,
            64,
            [Model(1, 0), Model(2, 1)],
            tracks,
            Files,
            Strip(0.75f, input: format, outputFormat: format),
            n
        );
        var frames = 6_400;
        var o = rig.Render(frames);
        for (var f = 0; f < frames; f++) {
            var expected = new float[n];
            var (l, r) = S(f);
            if (n == 1) {
                expected[0] = M(f) * 0.5f + 0.5f * (l + r);
            } else {
                expected[n - 1] += M(f) * 0.5f * 0.8f;
                expected[0] += l;
                expected[1] += r;
            }

            for (var c = 0; c < n; c++) {
                Near(expected[c] * 0.75f, o[c][f], f, $"{format} channel {c}");
            }
        }

        Assert.Contains(o[n - 1], static v => v != 0);
        if (n > 2) {
            Span<float> peaks = stackalloc float[16];
            rig.Controller.Engine.Master.TakePeaks(peaks, out var count);
            Assert.Equal(n, count);
            Assert.True(peaks[n - 1] > 0);
        }
    }

    [Fact]
    public void Beds_buses_and_sends_meet_across_formats() {
        const EngineChannelFormat f714 = EngineChannelFormat.Surround714;
        const EngineChannelFormat f51 = EngineChannelFormat.Surround51;
        EngineInsert[] doubled =
            [TestEffects.Insert("g", TestEffects.Gain, new Dictionary<string, double> { ["gain"] = 2 })];
        EngineTrack[] tracks = [
            TestAudio.Track(1, false, Strip(1f, 1, outputFormat: f714, panner: Surround(6))),
            TestAudio.Track(
                2,
                true,
                Strip(1f, 2, outputFormat: f51, panner: Surround(4), sends: [new("pre", 1, 0.5f, 0f, true)])
            ),
            // A 7.1.4 bed aux (doubled per channel) to the master, sending its down-mix to a stereo bus.
            TestAudio.Track(
                10,
                true,
                Strip(0.8f, 0, f714, f714, inserts: doubled, sends: [new("dm", 3, 1f, 0f, false)])
            ) with {
                InputBus = 1
            },
            TestAudio.Track(11, true, Strip(1f, 0, f51, f714)) with { InputBus = 2 }, // 5.1 into a 7.1.4 master
            TestAudio.Track(12, true, Strip(1f, -7)) with { InputBus = 3 } // the stereo bus to outputs 13-14
        ];
        using var rig = new Rig(
            dir,
            64,
            [Model(1, 0), Model(2, 1), new(10, [], []), new(11, [], []), new(12, [], [])],
            tracks,
            Files,
            Strip(1f, input: f714, outputFormat: f714),
            14
        );
        var frames = 4_800;
        var o = rig.Render(frames);
        for (var f = 0; f < frames; f++) {
            var (l, r) = S(f);
            var bus1 = new float[12];
            bus1[6] = M(f);
            bus1[4] += 0.5f * l;
            bus1[5] += 0.5f * r;
            var master = new float[12];
            for (var c = 0; c < 12; c++) {
                master[c] = bus1[c] * 2 * 0.8f;
            }

            // The 5.1 bus: its surrounds (M±110) land on 7.1.4's nearest speakers, the sides (M±090).
            master[4] += l;
            master[5] += r;
            float downL = 0, downR = 0;
            for (var c = 0; c < 12; c++) {
                if ((c & 1) == 0) {
                    downL += 0.5f * bus1[c] * 2;
                } else {
                    downR += 0.5f * bus1[c] * 2;
                }
            }

            for (var c = 0; c < 12; c++) {
                Near(master[c], o[c][f], f, $"master channel {c}");
            }

            Near(downL * 0.8f, o[12][f], f, "down-mix left");
            Near(downR * 0.8f, o[13][f], f, "down-mix right");
        }
    }

    [Fact]
    public void Delay_compensation_lines_up_paths_of_every_format() {
        const EngineChannelFormat f714 = EngineChannelFormat.Surround714;
        var delay100 = TestEffects.Insert("d", TestEffects.Delay, new Dictionary<string, double> { ["frames"] = 100 });
        var delay40 = TestEffects.Insert("d", TestEffects.Delay, new Dictionary<string, double> { ["frames"] = 40 });
        EngineTrack[] tracks = [
            TestAudio.Track(1, false, Strip(outputFormat: f714, panner: Surround(0), inserts: [delay100])),
            TestAudio.Track(2, false, Strip(outputFormat: f714, panner: Surround(2))),
            TestAudio.Track(3, false, Strip(output: 1, outputFormat: f714, panner: Surround(7))),
            TestAudio.Track(10, true, Strip(1f, 0, f714, f714, inserts: [delay40])) with { InputBus = 1 }
        ];
        using var rig = new Rig(
            dir,
            64,
            [Model(1, 0), Model(2, 0), Model(3, 0), new(10, [], [])],
            tracks,
            Files,
            Strip(input: f714, outputFormat: f714),
            12
        );
        rig.Controller.SetMixer("all", false, 0);
        var frames = 4_800;
        var o = rig.Render(frames);
        for (var f = 0; f < frames; f++) {
            Near(M(f - 100), o[0][f], f, "the latent track (stereo after its insert)");
            Near(M(f - 100), o[1][f], f, "the latent track's second channel");
            Near(M(f - 100), o[2][f], f, "the track it waits for");
            Near(M(f - 100), o[7][f], f, "through the latent surround aux (a front-pair insert's other channels)");
            Near(0, o[3][f], f, "silent channel");
        }
    }

    [Theory]
    [InlineData(ChannelLink.All, true)]
    [InlineData(ChannelLink.ByGroup, false)]
    public void The_compressor_s_channel_link_chooses_which_channels_a_loud_one_ducks(int link, bool ducked) {
        const EngineChannelFormat f51 = EngineChannelFormat.Surround51;
        var compressor = TestEffects.Insert(
            "c",
            Compressor.Id,
            new Dictionary<string, double> {
                ["threshold"] = -40,
                ["ratio"] = 20,
                ["attack"] = 0.1,
                ["release"] = 5,
                ["autoGain"] = 0,
                ["link"] = link
            }
        );
        EngineTrack[] tracks = [
            TestAudio.Track(1, false, Strip(1.8f, 1, outputFormat: f51, panner: Surround(0))), // loud, in L
            TestAudio.Track(2, false, Strip(0.02f, 1, outputFormat: f51, panner: Surround(4))), // quiet, in Ls
            TestAudio.Track(10, true, Strip(1f, 0, f51, f51, inserts: [compressor])) with { InputBus = 1 }
        ];
        using var rig = new Rig(
            dir,
            64,
            [Model(1, 0), Model(2, 2), new(10, [], [])],
            tracks,
            Files,
            Strip(input: f51, outputFormat: f51),
            6
        );
        rig.Controller.SetMixer("off", false, 0);
        var o = rig.Render(24_000);
        // The surround channel's level over the last half of the render, against its dry level.
        double energy = 0, dry = 0;
        for (var f = 12_000; f < 24_000; f++) {
            energy += o[4][f] * o[4][f];
            var q = Q(f) * 0.02f;
            dry += q * q;
        }

        var db = 10 * Math.Log10(energy / dry);
        if (ducked) {
            Assert.True(db < -6, $"{db:F1} dB");
        } else {
            Assert.True(Math.Abs(db) < 1.5, $"{db:F1} dB");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void A_linked_dynamics_insert_on_a_surround_bus_ducks_every_channel_together(int latency) {
        const EngineChannelFormat f714 = EngineChannelFormat.Surround714;
        var duck = TestEffects.Insert("k", DuckEffect.Id, new Dictionary<string, double> { ["frames"] = latency });
        EngineTrack[] tracks = [
            TestAudio.Track(1, false, Strip(1.8f, 1, outputFormat: f714, panner: Surround(5))), // loud, in Rs
            TestAudio.Track(2, false, Strip(0.1f, 1, outputFormat: f714, panner: Surround(0))), // quiet, in L
            TestAudio.Track(10, true, Strip(1f, 0, f714, f714, inserts: [duck])) with { InputBus = 1 }
        ];
        using var rig = new Rig(
            dir,
            64,
            [new(1, [], [TestAudio.Region(0, 2_400, 0)]), Model(2, 2), new(10, [], [])],
            tracks,
            Files,
            Strip(input: f714, outputFormat: f714),
            12
        );
        rig.Controller.SetMixer("off", false, 0);
        var frames = 4_800;
        var o = rig.Render(frames);
        var ducked = 0;
        for (var f = 0; f < frames; f++) {
            var loud = f < 2_400 ? M(f) * 1.8f : 0f; // loud for the first half
            var soft = Q(f) * 0.1f;
            var key = Math.Abs(soft) > Math.Abs(loud) ? soft : loud;
            var g = Math.Abs(key) > 0.5f ? 0.25f : 1f;
            ducked += g < 1 ? 1 : 0;
            Near((f - latency < 2_400 ? M(f - latency) : 0) * 1.8f * g, o[5][f], f, "the loud channel");
            Near(Q(f - latency) * 0.1f * g, o[0][f], f, "the quiet channel, linked to the loud one");
        }

        Assert.InRange(ducked, frames / 10, frames - frames / 10);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(37)]
    public void Panner_automation_follows_its_lane_through_the_automation_path(int block) {
        const EngineChannelFormat f714 = EngineChannelFormat.Surround714;
        EngineAutomationTarget[] targets =
            [new(0, 1, "surround", null, "diversity"), new(1, 1, "surround", null, "angle")];
        PlaybackEvent[] events = [
            Automation(0, 0, 0f, 1), Automation(0, 1, 2f, 1), Automation(9_600, 0, 1f, 1),
            Automation(12_000, 1, 9f, 1, true)
        ];
        EngineTrack[] tracks = [TestAudio.Track(1, false, Strip(outputFormat: f714, panner: Surround(0)))];
        using var rig = new Rig(
            dir,
            block,
            [Model(1, 0, events)],
            tracks,
            Files,
            Strip(input: f714, outputFormat: f714),
            12,
            targets: targets
        );
        var frames = 14_400;
        var o = rig.Render(frames);
        // From the second piece: the first ramps from the stopped block's settings (the lane at 0).
        for (var f = AudioEngine.ParameterBlock; f < 12_000 - 64; f++) {
            if (Math.Abs(f - 9_600) < AudioEngine.ParameterBlock) {
                continue; // the piece across the lane's corner ramps straight through it
            }

            var lane = Math.Min(1f, f / 9_600f);
            Near(M(f) * lane, o[2][f], f, "channel 2, its gain ramping with the lane");
        }

        for (var f = 12_000 + 64; f < frames; f++) {
            Near(M(f), o[9][f], f, "after the angle's step: channel 9");
            Near(0, o[2][f], f, "channel 2 after the step");
        }

        // Each piece's settings are the lanes' at the piece's last frame (32-frame pieces).
        var panner = Assert.Single(rig.Spatial.Panners);
        var at = 0L;
        // The first two blocks were played stopped (the stop and the locate before play): their pieces come first.
        var calls = panner.Calls.Skip(2 * ((block + AudioEngine.ParameterBlock - 1) / AudioEngine.ParameterBlock))
            .ToList();
        Assert.All(calls, static c => Assert.InRange(c.Frames, 1, AudioEngine.ParameterBlock));
        foreach (var (settings, length) in calls) {
            var end = at + length - 1;
            if (end >= 0 && at < frames) {
                var expected = Math.Min(1f, end / 9_600f);
                Assert.True(
                    Math.Abs(expected - settings.Diversity) < 1e-6,
                    $"piece at {at}: {settings.Diversity} for {expected}"
                );
                Assert.Equal(end >= 12_000 ? 9f : 2f, settings.Angle);
            }

            at += length;
        }
    }

    [Fact]
    public void Objects_render_at_the_master_after_the_bed_and_before_its_inserts() {
        const EngineChannelFormat f714 = EngineChannelFormat.Surround714;
        EngineAutomationTarget[] targets = [new(0, 2, "object", null, "azimuth")];
        PlaybackEvent[] moves = [Automation(0, 0, 0f, 2), Automation(4_800, 0, 8f, 2, true)];
        EngineInsert[] masterGain =
            [TestEffects.Insert("g", TestEffects.Gain, new Dictionary<string, double> { ["gain"] = 0.5 })];
        EngineTrack[] tracks = [
            TestAudio.Track(1, false, Strip(1f, outputFormat: f714, panner: Object(3, 0.5f))),
            TestAudio.Track(2, false, Strip(1f, outputFormat: f714, panner: Object(0))),
            TestAudio.Track(3, false, Strip(1f, outputFormat: f714, panner: Surround(1)))
        ];
        using var rig = new Rig(
            dir,
            64,
            [Model(1, 0), Model(2, 2, moves), Model(3, 0)],
            tracks,
            Files,
            Strip(input: f714, outputFormat: f714, inserts: masterGain),
            12,
            targets: targets
        );
        var plan = rig.Controller.Engine.Root.Mix;
        Assert.Equal(2, plan.Objects.Length);
        Assert.Equal(ObjectRenderer.MaxObjects, rig.Spatial.Objects!.Prepared);
        var frames = 9_600;
        var o = rig.Render(frames);
        for (var f = 0; f < frames; f++) {
            Near(M(f) * 0.5f * 0.5f, o[3][f], f, "object 1 at its channel, through the master's insert");
            Near(M(f) * 0.5f, o[1][f], f, "the bed");
            if (f < 4_800) {
                Near(Q(f) * 0.5f, o[0][f], f, "object 2 before its move");
                Near(0, o[8][f], f, "object 2's destination before the move");
            } else if (f >= 4_800 + 32) {
                Near(Q(f) * 0.5f, o[8][f], f, "object 2 after its move");
                Near(0, o[0][f], f, "object 2's origin after the move");
            }
        }
    }

    [Fact]
    public void A_panner_move_holds_its_automation_and_a_mode_change_rebuilds_the_plan() {
        const EngineChannelFormat f714 = EngineChannelFormat.Surround714;
        EngineAutomationTarget[] targets = [new(0, 1, "surround", null, "angle")];
        PlaybackEvent[] events = [Automation(0, 0, 2f, 1)];
        EngineTrack[] tracks = [TestAudio.Track(1, false, Strip(outputFormat: f714, panner: Surround(2)))];
        using var rig = new Rig(
            dir,
            64,
            [Model(1, 0, events)],
            tracks,
            Files,
            Strip(input: f714, outputFormat: f714),
            12,
            targets: targets
        );
        var o = rig.Render(640);
        Near(M(320), o[2][320], 320, "the lane's angle");

        // A move holds the lane until the next play or locate (a touch pass is heard as it is recorded).
        Assert.True(rig.Controller.SetStrip(1, Strip(outputFormat: f714, panner: Surround(7))));
        Assert.True(rig.Controller.Engine.Overrides.IsHeld(0));
        for (var b = 0; b < 3; b++) {
            rig.Driver.Process();
        }

        Assert.Equal(0f, rig.Driver.Output(2)[10]);
        Assert.NotEqual(0f, rig.Driver.Output(7)[10]);

        // Object mode through setStrip: the plan is rebuilt with the track as an object.
        Assert.Empty(rig.Controller.Engine.Root.Mix.Objects);
        Assert.True(rig.Controller.SetStrip(1, Strip(outputFormat: f714, panner: Object(4))));
        Assert.Single(rig.Controller.Engine.Root.Mix.Objects);
        var objects = rig.Render(640);
        Near(M(320), objects[4][320], 320, "the object at its channel");
    }

    [Fact]
    public void Object_mode_without_a_surround_master_is_a_stereo_pan() {
        EngineTrack[] tracks = [TestAudio.Track(1, false, Strip(1f, panner: Object(3)))];
        using var rig = new Rig(dir, 64, [Model(1, 0)], tracks, Files, Strip(), 2);
        Assert.Empty(rig.Controller.Engine.Root.Mix.Objects);
        Assert.False(rig.Controller.Engine.Root.Mix.Multichannel);
        var o = rig.Render(1_280);
        var (gl, gr) = Engine.Strip.PanGains(0, false);
        for (var f = 0; f < 1_280; f++) {
            Assert.Equal(M(f) * gl, o[0][f]);
            Assert.Equal(M(f) * gr, o[1][f]);
        }
    }

    [Theory]
    [InlineData(EngineSoloMode.Pfl)]
    [InlineData(EngineSoloMode.Afl)]
    public void A_soloed_surround_strip_reaches_the_monitors_front_pair_and_a_bounce_keeps_the_mix(EngineSoloMode mode) {
        const EngineChannelFormat f51 = EngineChannelFormat.Surround51;
        EngineTrack[] tracks = [
            TestAudio.Track(1, false, Strip(0.5f, outputFormat: f51, panner: Surround(2)) with { Solo = true }),
            TestAudio.Track(2, false, Strip(1f, outputFormat: f51, panner: Surround(5)))
        ];
        using var rig = new Rig(dir, 64, [Model(1, 0), Model(2, 2)], tracks, Files, Strip(input: f51, outputFormat: f51), 6);
        rig.Controller.SetMixer("all", false, 10, soloMode: mode);
        var o = rig.Render(1_280);
        // PFL hears the source itself, unfaded; AFL the panned channel, faded, through the stand-in's down-mix (½, even
        // channels to the left).
        var (left, right) = mode == EngineSoloMode.Pfl ? (1f, 1f) : (0.25f, 0f);
        for (var f = 64; f < 1_280; f++) {
            Near(M(f) * left, o[0][f], f, "the listen bus, left");
            Near(M(f) * right, o[1][f], f, "the listen bus, right");
            Near(0, o[2][f], f, "the monitors' other speakers fall silent");
            Near(0, o[5][f], f, "the monitors' other speakers fall silent");
        }

        var master = Bounce(rig.Controller, new(1, 0, 1_280, Path.Combine(dir, "mix.wav")));
        var (data, channels, _) = Wav.Read(Assert.Single(master));
        var order = WaveChannels.Of(SpeakerLayout.Of(SpeakerFormat.Surround51)).Order;
        for (var k = 0; k < channels; k++) {
            if (order[k] == 5) {
                Near(Q(640), data[640 * channels + k], 640, "the other track in the bounce");
            }
        }
    }

    [Fact]
    public void Monitoring_reaches_the_device_through_the_speaker_map_and_never_a_bounce() {
        const EngineChannelFormat f714 = EngineChannelFormat.Surround714;
        EngineTrack[] tracks = [
            TestAudio.Track(1, false, Strip(1f, outputFormat: f714, panner: Surround(0))),
            TestAudio.Track(2, false, Strip(1f, outputFormat: f714, panner: Surround(5)))
        ];
        using var rig = new Rig(
            dir,
            64,
            [Model(1, 0), Model(2, 2)],
            tracks,
            Files,
            Strip(input: f714, outputFormat: f714),
            12,
            new() { BinauralLatency = 7 }
        );
        Assert.Throws<ArgumentException>(() => rig.Controller.SetMonitoring(new(true, null, [])));
        rig.Controller.SetMonitoring(new(true, null, [5, 4], "set.anhrtf"));
        var frames = 3_200;
        var o = rig.Render(frames);
        for (var f = 0; f < frames; f++) {
            Near(M(f - 7), o[5][f], f, "the left ear (even channels) at output 6");
            Near(Q(f - 7), o[4][f], f, "the right ear (odd channels) at output 5");
            Near(0, o[0][f], f, "output 1 carries nothing");
        }

        // The bounce is the master's own 7.1.4; the monitoring render the ears, without the renderer's latency.
        var master = Bounce(rig.Controller, new(1, 0, frames, Path.Combine(dir, "mix.wav")));
        var (data, channels, _) = Wav.Read(Assert.Single(master));
        Assert.Equal(12, channels);
        var order = WaveChannels.Of(SpeakerLayout.Of(SpeakerFormat.Surround714)).Order;
        for (var f = 0; f < frames; f++) {
            for (var k = 0; k < 12; k++) {
                var c = order[k];
                var expected = c == 0 ? M(f) : c == 5 ? Q(f) : 0f;
                Near(expected, data[f * 12 + k], f, $"bounce channel {c}");
            }
        }

        var ears = Bounce(rig.Controller, new(2, 0, frames, Path.Combine(dir, "ears.wav"), Monitoring: true));
        var (earData, earChannels, _) = Wav.Read(Assert.Single(ears));
        Assert.Equal(2, earChannels);
        for (var f = 0; f < frames; f++) {
            Near(M(f), earData[2 * f], f, "monitoring render, left");
            Near(Q(f), earData[2 * f + 1], f, "monitoring render, right");
        }

        // A down-mix to stereo, the speaker map swapping the pair; −1 sends nothing.
        rig.Controller.SetMonitoring(new(false, EngineChannelFormat.Stereo, [1, 0]));
        var down = rig.Render(1_280);
        for (var f = 0; f < 1_280; f++) {
            Near(0.5f * M(f), down[1][f], f, "the down-mix's left at output 2");
            Near(0.5f * Q(f), down[0][f], f, "the down-mix's right at output 1");
        }

        rig.Controller.SetMonitoring(new(false, null, [-1, 3]));
        var mapped = rig.Render(1_280);
        for (var f = 0; f < 1_280; f++) {
            Near(0, mapped[0][f], f, "channel 1 not sent");
            Near(0, mapped[1][f], f, "channel 2 goes to output 4");
            Near(Q(f), mapped[5][f], f, "channel 6 at output 6 (past the map)");
        }
    }

    [Fact]
    public void Stereo_monitoring_swaps_through_the_speaker_map() {
        EngineTrack[] tracks = [TestAudio.Track(1, true, Strip())];
        using var rig = new Rig(dir, 64, [Model(1, 1)], tracks, Files, Strip(), 4);
        rig.Controller.SetMonitoring(new(false, null, [3, 2]));
        var o = rig.Render(1_280);
        for (var f = 0; f < 1_280; f++) {
            var (l, r) = S(f);
            Assert.Equal(l, o[3][f]);
            Assert.Equal(r, o[2][f]);
            Assert.Equal(0, o[0][f]);
        }

        rig.Controller.SetMonitoring(new(false, null, []));
        Assert.Null(rig.Controller.Engine.Monitoring);
    }

    [Fact]
    public void Surround_bounces_are_extensible_in_the_mask_order_or_one_file_per_speaker() {
        const EngineChannelFormat f71 = EngineChannelFormat.Surround71;
        var tracks = Enumerable.Range(0, 8)
            .Select(c => TestAudio.Track(c + 1, false, Strip((c + 1) / 10f, outputFormat: f71, panner: Surround(c))))
            .ToArray();
        using var rig = new Rig(
            dir,
            64,
            [..Enumerable.Range(0, 8).Select(static c => Model(c + 1, 0))],
            tracks,
            Files,
            Strip(input: f71, outputFormat: f71),
            8
        );
        var frames = 2_000;
        var file = Assert.Single(Bounce(rig.Controller, new(1, 0, frames, Path.Combine(dir, "mix.wav"))));
        var (data, channels, mask) = Wav.Read(file);
        Assert.Equal(8, channels);
        Assert.Equal(0x63Fu, mask); // FL FR FC LFE BL BR SL SR
        int[] adm = [0, 1, 2, 3, 6, 7, 4, 5]; // the file's channel k is ADM channel adm[k]: the rears before the sides
        for (var f = 0; f < frames; f++) {
            for (var k = 0; k < 8; k++) {
                Near(M(f) * (adm[k] + 1) / 10f, data[f * 8 + k], f, $"file channel {k}");
            }
        }

        var split = Bounce(rig.Controller, new(2, 0, frames, Path.Combine(dir, "split.wav"), SplitChannels: true));
        var layout = SpeakerLayout.Of(SpeakerFormat.Surround71);
        Assert.Equal(
            [..Enumerable.Range(0, 8).Select(c => Path.Combine(dir, $"split.{layout.Label(c)}.wav"))],
            split
        );
        for (var c = 0; c < 8; c++) {
            var (mono, one, _) = Wav.Read(split[c]);
            Assert.Equal(1, one);
            Assert.Equal(frames, mono.Length);
            Near(M(100) * (c + 1) / 10f, mono[100], 100, layout.Label(c));
        }
    }

    [Fact]
    public void Stems_have_their_destination_format_an_object_its_own_and_a_bed_render_leaves_objects_out() {
        const EngineChannelFormat f714 = EngineChannelFormat.Surround714;
        EngineTrack[] tracks = [
            TestAudio.Track(1, false, Strip(0.5f, outputFormat: f714, panner: Object(3))),
            TestAudio.Track(2, false, Strip(1f, outputFormat: f714, panner: Surround(1)))
        ];
        using var rig = new Rig(
            dir,
            64,
            [Model(1, 0), Model(2, 2)],
            tracks,
            Files,
            Strip(input: f714, outputFormat: f714),
            12
        );
        var frames = 2_000;
        var stems = Bounce(rig.Controller, new(1, 0, frames, Path.Combine(dir, "stems"), [1, 2]));
        var (objectStem, objectChannels, _) = Wav.Read(
            stems.Single(static s => s.EndsWith("1.wav", StringComparison.Ordinal))
        );
        Assert.Equal(1, objectChannels);
        for (var f = 0; f < frames; f++) {
            Near(M(f) * 0.5f, objectStem[f], f, "the object's own signal, post-fader");
        }

        var (bedStem, bedChannels, _) = Wav.Read(
            stems.Single(static s => s.EndsWith("2.wav", StringComparison.Ordinal))
        );
        Assert.Equal(12, bedChannels);
        var order = WaveChannels.Of(SpeakerLayout.Of(SpeakerFormat.Surround714)).Order;
        var k1 = Array.IndexOf(order, 1);
        Near(Q(50), bedStem[50 * 12 + k1], 50, "the bed stem's channel 2");

        var bed = Bounce(rig.Controller, new(2, 0, frames, Path.Combine(dir, "bed.wav"), BedOnly: true));
        var (bedData, _, _) = Wav.Read(Assert.Single(bed));
        var k3 = Array.IndexOf(order, 3);
        for (var f = 0; f < frames; f++) {
            Near(0, bedData[f * 12 + k3], f, "no object in the bed");
            Near(Q(f), bedData[f * 12 + k1], f, "the bed");
        }
    }

    [Fact]
    public void Wave_channel_masks_and_orders() {
        var (m714, o714) = WaveChannels.Of(SpeakerLayout.Of(SpeakerFormat.Surround714));
        Assert.Equal(0x2D63Fu, m714);
        Assert.Equal([0, 1, 2, 3, 6, 7, 4, 5, 8, 9, 10, 11], o714);
        var (m51, o51) = WaveChannels.Of(SpeakerLayout.Of(SpeakerFormat.Surround51));
        Assert.Equal(0x3Fu, m51);
        Assert.Equal([0, 1, 2, 3, 4, 5], o51);
        var (m712, _) = WaveChannels.Of(SpeakerLayout.Of(SpeakerFormat.Surround712));
        Assert.Equal(0x563Fu, m712); // the top middle pair as the top front positions
        var (m916, o916) = WaveChannels.Of(SpeakerLayout.Of(SpeakerFormat.Surround916));
        Assert.Equal(14, System.Numerics.BitOperations.PopCount(m916));
        Assert.Equal([12, 13], o916[^2..]); // the top middle pair has no position: last
        var (mQuad, oQuad) = WaveChannels.Of(SpeakerLayout.Of(SpeakerFormat.Quad));
        Assert.Equal(0x33u, mQuad);
        Assert.Equal([0, 1, 2, 3], oQuad);
    }

    [Fact]
    public void A_surround_file_past_its_threshold_is_rf64_with_a_ds64_chunk() {
        var path = Path.Combine(dir, "big.wav");
        var layout = SpeakerLayout.Of(SpeakerFormat.Surround51);
        using (var writer = new Rendering.FloatWavWriter(path, 6, Rate, layout) { Rf64Threshold = 16 }) {
            writer.Write(new float[6 * 100]);
        }

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RF64"u8.ToArray(), bytes[..4]);
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal("ds64"u8.ToArray(), bytes[12..16]);
        Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(6 * 100 * 4, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(28)));
        Assert.Equal(100, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(36)));

        using (var writer = new Rendering.FloatWavWriter(path, 6, Rate, layout)) {
            writer.Write(new float[6 * 100]);
        }

        bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("JUNK"u8.ToArray(), bytes[12..16]); // the room ds64 would take
    }

    /// <summary>An offline render, waited for (outside the test methods, which the analyzers keep asynchronous).</summary>
    static IReadOnlyList<string> Bounce(EngineController controller, RenderMessage message) =>
        controller.RenderAsync(message, null, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    /// <summary>A float WAV's samples, channels and channel mask (0 for a plain IEEE float file).</summary>
    internal static class Wav {
        public static (float[] Data, int Channels, uint Mask) Read(string path) {
            var bytes = File.ReadAllBytes(path);
            var at = 12;
            var channels = 0;
            uint mask = 0;
            while (at + 8 <= bytes.Length) {
                var id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
                var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
                if (id == "fmt ") {
                    var tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 8));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 10));
                    Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 22)));
                    if (tag == 0xFFFE) {
                        Assert.Equal(22, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 24)));
                        mask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 28));
                        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 32))); // float
                    } else {
                        Assert.Equal(3, tag);
                    }
                } else if (id == "data") {
                    var samples = new float[size / 4];
                    for (var i = 0; i < samples.Length; i++) {
                        samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 8 + 4 * i));
                    }

                    return (samples, channels, mask);
                }

                at += 8 + size + (size & 1);
            }

            throw new InvalidDataException("no data chunk");
        }
    }
}

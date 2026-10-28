using Anode.Engine.Mixing;
using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.Engine.Tests;

/// <summary>
///     The mixer graph rendered offline and compared bit for bit with a reference computed here from the files' known
///     samples (docs/05-phase-4.md, workstream A): routing to buses, aux tracks and device output pairs, pre- and
///     post-fader sends, sidechains (with the one-block rule), plugin delay compensation (null tests) and automation.
/// </summary>
public sealed class MixTests : IDisposable {
    const int Rate = 48_000;
    const int Seconds = 3;
    readonly string dir = TestAudio.TempDirectory();
    readonly string mono;
    readonly string stereo;

    public MixTests() {
        mono = TestAudio.Write24(dir, "mono.wav", 1, Seconds * Rate, 1);
        stereo = TestAudio.Write24(dir, "stereo.wav", 2, Seconds * Rate, 10);
    }

    public void Dispose() => Directory.Delete(dir, true);

    EngineFile[] Files => [new(0, mono, 1, Rate, Seconds * Rate), new(1, stereo, 2, Rate, Seconds * Rate)];

    /// <summary>The mono file's sample at frame <paramref name="n" /> (0 outside it).</summary>
    static float M(long n) => n is >= 0 and < Seconds * Rate ? TestAudio.Sample(n, 1) : 0f;

    static (float L, float R) S(long n) =>
        n is >= 0 and < Seconds * Rate ? (TestAudio.Sample(n, 10), TestAudio.Sample(n, 11)) : (0f, 0f);

    static EngineStrip Strip(
        float gain = 1f,
        float pan = 0f,
        int output = 0,
        EngineInsert[]? inserts = null,
        EngineSend[]? sends = null,
        bool mute = false,
        bool solo = false
    ) =>
        new(gain, pan, mute, solo, inserts, sends, output);

    static EngineTrack Track(int id, bool isStereo, EngineStrip strip, int inputBus = 0, bool monitoring = false) =>
        new(id, -1, isStereo, false, monitoring, strip, InputBus: inputBus);

    static TrackModel Model(int id, int file, long offset = 0, PlaybackEvent[]? events = null) =>
        new(id, events ?? [], [TestAudio.Region(0, Seconds * Rate - offset, file, offset)]);

    sealed class Rig : IDisposable {
        public Rig(
            string dir,
            int block,
            TrackModel[] models,
            EngineTrack[] tracks,
            EngineFile[] files,
            EngineStrip? master = null,
            EngineAutomationTarget[]? targets = null,
            int outputs = 2
        ) {
            Controller = new(new(Rate, block), TestEffects.Registry());
            using var segment = TestAudio.Segment(models, 1);
            Controller.LoadModel(
                TestAudio.Load(segment, 1, true, tracks, files, dir, master) with { AutomationTargets = targets }
            );
            Driver = new(Controller, block, 2, outputs);
        }

        public EngineController Controller { get; }

        public OfflineDriver Driver { get; }

        /// <summary>Plays from 0 and renders every output channel.</summary>
        public float[][] Render(int frames) {
            Driver.PlayFrom(0);
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

            Assert.Equal(0, Controller.Engine.Snapshot.Underruns);
            return channels;
        }

        public void Dispose() {
            Driver.Dispose();
            Controller.Dispose();
        }
    }

    static void Same(float expected, float actual, int frame, string what) {
        if (expected != actual && !(float.IsNaN(expected) && float.IsNaN(actual))) {
            Assert.Fail($"{what}, frame {frame}: expected {expected}, got {actual}");
        }
    }

    static (float L, float R) Coef(float gain, float pan, bool isStereo) {
        var (l, r) = Engine.Strip.PanGains(pan, isStereo);
        return (gain * l, gain * r);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(37)]
    public void Tracks_route_to_buses_aux_tracks_the_master_and_device_output_pairs(int block) {
        EngineTrack[] tracks = [
            Track(1, false, Strip(0.8f, -0.3f, 1)), // to bus 1
            Track(2, true, Strip(0.7f, 0.2f)), // to the master
            Track(3, false, Strip(0.5f, 0f, -2)), // to the device's outputs 3-4
            Track(10, true, Strip(0.9f, 0.4f), 1) // aux: bus 1 to the master (not in the segment)
        ];
        using var rig = new Rig(
            dir,
            block,
            [Model(1, 0), Model(2, 1), Model(3, 0, 1000)],
            tracks,
            Files,
            Strip(0.75f),
            outputs: 4
        );
        var frames = 20_000;
        var o = rig.Render(frames);

        var c1 = Coef(0.8f, -0.3f, false);
        var c2 = Coef(0.7f, 0.2f, true);
        var c3 = Coef(0.5f, 0f, false);
        var ca = Coef(0.9f, 0.4f, true);
        var cm = Coef(0.75f, 0f, true);
        for (var n = 0; n < frames; n++) {
            var s1 = M(n);
            var (bl, br) = (s1 * c1.L, s1 * c1.R);
            var (al, ar) = (bl * ca.L, br * ca.R);
            var (s2l, s2r) = S(n);
            var ml = (s2l * c2.L + al) * cm.L;
            var mr = (s2r * c2.R + ar) * cm.R;
            Same(ml, o[0][n], n, "master left");
            Same(mr, o[1][n], n, "master right");
            var s3 = M(n + 1000);
            Same(s3 * c3.L, o[2][n], n, "pair 2 left");
            Same(s3 * c3.R, o[3][n], n, "pair 2 right");
        }

        Assert.Contains(o[0], static v => v != 0);
    }

    [Fact]
    public void Pre_fader_sends_ignore_the_fader_and_post_fader_sends_follow_it() {
        EngineSend[] sends = [new("pre", 1, 0.8f, 0f, true), new("post", 2, 0.6f, 0.3f, false)];
        EngineTrack[] tracks = [
            Track(1, false, Strip(0.5f, 0f, -1, sends: sends)), // the output goes to outputs 1-2, bypassing the master
            Track(10, true, Strip(1f, 0f, -2), 1), // bus 1 (the pre-fader send) to outputs 3-4
            Track(11, true, Strip(1f, 0f, -3), 2) // bus 2 (the post-fader send) to outputs 5-6
        ];
        using var rig = new Rig(dir, 64, [Model(1, 0), new(10, [], []), new(11, [], [])], tracks, Files, outputs: 6);
        var frames = 9_600;
        var o = rig.Render(frames);
        var direct = Coef(0.5f, 0f, false);
        var pre = Coef(0.8f, 0f, false);
        var post = Coef(0.6f * 0.5f, 0.3f, false);
        for (var n = 0; n < frames; n++) {
            var s = M(n);
            Same(s * direct.L, o[0][n], n, "output");
            Same(s * pre.L, o[2][n], n, "pre-fader send");
            Same(s * pre.R, o[3][n], n, "pre-fader send, right");
            Same(s * post.L, o[4][n], n, "post-fader send");
            Same(s * post.R, o[5][n], n, "post-fader send, right");
        }

        // The fader down: the pre-fader send keeps its level, the post-fader one follows (ramped over one block).
        rig.Controller.SetStrip(1, Strip(0f, 0f, -1, sends: sends));
        rig.Driver.Process();
        rig.Driver.Process();
        Assert.All(rig.Driver.Output(4).ToArray(), static v => Assert.Equal(0f, v));
        Assert.Contains(rig.Driver.Output(2).ToArray(), static v => v != 0);

        // A level change through setSendLevel.
        Assert.True(rig.Controller.SetSendLevel(1, "pre", 0f, 0f));
        Assert.False(rig.Controller.SetSendLevel(1, "nope", 0f, 0f));
        rig.Driver.Process();
        rig.Driver.Process();
        Assert.All(rig.Driver.Output(2).ToArray(), static v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Mute_and_solo_silence_sends_and_a_soloed_track_keeps_its_aux_audible() {
        EngineSend[] sends = [new("s", 1, 1f, 0f, true)];
        EngineTrack[] tracks = [
            Track(1, false, Strip(1f, 0f, 0, sends: sends, solo: true)),
            Track(2, false, Strip(1f, 0f, 0, sends: sends)), // not soloed: silent, its send too
            Track(3, false, Strip(1f, 0f, 0, sends: sends, mute: true)),
            Track(10, true, Strip(1f, 0f, -2), 1) // the aux: implied solo through track 1
        ];
        using var rig = new Rig(dir, 64, [Model(1, 0), Model(2, 0, 100), Model(3, 0, 200)], tracks, Files, outputs: 4);
        var frames = 4_800;
        var o = rig.Render(frames);
        for (var n = 0; n < frames; n++) {
            Same(M(n), o[0][n], n, "master: only the soloed track");
            Same(M(n), o[2][n], n, "aux: only the soloed track's send");
        }
    }

    [Fact]
    public void A_sidechain_from_a_later_track_or_bus_is_one_block_late_and_from_an_earlier_one_current() {
        const int block = 64;
        EngineTrack[] tracks = [
            Track(0, false, Strip(inserts: [TestEffects.Insert("sc", TestEffects.Sidechain, sidechainBus: 3)])),
            Track(1, false, Strip(inserts: [TestEffects.Insert("sc", TestEffects.Sidechain, sidechainTrack: 2)])),
            Track(2, false, Strip(0.5f, sends: [new("s", 3, 1f, 0f, false)])),
            Track(3, false, Strip(inserts: [TestEffects.Insert("sc", TestEffects.Sidechain, sidechainBus: 3)])),
            Track(4, false, Strip(inserts: [TestEffects.Insert("sc", TestEffects.Sidechain, sidechainTrack: 2)]))
        ];
        using var rig = new Rig(
            dir,
            block,
            [Model(0, 0, 10), Model(1, 0, 20), Model(2, 0, 30), Model(3, 0, 40), Model(4, 0, 50)],
            tracks,
            Files
        );
        var frames = 6_400;
        var o = rig.Render(frames);
        var half = Coef(0.5f, 0f, false);
        for (var n = 0; n < frames; n++) {
            var post2 = M(n + 30) * half.L; // track 2's post-fader signal, and bus 3 (its post-fader send at 1)
            var late = n >= block ? M(n - block + 30) * half.L : 0f;
            var t0 = M(n + 10) + late; // bus 3, before track 2: the previous block
            var t1 = M(n + 20) + late; // track 2, processed after track 1: the previous block
            var t2 = post2;
            var t3 = M(n + 40) + post2; // bus 3, complete: this block
            var t4 = M(n + 50) + post2; // track 2, processed before: this block
            Same(t0 + t1 + t2 + t3 + t4, o[0][n], n, "master");
        }
    }

    [Theory]
    [InlineData("all", true)]
    [InlineData("tracks", true)]
    [InlineData("off", false)]
    public void Delay_compensation_nulls_a_latent_insert_against_its_inverted_parallel_path(string pdc, bool nulls) {
        EngineTrack[] tracks = [
            Track(1, false, Strip(inserts: [TestEffects.Insert("d", TestEffects.Delay, Frames(100))])),
            Track(2, false, Strip(inserts: [TestEffects.Insert("g", TestEffects.Gain, Gain(-1))]))
        ];
        using var rig = new Rig(dir, 64, [Model(1, 0), Model(2, 0)], tracks, Files);
        rig.Controller.SetMixer(pdc, false, 0);
        var o = rig.Render(9_600);
        if (nulls) {
            Assert.All(o[0], static v => Assert.Equal(0f, v));
            Assert.All(o[1], static v => Assert.Equal(0f, v));
        } else {
            Assert.Contains(o[0], static v => v != 0);
        }
    }

    [Fact]
    public void Delay_compensation_aligns_an_aux_path_in_all_mode_but_not_in_tracks_mode() {
        EngineTrack[] tracks = [
            Track(1, false, Strip(output: 2)),
            Track(20, true, Strip(inserts: [TestEffects.Insert("d", TestEffects.Delay, Frames(50))]), 2),
            Track(2, false, Strip(inserts: [TestEffects.Insert("g", TestEffects.Gain, Gain(-1))]))
        ];
        using var rig = new Rig(dir, 64, [Model(1, 0), Model(2, 0)], tracks, Files);
        rig.Controller.SetMixer("all", false, 0);
        var o = rig.Render(4_800);
        Assert.All(o[0], static v => Assert.Equal(0f, v));

        rig.Controller.SetMixer("tracks", false, 0);
        rig.Driver.Send(new(EngineCommandKind.Stop));
        o = rig.Render(4_800);
        Assert.Contains(o[0], static v => v != 0);
    }

    [Fact]
    public void Delay_compensation_delays_a_parallel_path_by_exactly_the_latency_and_low_latency_mode_bypasses_it() {
        EngineTrack[] tracks = [
            Track(
                1,
                false,
                Strip(inserts: [TestEffects.Insert("d", TestEffects.Delay, Frames(100))]),
                monitoring: true
            ),
            Track(2, false, Strip())
        ];
        using var rig = new Rig(dir, 64, [Model(1, 0), Model(2, 0)], tracks, Files);
        var frames = 4_800;
        var o = rig.Render(frames);
        for (var n = 0; n < frames; n++) {
            Same(M(n - 100) + M(n - 100), o[0][n], n, "both paths 100 late");
        }

        // Low-latency mode, limit 1 ms (48 frames): the monitored track's 100-frame insert is bypassed.
        rig.Controller.SetMixer("all", true, 1);
        rig.Driver.Send(new(EngineCommandKind.Stop));
        o = rig.Render(frames);
        for (var n = 0; n < frames; n++) {
            Same(M(n) + M(n), o[0][n], n, "no latency");
        }
    }

    [Fact]
    public void Volume_pan_mute_send_and_parameter_automation_play_back_sample_accurately() {
        const int block = 64;
        EngineAutomationTarget[] targets = [
            new(0, 1, "volume"),
            new(1, 1, "pan"),
            new(2, 1, "mute"),
            new(3, 1, "send", "s"),
            new(4, 1, "insert", "g", "gain"),
            new(5, -1, "volume")
        ];
        PlaybackEvent[] events = [
            Point(0, 0, 0.2f),
            Point(0, 1, -1f),
            Point(0, 2, 0f),
            Point(0, 3, 0f),
            Point(0, 4, 1f),
            Point(1_000, 4, 3f),
            Point(2_000, 1, 1f),
            Point(4_800, 0, 1f),
            Point(4_800, 3, 2f),
            Point(6_000, 2, 1f, true),
            Point(6_500, 2, 0f, true),
            Point(7_000, 4, 0.5f, true),
            Point(9_600, 0, 0.5f, true),
            Point(9_600, 3, 0.25f, true)
        ];
        PlaybackEvent[] masterEvents = [Point(0, 5, 1f, track: -1), Point(8_000, 5, 0.5f, track: -1)];
        EngineTrack[] tracks = [
            Track(
                1,
                false,
                Strip(
                    inserts: [TestEffects.Insert("g", TestEffects.Gain)],
                    sends: [new("s", 1, 1f, 0f, true)]
                )
            ),
            Track(10, true, Strip(output: -2), 1)
        ];
        using var rig = new Rig(
            dir,
            block,
            [Model(1, 0, 0, events), new(-1, masterEvents, [])],
            tracks,
            Files,
            targets: targets,
            outputs: 4
        );
        var frames = 12_000;
        var o = rig.Render(frames);
        for (var n = 0; n < frames; n++) {
            var volume = n < 4_800 ? AutomationLane.Interpolate(0, 0.2f, 4_800, 1f, n) : n < 9_600 ? 1f : 0.5f;
            var pan = n < 2_000 ? AutomationLane.Interpolate(0, -1f, 2_000, 1f, n) : 1f;
            var muted = n is >= 6_000 and < 6_500;
            var send = n < 4_800 ? AutomationLane.Interpolate(0, 0f, 4_800, 2f, n) : n < 9_600 ? 2f : 0.25f;
            var chunk = n / 32 * 32; // parameters are set every 32 frames
            var parameter = chunk < 1_000 ? AutomationLane.Interpolate(0, 1f, 1_000, 3f, chunk)
                : chunk < 7_000 ? 3f
                : 0.5f;
            var master = AutomationLane.Interpolate(0, 1f, 8_000, 0.5f, Math.Min(n, 8_000));
            var x = M(n) * parameter; // after the insert: a stereo signal
            var g = muted ? 0f : volume;
            var (pl, pr) = Engine.Strip.PanGains(pan, true);
            var (ml, mr) = Engine.Strip.PanGains(0f, true);
            Same(x * (g * pl) * (master * ml), o[0][n], n, "master left");
            Same(x * (g * pr) * (master * mr), o[1][n], n, "master right");
            var s = muted ? 0f : send;
            Same(x * s, o[2][n], n, "send");
        }
    }

    [Fact]
    public void A_control_move_holds_an_automated_target_until_the_next_play() {
        EngineAutomationTarget[] targets = [new(0, 1, "volume")];
        PlaybackEvent[] events = [Point(0, 0, 0f)];
        using var rig = new Rig(dir, 64, [Model(1, 0, 0, events)], [Track(1, false, Strip())], Files, targets: targets);
        var o = rig.Render(640);
        Assert.All(o[0], static v => Assert.Equal(0f, v)); // automation at 0

        rig.Controller.SetStrip(1, Strip(0.5f)); // touched: the control wins
        rig.Driver.Process();
        rig.Driver.Process();
        Assert.Contains(rig.Driver.Output(0).ToArray(), static v => v != 0);

        rig.Driver.Send(new(EngineCommandKind.Stop));
        o = rig.Render(640); // a new play: the lane again
        Assert.All(o[0], static v => Assert.Equal(0f, v));
    }

    static PlaybackEvent Point(long position, int target, float value, bool step = false, int track = 1) =>
        new() {
            SamplePos = position,
            Track = track,
            Kind = PlaybackEventKind.Automation,
            Data1 = step ? (byte)1 : (byte)0,
            Value = value,
            Target = target
        };

    static Dictionary<string, double> Frames(int frames) => new() { ["frames"] = frames };

    static Dictionary<string, double> Gain(double gain) => new() { ["gain"] = gain };
}

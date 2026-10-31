using Anode.Engine.Sync;
using Anode.Ipc;

namespace Anode.Engine.Tests;

/// <summary>
///     MIDI Clock and MTC in (docs/09-phase-8.md, "Sync"): the tempo estimator is within 0.5 bpm two beats after the
///     clock starts and two beats after a step change, through jitter; the follower turns Start, Stop, Continue and the
///     Song Position Pointer into transport calls and the estimate into the transport's tempo; and it chases MTC to
///     within a frame, and stops when the timecode stops.
/// </summary>
public sealed class SyncInTests {
    const int Rate = 48_000;

    static long Period(double bpm) => (long)Math.Round(60e9 / (bpm * 24));

    [Theory]
    [InlineData(100.0, 0)]
    [InlineData(100.0, 500_000)]
    [InlineData(60.0, 1_000_000)]
    [InlineData(180.0, 500_000)]
    public void The_tempo_is_within_half_a_bpm_two_beats_in_and_through_a_step(double bpm, long jitterNs) {
        var estimator = new TempoEstimator();
        var random = new Random(5);
        long t = 1_000_000_000;

        long Jitter() => jitterNs == 0 ? 0 : random.NextInt64(-jitterNs, jitterNs + 1);

        for (var i = 0; i < 48; i++) { // two beats are 48 intervals: 49 ticks
            estimator.Tick(t + Jitter());
            Assert.Equal(0, estimator.Tempo);
            t += Period(bpm);
        }

        estimator.Tick(t + Jitter());
        Assert.InRange(estimator.Tempo, bpm - 0.5, bpm + 0.5);
        Assert.True(estimator.Locked);
        var worst = 0.0;
        for (var i = 0; i < 24 * 8; i++) { // eight beats steady
            t += Period(bpm);
            estimator.Tick(t + Jitter());
            worst = Math.Max(worst, Math.Abs(estimator.Tempo - bpm));
        }

        Assert.True(worst < 0.5, $"{worst:0.000} bpm off while steady");

        // A step to a third faster: within half a bpm two beats later, and it stays there.
        var next = bpm * 4 / 3;
        for (var i = 0; i < 48; i++) {
            t += Period(next);
            estimator.Tick(t + Jitter());
        }

        Assert.InRange(estimator.Tempo, next - 0.5, next + 0.5);
        worst = 0;
        for (var i = 0; i < 24 * 8; i++) {
            t += Period(next);
            estimator.Tick(t + Jitter());
            worst = Math.Max(worst, Math.Abs(estimator.Tempo - next));
        }

        Assert.True(worst < 0.5, $"{worst:0.000} bpm off after the step");

        // The clock stops: not locked three ticks later; it comes back and locks again.
        Assert.False(estimator.Check(t + 2 * Period(next)));
        Assert.True(estimator.Check(t + 4 * Period(next)));
        Assert.False(estimator.Locked);
    }

    [Fact]
    public void A_late_tick_does_not_move_the_tempo_far() {
        var estimator = new TempoEstimator();
        long t = 0;
        for (var i = 0; i < 200; i++) {
            estimator.Tick(i == 150 ? t + Period(120) / 3 : t);
            t += Period(120);
        }

        Assert.InRange(estimator.Tempo, 119.5, 120.5);
    }

    /// <summary>A transport that records what the follower asks of it and plays on a clock the test drives.</summary>
    sealed class FakeTransport : ISyncTransport {
        long startNs = -1; // when it rolls from `position`
        long position;
        bool rolling;

        public List<string> Calls { get; } = [];

        public long Now { get; set; }

        /// <summary>How long a play takes to start (the disk priming).</summary>
        public long StartDelayNs { get; set; } = 100_000_000;

        public double Tempo { get; private set; }

        public int SampleRate => Rate;

        public int OutputLatency => 0;

        public EngineSnapshot Snapshot =>
            new() {
                State = rolling && Now >= startNs ? TransportState.Playing : TransportState.Stopped,
                Position = PositionAt(Now),
                HostTimeNs = Now
            };

        public long PositionAt(long time) =>
            rolling && time >= startNs ? position + (long)((time - startNs) * 1e-9 * Rate) : position;

        public void Play(long at) {
            Calls.Add($"play {at}");
            if (at >= 0) {
                position = at;
            }

            rolling = true;
            startNs = Now + StartDelayNs;
        }

        public void Stop() {
            Calls.Add("stop");
            position = PositionAt(Now);
            rolling = false;
        }

        public void Locate(long at) {
            Calls.Add($"locate {at}");
            position = at;
            startNs = Now;
        }

        public void LocateAt(long at, long timeNs) {
            Calls.Add("locateAt");
            position = at + (long)((Now - timeNs) * 1e-9 * Rate); // exactly, as the engine does it
            startNs = Now;
        }

        public void SetTempo(double bpm) => Tempo = bpm;

        public double SampleAt(double quarter) => quarter * Rate / 2; // 120 bpm
    }

    static SetSyncMessage Source(EngineSyncSource source, string? clockIn = null, double offset = 0) =>
        new(source, [], EngineMtcRate.Fps25, offset, false, clockIn, null);

    [Fact]
    public void Clock_in_drives_the_transport_and_its_tempo() {
        var transport = new FakeTransport();
        var follower = new SyncFollower(transport);
        follower.Configure(Source(EngineSyncSource.MidiClock, "in-a"));
        long t = 1_000_000_000;
        follower.Receive("in-a", [0xFA], t);
        for (var i = 0; i < 60; i++) {
            follower.Receive("in-a", [0xF8], t);
            follower.Receive("in-b", [0xF8], t + 1_000_000); // another input: ignored
            t += Period(100);
        }

        Assert.InRange(transport.Tempo, 99.5, 100.5);
        Assert.True(follower.Locked);
        Assert.InRange(follower.ExternalTempo!.Value, 99.5, 100.5);
        follower.Receive("in-a", [0xFC], t);
        transport.Now = t;
        follower.Receive("in-a", [0xF2, 0x10, 0x00], t); // 16 sixteenths: bar 2
        follower.Receive("in-a", [0xFB], t);
        Assert.Equal(["play 0", "stop", "locate 96000", "play 96000"], transport.Calls);
        follower.Poll(t + 10 * Period(100));
        Assert.False(follower.Locked);

        // Another source: the tempo goes back to the map.
        follower.Configure(Source(EngineSyncSource.Internal));
        Assert.Equal(0, transport.Tempo);
        Assert.Null(follower.ExternalTempo);
    }

    [Theory]
    [InlineData(EngineMtcRate.Fps25, 0.0)]
    [InlineData(EngineMtcRate.Fps2997Drop, 5.0)]
    [InlineData(EngineMtcRate.Fps24, 0.0)]
    public void Mtc_in_chases_within_a_frame_and_stops_with_it(EngineMtcRate rate, double offset) {
        var transport = new FakeTransport();
        var follower = new SyncFollower(transport);
        follower.Configure(new(EngineSyncSource.Mtc, [], rate, offset, false, null, null));
        var encoder = new MtcEncoder(rate);
        var events = new SyncMidiEvent[64];
        var frame = 1 / Timecode.FramesPerSecond(rate);
        const double from = 10; // the sender's timecode at t0
        var t0 = 5_000_000_000L;
        var full = new byte[10];
        encoder.FullFrame(from, full);
        transport.Now = t0;
        follower.Receive(null, full, t0);
        Assert.Equal($"locate {(long)Math.Round((Timecode.SecondsOf(Timecode.CountAt(from, rate), rate) - offset) * Rate)}", transport.Calls[0]);
        var worst = 0.0;
        for (var ms = 0; ms < 3000; ms++) {
            var a = from + ms / 1000.0;
            var count = encoder.Process(true, a, a + 0.001, 48, events);
            foreach (var e in events.AsSpan(0, count)) {
                var time = t0 + ms * 1_000_000L + e.Frame * 1_000_000L / 48;
                transport.Now = time;
                follower.Receive(null, [e.Status, e.Data1], time);
            }

            transport.Now = t0 + (ms + 1) * 1_000_000L;
            follower.Poll(transport.Now);
            if (ms >= 1500) { // once it settled: the playhead against the timecode
                var timecode = from + (transport.Now - t0) / 1e9 - offset;
                worst = Math.Max(worst, Math.Abs(transport.PositionAt(transport.Now) / (double)Rate - timecode));
            }
        }

        Assert.True(worst < frame, $"{worst / frame:0.00} frames off");
        Assert.Contains("locateAt", transport.Calls); // it relocated after the start's priming
        Assert.True(follower.Locked);
        Assert.DoesNotContain("stop", transport.Calls);

        // The timecode stops: so does the transport, after two frames.
        transport.Now += (long)(3e9 * frame);
        follower.Poll(transport.Now);
        Assert.Equal("stop", transport.Calls[^1]);
        Assert.False(follower.Locked);
    }
}

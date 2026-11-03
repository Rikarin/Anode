using Anode.Audio;
using Anode.Audio.Native;
using Anode.Engine.Sync;
using Anode.Ipc;

namespace Anode.Engine.Tests;

/// <summary>
///     Ableton Link (docs/09-phase-8.md, "Sync"): two an_link peers in one process find each other and agree on tempo
///     and phase; with the source Link, the engine takes the session's tempo, starts in phase (quantized launch), keeps
///     its beat on Link's at the output's host time (its MIDI Clock lands on the session's beats), and its own tempo
///     changes reach the session. Skipped where an_link is not built or the network has no multicast (no peers appear).
/// </summary>
public sealed class LinkTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 128;
    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

    static bool WaitFor(Func<bool> condition, int milliseconds = 10_000) {
        var deadline = System.Environment.TickCount64 + milliseconds;
        while (!condition()) {
            if (System.Environment.TickCount64 > deadline) {
                return false;
            }

            Thread.Sleep(20);
        }

        return true;
    }

    static double AppTempo(NativeLink link) {
        lock (link.AppGate) {
            link.CaptureApp();
            return NativeLink.Tempo(link.AppState);
        }
    }

    static void SetTempo(NativeLink link, double bpm) {
        lock (link.AppGate) {
            link.CaptureApp();
            NativeLink.SetTempo(link.AppState, bpm, link.ClockMicros);
            link.CommitApp();
        }
    }

    /// <summary>How far apart two peers' phases at a bar are half a second from now, in ms at <paramref name="bpm" />.</summary>
    static double PhaseError(NativeLink a, NativeLink b, double bpm) {
        var at = a.ClockMicros + 500_000;
        double phaseA, phaseB;
        lock (a.AppGate) {
            a.CaptureApp();
            phaseA = NativeLink.PhaseAtTime(a.AppState, at, 4);
        }

        lock (b.AppGate) {
            b.CaptureApp();
            phaseB = NativeLink.PhaseAtTime(b.AppState, at, 4);
        }

        var error = Math.Abs(phaseA - phaseB);
        return Math.Min(error, 4 - error) * 60_000 / bpm;
    }

    [Fact]
    public void Two_peers_agree_on_tempo_and_phase() {
        Assert.SkipUnless(NativeLink.IsAvailable, "an_link is not built");
        using var a = new NativeLink(120);
        using var b = new NativeLink(90);
        a.Enabled = true;
        b.Enabled = true;
        Assert.SkipUnless(WaitFor(() => a.Peers == 1 && b.Peers == 1), "the Link peers did not find each other");
        SetTempo(a, 133.5);
        // Link carries the tempo as microseconds a beat: equal to a thousandth of a bpm.
        Assert.True(WaitFor(() => Math.Abs(AppTempo(b) - 133.5) < 1e-3), $"b's tempo is {AppTempo(b)}");
        Assert.Equal(133.5, AppTempo(a), 3);
        SetTempo(b, 97);
        Assert.True(WaitFor(() => Math.Abs(AppTempo(a) - 97) < 1e-3));
        // Both peers' phase half a second from now: equal within 0.1 ms once the new timeline has gone round.
        var error = double.MaxValue;
        Assert.True(
            WaitFor(() => (error = PhaseError(a, b, 97)) < 0.1),
            $"the peers' phases differ by {error:0.000} ms"
        );
        Assert.StartsWith("Link 4.1", NativeLink.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void The_engine_follows_the_session_tempo_starts_in_phase_and_keeps_its_clock_on_the_beat() {
        Assert.SkipUnless(NativeLink.IsAvailable, "an_link is not built");
        using var peer = new NativeLink(128);
        peer.Enabled = true;
        using var controller = new EngineController(new(Rate, Block));
        using var driver = new OfflineDriver(controller, Block);
        var now = Realtime.NowNanoseconds();
        controller.Engine.HostClock = () => now;
        controller.SetSync(
            new(
                EngineSyncSource.Link,
                [new("out", true, false, 0)],
                EngineMtcRate.Fps25,
                0,
                false,
                null,
                null
            )
        );
        var link = controller.Link!;
        Assert.SkipUnless(WaitFor(() => peer.Peers == 1 && link.Peers == 1), "the Link peers did not find each other");
        SetTempo(peer, 128);
        Assert.True(WaitFor(() => Math.Abs(AppTempo(link) - 128) < 1e-3));
        var state = controller.SyncState();
        Assert.Equal((EngineSyncSource.Link, true, 1), (state.Source, state.Locked, state.LinkPeers));
        Assert.Equal(128, state.ExternalTempo!.Value, 2);

        // Callbacks on a clock the test drives, from now: Link's timeline is a function of time, so they may run ahead.
        var outputs = new List<OutgoingSync>();

        void Run(int callbacks) {
            for (var i = 0; i < callbacks; i++) {
                driver.Process();
                now += (long)(Block * 1e9 / Rate);
                while (controller.Engine.SyncOut.TryDequeue(out var e)) {
                    outputs.Add(e);
                }
            }
        }

        Run(20);
        Assert.Equal(128, controller.Engine.LinkTempo, 3);
        driver.Send(new(EngineCommandKind.Play));
        Run(8 * Rate / Block); // it starts at the next bar (within 1.9 s at 128), then plays

        Assert.Equal(TransportState.Playing, controller.Engine.Snapshot.State);

        // Each clock against the session's beat at its time: Anode's beat (24 clocks a beat from Start) in phase at a bar.
        var clocks = outputs.Where(static e => e.Kind == SyncMessageKind.Clock).ToList();
        Assert.Equal(0xFA, clocks[0].B0);
        var errors = new List<double>();
        lock (peer.AppGate) {
            peer.CaptureApp();
            var offset = peer.ClockMicros - Realtime.NowNanoseconds() / 1000;
            var beat = 0.0;
            foreach (var tick in clocks.Where(static e => e.B0 == 0xF8)) {
                var phase = NativeLink.PhaseAtTime(peer.AppState, tick.TimeNs / 1000 + offset, 4);
                var delta = (phase - beat % 4 + 6) % 4 - 2;
                errors.Add(delta * 60_000 / 128);
                beat += 1.0 / 24;
            }
        }

        var worst = errors.Max(Math.Abs);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{errors.Count} clocks, worst phase error {worst * 1000:0.0} µs"
        );
        Assert.True(errors.Count > 300, $"{errors.Count} clocks");
        Assert.True(worst < 0.1, $"the engine's beat is {worst:0.000} ms off Link's");

        // Anode's tempo (the model's) goes to the session.
        using var segment = TestAudio.Segment([new(1, [], [])], 2, tempo: [TestAudio.Tempo(0, 0, 100)]);
        controller.LoadModel(TestAudio.Load(segment, 2, true, [TestAudio.Track(1)], [], dir));
        Run(4);
        Assert.True(WaitFor(() => Math.Abs(AppTempo(peer) - 100) < 1e-3), $"the session's tempo is {AppTempo(peer)}");

        // Source back to internal: the peer leaves the session.
        controller.SetSync(new(EngineSyncSource.Internal, [], EngineMtcRate.Fps25, 0, false, null, null));
        Assert.Null(controller.Link);
        Assert.True(WaitFor(() => peer.Peers == 0));
    }
}

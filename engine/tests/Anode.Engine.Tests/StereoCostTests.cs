using Anode.Dsp.Effects.Dynamics;
using Anode.Dsp.Effects.Eq;
using Anode.Ipc;
using Anode.Ipc.Segments;
using System.Diagnostics;

namespace Anode.Engine.Tests;

/// <summary>
///     What the stereo path costs the audio callback (docs/11-phase-10.md, workstream A: "existing stereo paths unchanged
///     in cost"): 64 audio tracks (half mono, half stereo) with volume and pan automation, a pre- and a post-fader send
///     each to four buses read by aux tracks, at 64 frames; once as plain mixing (the engine's own work), once with a
///     Channel EQ on every track, a Compressor on every aux and a Limiter on the master. Reported (the figures at
///     52f2b5d and after the multichannel graph are recorded in the phase report), with a loose bound.
/// </summary>
public sealed class StereoCostTests : IDisposable {
    const int Rate = 48_000;
    const int Block = 64;
    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

    static PlaybackEvent Automation(long position, int target, float value, int track) =>
        new() {
            SamplePos = position,
            Track = track,
            Kind = PlaybackEventKind.Automation,
            Value = value,
            Target = target
        };

    (double Mean, double P99) Measure(bool effects) {
        var run = Guid.NewGuid().ToString("n")[..8];
        var mono = TestAudio.Write24(dir, $"mono-{run}.wav", 1, 4 * Rate, 1);
        var stereo = TestAudio.Write24(dir, $"stereo-{run}.wav", 2, 4 * Rate, 2);
        var models = new List<TrackModel>();
        var tracks = new List<EngineTrack>();
        var targets = new List<EngineAutomationTarget>();
        for (var t = 0; t < 64; t++) {
            var volume = targets.Count;
            targets.Add(new(volume, t, "volume"));
            targets.Add(new(volume + 1, t, "pan"));
            PlaybackEvent[] events = [
                Automation(0, volume, 0.8f, t), Automation(0, volume + 1, -0.5f, t),
                Automation(4 * Rate, volume, 0.2f, t), Automation(4 * Rate, volume + 1, 0.5f, t)
            ];
            models.Add(new(t, events, [TestAudio.Region(0, 4 * Rate, t & 1, t * 37L)]));
            EngineInsert[]? inserts = effects
                ? [new("eq", ChannelEq.Id, false, new Dictionary<string, double> { ["peak2Gain"] = 3 })]
                : null;
            EngineSend[] sends = [new("a", 1 + t % 4, 0.5f, 0f, true), new("b", 1 + (t + 1) % 4, 0.3f, 0.2f, false)];
            tracks.Add(TestAudio.Track(t, (t & 1) == 1, new(0.7f, 0f, false, false, inserts, sends)));
        }

        for (var b = 1; b <= 4; b++) {
            EngineInsert[]? inserts = effects
                ? [new("comp", Compressor.Id, false, new Dictionary<string, double>())]
                : null;
            tracks.Add(TestAudio.Track(100 + b, true, new(0.8f, 0f, false, false, inserts)) with { InputBus = b });
        }

        EngineInsert[]? masterInserts =
            effects ? [new("lim", Limiter.Id, false, new Dictionary<string, double>())] : null;
        using var segment = TestAudio.Segment(models, 1);
        using var controller = new EngineController(new(Rate, Block));
        controller.LoadModel(
            TestAudio.Load(
                segment,
                1,
                true,
                tracks,
                [new(0, mono, 1, Rate, 4 * Rate), new(1, stereo, 2, Rate, 4 * Rate)],
                dir,
                new(0.9f, 0f, false, false, masterInserts)
            ) with {
                AutomationTargets = targets
            }
        );
        using var driver = new OfflineDriver(controller, Block);
        driver.PlayFrom(0);
        var times = new List<double>();
        for (var i = 0; i < 2_500; i++) {
            if (i % 16 == 0) {
                driver.Pump();
            }

            var start = Stopwatch.GetTimestamp();
            driver.Process(false);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            if (i >= 500) {
                times.Add(elapsed);
            }
        }

        times.Sort();
        return (times.Average(), times[(int)(times.Count * 0.99)]);
    }

    [Fact]
    public void Callback_cost_of_a_stereo_session() {
        // Best of three runs of each: the figures are compared across commits, so the noise floor matters.
        var mix = Enumerable.Range(0, 3).Select(_ => Measure(false)).MinBy(static m => m.Mean);
        var full = Enumerable.Range(0, 3).Select(_ => Measure(true)).MinBy(static m => m.Mean);
        var budget = Block * 1e6 / Rate;
        var message =
            $"stereo session at {Block} frames ({budget:0} µs): 64 tracks, 128 sends, 4 auxes, automation: mixing "
            + $"{mix.Mean:0.0} µs mean, {mix.P99:0.0} µs p99; with EQ, compressors and a limiter {full.Mean:0.0} µs "
            + $"mean, {full.P99:0.0} µs p99";
        TestContext.Current.SendDiagnosticMessage(message);
        Console.WriteLine(message);
        if (System.Environment.GetEnvironmentVariable("ANODE_MEASURE_LOG") is { Length: > 0 } log) {
            File.AppendAllText(log, message + System.Environment.NewLine);
        }

        Assert.True(full.Mean < budget * 4, message);
    }
}

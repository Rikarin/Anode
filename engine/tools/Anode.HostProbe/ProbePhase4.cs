using Anode.Audio;
using Anode.Audio.Native;
using Anode.Engine;
using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.HostProbe;

/// <summary>
///     The smoke test's Phase 4 part: a mix of built-in inserts, a pre-fader send into an aux reverb, a master limiter
///     (delay compensation), structure and level changes while playing, then offline renders of the mix, stems and a
///     freeze, a real-time render, a cancelled render, and the frozen track playing its file.
/// </summary>
static class Phase4 {
    public static async Task RunAsync(ProbeLink link, SharedSegment region, int rate, string dir, string tone) {
        Console.WriteLine("phase 4: inserts, sends, aux, master limiter, renders, freeze");
        EngineInsert Insert(string id, string plugin, Dictionary<string, double>? parameters = null, bool bypassed = false) =>
            new(id, plugin, bypassed, parameters ?? []);

        EngineStrip track = new(
            0.8f,
            0f,
            false,
            false,
            [Insert("eq", "anode.channel-eq"), Insert("comp", "anode.compressor"), Insert("g", "anode.gain")],
            [new("s", 1, 0.5f, 0f, true)]
        );
        EngineStrip aux = new(0.7f, 0f, false, false, [Insert("verb", "anode.reverb")]);
        EngineStrip master = new(0.5f, 0f, false, false, [Insert("lim", "anode.limiter")]);
        using (var segment = SegmentWriter.Write(
                   [new(1, [], [new() { Start = 0, Length = 20L * rate, FileId = 0, Gain = 1f }]), new(10, [], [])],
                   4,
                   20L * rate,
                   $"an-probe{Environment.ProcessId % 10000}",
                   4,
                   true
               )) {
            var loaded = await link.Request(
                new LoadModelMessage(
                    new(segment.Name, segment.Length, 4),
                    true,
                    [new(1, -1, false, false, false, track), new(10, -1, true, false, false, aux, InputBus: 1)],
                    [new(0, tone, 1, rate, 20L * rate)],
                    master,
                    Path.Combine(dir, "Audio Files")
                )
            );
            Smoke.Check(loaded is ModelLoadedMessage { Version: 4 }, $"loadModel with a mix answered {loaded}");
        }

        Smoke.Check(!link.Pushed.OfType<PluginProblemMessage>().Any(), "every built-in insert loaded");
        await link.Send(new SetMixerMessage("all", false, 3));
        await link.Send(new TransportMessage(TransportAction.Play, 0));
        await Task.Delay(1000, link.Token);
        var playing = Smoke.Read(region, out var meters);
        Smoke.Report("mix", playing, meters);
        Smoke.Check(meters[1] is { TrackId: 10, PeakLeft: > 0.01f }, "the aux reverb sounds from the send");
        Smoke.Check(meters[2] is { TrackId: -1, PeakLeft: > 0.01f }, "the master sounds through its limiter");
        await link.Send(new SetChannelMessage(1, track with { Inserts = [Insert("eq", "anode.channel-eq", bypassed: true), ..track.Inserts!.Skip(1)] }));
        await link.Send(new SetInsertParameterMessage(1, "g", "gain", -6));
        await link.Send(new SetSendLevelMessage(1, "s", 0.25f, 0f));
        await link.Send(new SetStripMessage(1, track with { Gain = 0.6f }));
        await Task.Delay(300, link.Token);
        Smoke.Check(Smoke.Read(region, out _).Underruns == playing.Underruns, "no underruns through the changes");
        await link.Send(new TransportMessage(TransportAction.Stop));

        var bounces = Path.Combine(dir, "Bounces");
        var mix = await Render(link, new(1, 0, 2L * rate, Path.Combine(bounces, "mix.wav")));
        Smoke.Check(mix is { Error: null, Files: [var file] } && new FileInfo(file).Length == 58 + 2L * rate * 8, $"the mix renders offline: {mix}");
        var stems = await Render(link, new(2, 0, rate, Path.Combine(bounces, "stems"), [1, 10]));
        Smoke.Check(stems is { Error: null, Files.Count: 2 }, $"stems render: {stems}");
        var freeze = await Render(link, new(3, 0, 3L * rate, Path.Combine(dir, "Freeze"), [1], true));
        Smoke.Check(freeze is { Error: null, Files: [_] }, $"a freeze renders: {freeze}");
        var realtime = await Render(link, new(4, 0, rate / 2, Path.Combine(bounces, "realtime.wav"), Realtime: true));
        Smoke.Check(realtime is { Error: null, Files: [_] }, $"a real-time render: {realtime}");

        await link.Write(new RenderMessage(5, 0, 600L * rate, Path.Combine(bounces, "long.wav")));
        await Task.Delay(200, link.Token);
        await link.Write(new CancelRenderMessage(5));
        var cancelled = await Finished(link, 5);
        Smoke.Check(cancelled is { Error: "cancelled" } && !File.Exists(Path.Combine(bounces, "long.wav")), $"a render cancels: {cancelled}");
        Console.WriteLine(
            $"  renders: progress messages {link.Pushed.OfType<RenderProgressMessage>().Count()}, mix {(mix is { Files: [var m] } ? m : "none")}"
        );

        // The frozen track plays its file.
        if (freeze?.Files is [var frozen]) {
            using var segment = SegmentWriter.Write([new(1, [], [])], 5, 20L * rate, $"an-probe{Environment.ProcessId % 10000}", 5, true);
            var loaded = await link.Request(
                new LoadModelMessage(new(segment.Name, segment.Length, 5), false, [new(1, -1, false, false, false, track, FreezeFile: frozen)], [], master, Path.Combine(dir, "Audio Files"))
            );
            Smoke.Check(loaded is ModelLoadedMessage { Version: 5 }, $"loadModel with a frozen track answered {loaded}");
            await link.Send(new TransportMessage(TransportAction.Play, 0));
            await Task.Delay(700, link.Token);
            var frozenPlaying = Smoke.Read(region, out meters);
            Smoke.Report("frozen", frozenPlaying, meters);
            Smoke.Check(meters[0] is { TrackId: 1, PeakLeft: > 0.01f }, "the frozen track plays its file");
            await link.Send(new TransportMessage(TransportAction.Stop));
        }
    }

    static async Task<RenderFinishedMessage?> Render(ProbeLink link, RenderMessage message) {
        await link.Write(message);
        return await Finished(link, message.JobId);
    }

    static async Task<RenderFinishedMessage?> Finished(ProbeLink link, int job) {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline) {
            if (link.Pushed.OfType<RenderFinishedMessage>().FirstOrDefault(f => f.JobId == job) is { } done) {
                return done;
            }

            await link.Request(new PingMessage(job));
            await Task.Delay(20, link.Token);
        }

        return null;
    }
}

/// <summary>
///     The Phase 4 callback cost (docs/05-phase-4.md, workstream A): <c>tracks</c> mono tracks, each with 4 built-in
///     inserts (Channel EQ, Compressor, Chorus or Stereo Delay, Gain) and 2 sends, 4 aux buses (Reverb, Channel EQ),
///     the master with Channel EQ and Limiter (88 frames of latency: delay compensation on every path), and volume
///     automation on every fourth track; on the null clock or a device (muted).
/// </summary>
static class MixBench {
    public static async Task<int> RunAsync(bool useNull, string? device, int tracks, int seconds, int buffer) {
        IAudioDevice audio = useNull ? new NullAudioDevice(48_000, buffer) : NativeAudioSession.Open(device, 48_000, buffer, 0);
        if (audio is NativeAudioSession session) {
            session.OutputGain = 0f; // measured, not heard
        }

        var rate = audio.SampleRate;
        var dir = Path.Combine(Path.GetTempPath(), "anode-bench");
        var file = Media.Tone(dir, $"bench-{rate}.wav", 330, 120, rate);
        var models = new List<TrackModel>();
        var list = new List<EngineTrack>();
        var targets = new List<EngineAutomationTarget>();
        for (var t = 0; t < tracks; t++) {
            EngineInsert[] inserts = [
                new("eq", "anode.channel-eq", false, new Dictionary<string, double>()),
                new("comp", "anode.compressor", false, new Dictionary<string, double>()),
                new("mod", t % 2 == 0 ? "anode.chorus" : "anode.stereo-delay", false, new Dictionary<string, double>()),
                new("gain", "anode.gain", false, new Dictionary<string, double>())
            ];
            EngineSend[] sends = [new("a", 1 + t % 4, 0.3f, 0f, false), new("b", 1 + (t + 1) % 4, 0.2f, 0f, true)];
            list.Add(new(t, -1, false, false, false, new(0.5f, (t % 5 - 2) / 4f, false, false, inserts, sends)));
            PlaybackEvent[] events = [];
            if (t % 4 == 0) {
                var index = targets.Count;
                targets.Add(new(index, t, "volume"));
                events = [
                    ..Enumerable.Range(0, 60).Select(p => new PlaybackEvent {
                            SamplePos = p * (long)rate,
                            Track = t,
                            Kind = PlaybackEventKind.Automation,
                            Value = p % 2 == 0 ? 0.3f : 0.8f,
                            Target = index
                        }
                    )
                ];
            }

            models.Add(new(t, events, [new() { Start = 0, Length = 60L * rate, FileOffset = t * 997L, FileId = 0, Gain = 0.5f }]));
        }

        for (var b = 1; b <= 4; b++) {
            list.Add(
                new(1000 + b, -1, true, false, false, new(0.7f, 0f, false, false, [
                    new("verb", "anode.reverb", false, new Dictionary<string, double>()),
                    new("eq", "anode.channel-eq", false, new Dictionary<string, double>())
                ]), InputBus: b)
            );
            models.Add(new(1000 + b, [], []));
        }

        using var controller = new EngineController(new(rate, audio.BufferFrames));
        using (var segment = SegmentWriter.Write(models, 1, 60L * rate, "an-mixbench", Environment.ProcessId, true)) {
            controller.LoadModel(
                new(
                    new(segment.Name, segment.Length, 1),
                    true,
                    list,
                    [new(0, file, 1, rate, 120L * rate)],
                    new(0.8f, 0f, false, false, [
                        new("eq", "anode.channel-eq", false, new Dictionary<string, double>()),
                        new("lim", "anode.limiter", false, new Dictionary<string, double>())
                    ]),
                    dir,
                    targets
                )
            );
        }

        controller.Start();
        var timed = new Bench.TimedProcessor(controller.Engine, audio.BufferFrames, rate);
        audio.Start(timed);
        await controller.PlayAsync(rate);
        Thread.Sleep(2000); // tiered compilation settles
        timed.Reset();
        (audio as NativeAudioSession)?.ResetTiming();
        var gcs = GC.CollectionCount(0);
        var overloads = audio.Overloads;
        await Task.Delay(seconds * 1000);
        var (p50, p99, max, count, allocated) = timed.Summary();
        var period = 1e6 * audio.BufferFrames / rate;
        Console.WriteLine(
            $"{audio.Backend}, {rate} Hz, {audio.BufferFrames} frames ({period:0} µs): {tracks} tracks × (4 built-in inserts + 2 sends), "
            + $"4 aux buses × (reverb + EQ), master EQ + limiter (PDC {controller.Engine.Compensation}), {seconds} s"
        );
        Console.WriteLine(
            $"callback (managed) p50 {p50:0.0} µs ({p50 / period:P1}), p99 {p99:0.0} µs ({p99 / period:P1}), max {max:0.0} µs ({max / period:P1}), "
            + $"{count} callbacks, {allocated} B allocated on the audio thread"
        );
        if (audio is NativeAudioSession s) {
            var timing = s.Timing();
            Console.WriteLine(
                $"callback (native, around the call) p99 ≤ {timing.P99Percent}% , max {timing.MaxPercent:0.0}% of the period, over budget {timing.OverBudget}"
            );
        }

        var snapshot = controller.Engine.Snapshot;
        Console.WriteLine($"overloads {audio.Overloads - overloads}, underruns {snapshot.Underruns}, gen0 GCs {GC.CollectionCount(0) - gcs}");
        audio.Stop();
        audio.Dispose();
        return 0;
    }
}

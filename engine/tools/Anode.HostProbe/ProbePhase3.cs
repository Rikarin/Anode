using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.HostProbe;

/// <summary>
///     The smoke test's Phase 3 part: a curved crossfade playing through a cycle, Flashback Capture of a looped
///     performance, cycle recording into one file with passes, autopunch, and the preview channel.
/// </summary>
static class Phase3 {
    public static async Task RunAsync(
        ProbeLink link,
        SharedSegment region,
        int rate,
        string dir,
        EngineStrip unity,
        string tone,
        string high
    ) {
        Console.WriteLine("phase 3: crossfades, flashback, cycle recording, autopunch, preview");
        AudioPlaylistEntry[] regions = [
            new() {
                Start = 0,
                Length = 3L * rate / 2,
                FileId = 0,
                Gain = 1f,
                FadeOut = rate / 2,
                FadeOutCurve = 0.5f
            },
            new() {
                Start = rate,
                Length = 10L * rate,
                FileId = 1,
                FileOffset = rate,
                Gain = 0.5f,
                FadeIn = rate / 2,
                FadeInCurve = -0.5f
            }
        ];
        using (var segment = SegmentWriter.Write(
                   [new(1, [], regions), new(3, [], [])],
                   3,
                   20L * rate,
                   $"an-probe{Environment.ProcessId % 10000}",
                   3,
                   true
               )) {
            var loaded = await link.Request(
                new LoadModelMessage(
                    new(segment.Name, segment.Length, 3),
                    true,
                    [new(1, -1, false, false, false, unity), new(3, 0, false, true, false, unity)],
                    [new(0, tone, 1, rate, 20L * rate), new(1, high, 1, rate, 20L * rate)],
                    new(0.5f, 0f, false, false),
                    Path.Combine(dir, "Audio Files")
                )
            );
            Smoke.Check(loaded is ModelLoadedMessage { Version: 3 }, $"loadModel with a crossfade answered {loaded}");
        }

        await link.Send(new SetMetronomeMessage(false, 0.3f, false));
        await link.Send(new SetCycleMessage(true, rate, 2L * rate)); // the crossfade runs from 1 s to 1.5 s
        await link.Send(new TransportMessage(TransportAction.Play, rate / 2));
        await Task.Delay(2000, link.Token); // past the wrap at 2 s by more than a device's latency
        var playing = Smoke.Read(region, out var meters);
        Smoke.Report("crossfade", playing, meters);
        Smoke.Check(meters[0].TrackId == 1 && meters[0].PeakLeft > 0.05f, "the crossfading track sounds");
        Smoke.Check(playing.Underruns == 0, "no underruns through the crossfade and the wraps");

        var flashback = await link.Request(new FlashbackCaptureMessage());
        if (flashback is RecordingFinishedMessage { Flashback: true, Takes: [var captured] }) {
            Console.WriteLine(
                $"  flashback: track {captured.TrackId} from {captured.StartSample}, {captured.Frames} frames, "
                + $"{captured.Passes?.Count ?? 1} passes"
            );
            Smoke.Check(
                captured is { TrackId: 3, Passes.Count: >= 2 },
                "flashback captured the looped input as passes"
            );
            Smoke.Check(File.Exists(captured.Path), "the flashback take is on disk");
        } else {
            Smoke.Check(false, $"flashbackCapture answers recordingFinished with one take, got {flashback}");
        }

        await link.Send(new TransportMessage(TransportAction.Stop));
        await link.Write(new TransportMessage(TransportAction.Record, rate));
        await Task.Delay(2600, link.Token);
        var cycled = await link.Request(new TransportMessage(TransportAction.Stop));
        if (cycled is RecordingFinishedMessage { Flashback: false, Takes: [var take] }) {
            Console.WriteLine(
                $"  cycle take: {take.Frames} frames, passes "
                + string.Join(
                    ", ",
                    (take.Passes ?? []).Select(static p => $"{p.StartSample}+{p.Frames}@{p.FileOffset}")
                )
            );
            Smoke.Check(
                take.Passes is { Count: >= 2 } passes && passes.Skip(1).All(p => p.StartSample == rate),
                "cycle recording wrote one file of several passes"
            );
        } else {
            Smoke.Check(false, $"recordingFinished with one take, got {cycled}");
        }

        await link.Send(new SetCycleMessage(false, rate, 2L * rate));
        await link.Send(new SetPunchMessage(true, rate * 5L / 4, rate * 3L / 2));
        await link.Write(new TransportMessage(TransportAction.Record, rate));
        await Task.Delay(1200, link.Token);
        var punched = await link.Request(new TransportMessage(TransportAction.Stop));
        await link.Send(new SetPunchMessage(false, 0, 0));
        Smoke.Check(
            punched is RecordingFinishedMessage { Takes: [{ Passes: null } p] }
            && p.StartSample == rate * 5L / 4
            && p.Frames == rate / 4,
            $"autopunch kept exactly the punch range: {(punched as RecordingFinishedMessage)?.Takes.ElementAtOrDefault(0)}"
        );

        await link.Send(new PreviewFileMessage(tone, 0.5f));
        await Task.Delay(300, link.Token);
        var previewing = Smoke.Read(region, out meters);
        Smoke.Report("preview", previewing, meters);
        Smoke.Check(
            previewing.State == Ipc.Telemetry.TelemetryTransportState.Stopped
            && meters[previewing.TrackCount].PeakLeft > 0.05f,
            "the preview sounds on the master while stopped"
        );
        await link.Send(new PreviewFileMessage(null, 0f));
        var bad = await link.Request(new PreviewFileMessage(Path.Combine(dir, "missing.wav"), 1f));
        Smoke.Check(bad is ErrorMessage { Request: "previewFile" }, "a missing preview file is an error");
    }
}

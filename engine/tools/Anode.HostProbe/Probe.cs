using Anode.Audio;
using Anode.Audio.Native;
using Anode.Engine;
using Anode.Engine.Plugins;
using Anode.Engine.Recording;
using Anode.Ipc;
using Anode.Ipc.Segments;
using Anode.Ipc.Telemetry;
using System.Diagnostics;
using System.Net.Sockets;

namespace Anode.HostProbe;

static class Media {
    /// <summary>A mono 24-bit file of a sine at −12 dBFS.</summary>
    public static string Tone(string dir, string name, double frequency, int seconds, int rate) {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) {
            return path;
        }

        using var writer = new WavWriter(path, 1, rate);
        var block = new float[4096];
        for (long f = 0; f < (long)seconds * rate; f += block.Length) {
            for (var i = 0; i < block.Length; i++) {
                block[i] = (float)(0.25 * Math.Sin(2 * Math.PI * frequency * (f + i) / rate));
            }

            writer.Write(block);
        }

        return path;
    }

    public static PlaybackEvent[] Clicks(int rate, int seconds) => [
        ..Enumerable.Range(0, seconds * 2)
            .Select(i => new PlaybackEvent {
                    SamplePos = i * (long)rate / 2,
                    Track = TrackEntry.MetronomeTrack,
                    Kind = PlaybackEventKind.Click,
                    Data1 = (byte)(i % 4 == 0 ? 1 : 0),
                    Value = 1f
                }
            )
    ];
}

static class Smoke {
    static int failures;

    static void Check(bool ok, string what) {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        failures += ok ? 0 : 1;
    }

    public static async Task<int> RunAsync(
        string socketPath,
        string dir,
        CancellationToken cancellationToken = default
    ) {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
        await using var stream = new NetworkStream(socket);

        var pushed = new List<IpcMessage>();
        var scanning = false;

        // The next reply; what the host pushes unasked (catalogue, ports, activity) is set aside.
        async Task<IpcMessage?> Reply() {
            while (true) {
                var message = await IpcFraming.ReadAsync(stream, cancellationToken);
                if (message is MidiPortsMessage or MidiActivityMessage
                    || (message is PluginCatalogMessage && !scanning)) {
                    pushed.Add(message);
                    continue;
                }

                return message;
            }
        }

        async Task<IpcMessage?> Request(IpcMessage message) {
            await IpcFraming.WriteAsync(stream, message, cancellationToken);
            return await Reply();
        }

        async Task Send(IpcMessage message) {
            await IpcFraming.WriteAsync(stream, message, cancellationToken);
            Check(await Request(new PingMessage(0)) is PongMessage, $"{message.GetType().Name} accepted");
        }

        var hello = (HelloMessage)(await Request(new HelloMessage("probe")))!;
        Console.WriteLine(
            $"host {hello.Version}: {hello.Backend}, {hello.SampleRate} Hz, {hello.BufferFrames} frames, "
            + $"{hello.InputChannels} in / {hello.OutputChannels} out, telemetry {hello.TelemetryName}"
        );
        var rate = hello.SampleRate;
        using var region = SharedSegment.Open(hello.TelemetryName!, TelemetryLayout.Size);

        var tone = Media.Tone(dir, $"tone-{rate}.wav", 220, 20, rate);
        var high = Media.Tone(dir, $"high-{rate}.wav", 880, 20, rate);
        TrackModel[] models = [
            new(
                1,
                [],
                [
                    new() {
                        Start = 0,
                        Length = 20L * rate,
                        FileId = 0,
                        Gain = 1f,
                        FadeIn = 480,
                        FadeOut = 480
                    }
                ]
            ),
            new(
                2,
                [],
                [
                    new() {
                        Start = rate,
                        Length = 10L * rate,
                        FileId = 1,
                        FileOffset = rate,
                        Gain = 0.5f
                    }
                ]
            ),
            new(3, [], []),
            new(TrackEntry.MetronomeTrack, Media.Clicks(rate, 20), [])
        ];
        EngineStrip unity = new(1f, 0f, false, false);
        using (var segment = SegmentWriter.Write(
                   models,
                   1,
                   20L * rate,
                   $"an-probe{Environment.ProcessId % 10000}",
                   1,
                   true
               )) {
            var loaded = await Request(
                new LoadModelMessage(
                    new(segment.Name, segment.Length, 1),
                    true,
                    [
                        new(1, -1, false, false, false, unity),
                        new(2, -1, true, false, false, new(0.8f, 0.3f, false, false)),
                        new(3, 0, false, true, true, unity)
                    ],
                    [new(0, tone, 1, rate, 20L * rate), new(1, high, 1, rate, 20L * rate)],
                    new(0.5f, 0f, false, false),
                    Path.Combine(dir, "Audio Files")
                )
            );
            Check(loaded is ModelLoadedMessage { Version: 1 }, $"loadModel answered {loaded}");
        }

        await Send(new SetMetronomeMessage(true, 0.3f, true));
        await Send(new TransportMessage(TransportAction.Play, 0));
        var first = Read(region, out _);
        for (var i = 0; i < 100 && first.State != TelemetryTransportState.Playing; i++) {
            Thread.Sleep(10);
            first = Read(region, out _);
        }

        Thread.Sleep(1000);
        var second = Read(region, out var meters);
        var seconds = (second.HostTimeNs - first.HostTimeNs) / 1e9;
        Report("playing", second, meters);
        Check(second.State == TelemetryTransportState.Playing, "state playing");
        Check(
            Math.Abs(second.PositionSamples - first.PositionSamples - seconds * rate) < 4 * hello.BufferFrames,
            $"playhead advanced {second.PositionSamples - first.PositionSamples} frames in {seconds:0.000} s"
        );
        Check(meters[0].PeakLeft > 0.1f && meters[3].PeakLeft > 0.1f, "track and master meters move");

        await Send(new SetCycleMessage(true, rate, 2L * rate));
        var inside = true;
        for (var i = 0; i < 30; i++) {
            Thread.Sleep(50);
            var t = Read(region, out _);
            inside &= t.PositionSamples is >= 0 and < 2 * 48_000 * 4;
            if (i > 20) {
                inside &= t.PositionSamples >= rate && t.PositionSamples < 2L * rate;
            }
        }

        Check(inside, "the playhead loops in the cycle");
        await Send(new SetCycleMessage(false, rate, 2L * rate));
        await Send(new TransportMessage(TransportAction.Stop));
        Thread.Sleep(100);
        Check(Read(region, out _).State == TelemetryTransportState.Stopped, "stopped");

        await Send(new SetStripMessage(1, new(0.5f, -0.5f, false, true)));
        await IpcFraming.WriteAsync(stream, new TransportMessage(TransportAction.Record, 4L * rate), cancellationToken);
        Thread.Sleep(200);
        var counting = Read(region, out _);
        Check(
            counting is { State: TelemetryTransportState.Recording } && counting.PositionSamples == 4L * rate,
            "count-in holds the playhead"
        );
        Thread.Sleep(3000);
        var recording = Read(region, out meters);
        Report("recording", recording, meters);
        Check(recording.PositionSamples > 4L * rate, "recording rolls after the count-in");
        var finished = await Request(new TransportMessage(TransportAction.Stop));
        if (finished is RecordingFinishedMessage { Takes: [var take] }) {
            Console.WriteLine(
                $"  take: track {take.TrackId} {take.Path} from {take.StartSample}, {take.Frames} frames, {take.Channels} ch"
            );
            Check(
                File.Exists(take.Path) && new FileInfo(take.Path).Length == 44 + take.Frames * 3,
                "the take is a complete 24-bit WAV"
            );
            Check(take.Frames > rate, "the take holds about a second or more");
        } else {
            Check(false, $"recordingFinished with one take, got {finished}");
        }

        var error = await Request(new SetStripMessage(99, unity));
        Check(error is ErrorMessage { Request: "setStrip" }, $"unknown track: {error}");

        // ---- Phase 2: catalogue, ports, an instrument track, live MIDI and MIDI recording ----
        Check(pushed.OfType<MidiPortsMessage>().Any(), "midiPorts after the hello");
        foreach (var port in pushed.OfType<MidiPortsMessage>().LastOrDefault()?.Ports ?? []) {
            Console.WriteLine($"  midi port {port.Id} \"{port.Name}\" {(port.Input ? "in" : "out")}");
        }

        scanning = true;
        var catalog = await Request(new ScanPluginsMessage()) as PluginCatalogMessage;
        scanning = false;
        Check(catalog is not null, "scanPlugins answers the catalogue");
        foreach (var plugin in catalog?.Plugins ?? []) {
            Console.WriteLine(
                $"  plugin {plugin.Id} ({plugin.Format}) {plugin.Name}, {plugin.Parameters.Count} parameters"
            );
        }

        var instrument = catalog?.Plugins.FirstOrDefault(static p => p.Id == "org.anode.test-sine")
            ?? catalog?.Plugins.FirstOrDefault(static p => p.IsInstrument);
        if (instrument is null) {
            Console.WriteLine("  (no instrument in the catalogue: build engine/native and set CLAP_PATH to test one)");
        } else {
            await Instruments(instrument);
        }
        var end = Read(region, out _);
        Console.WriteLine(
            $"callbacks {end.Callbacks}, overloads {end.Overloads}, underruns {end.Underruns}, model {end.ModelVersion}"
        );
        Console.WriteLine(failures == 0 ? "PASS" : $"FAIL ({failures})");
        return failures == 0 ? 0 : 1;

        async Task Instruments(EnginePlugin plugin) {
            Console.WriteLine($"instrument track with {plugin.Id}");
            var parameters = plugin.Parameters.ToDictionary(static p => p.Id, static p => p.Default);
            var notes = Enumerable.Range(0, 16)
                .SelectMany(i => new[] {
                        Note(i * (long)rate / 4, 60 + i % 3 * 4, true), Note(i * (long)rate / 4 + rate / 5, 60 + i % 3 * 4, false)
                    }
                )
                .ToArray();
            using (var segment = SegmentWriter.Write(
                       [new(4, notes, []), new(TrackEntry.MetronomeTrack, Media.Clicks(rate, 20), [])],
                       2,
                       20L * rate,
                       $"an-probe{Environment.ProcessId % 10000}",
                       2,
                       true
                   )) {
                var loaded = await Request(
                    new LoadModelMessage(
                        new(segment.Name, segment.Length, 2),
                        true,
                        [new(4, -1, true, true, false, unity, new(plugin.Id, parameters))],
                        [],
                        new(0.5f, 0f, false, false),
                        Path.Combine(dir, "Audio Files")
                    )
                );
                Check(loaded is ModelLoadedMessage { Version: 2 }, $"loadModel with an instrument answered {loaded}");
            }

            await Send(new SetMetronomeMessage(false, 0.3f, false));
            await Send(new TransportMessage(TransportAction.Play, 0));
            Thread.Sleep(1500);
            var playing = Read(region, out var meters);
            Report("instrument", playing, meters);
            Check(meters[0].TrackId == 4 && meters[0].PeakLeft > 0.01f, "the instrument track sounds");
            await Send(new TransportMessage(TransportAction.Stop));
            if (plugin.Parameters.Count > 0) {
                await Send(new SetParameterMessage(4, plugin.Parameters[0].Id, plugin.Parameters[0].Min));
            }

            await IpcFraming.WriteAsync(stream, new TransportMessage(TransportAction.Record, rate), cancellationToken);
            Thread.Sleep(300);
            await Send(new LiveMidiMessage(4, [new(0x90, 72, 100)]));
            Thread.Sleep(250);
            await Send(new LiveMidiMessage(4, [new(0x80, 72, 0)]));
            Thread.Sleep(200);
            var finished = await Request(new TransportMessage(TransportAction.Stop));
            if (finished is RecordingFinishedMessage { MidiTakes: [var take] }) {
                Console.WriteLine(
                    $"  midi take: track {take.TrackId} {take.StartSample}…{take.EndSample}: "
                    + string.Join(", ", take.Events.Select(static e => $"{e.Status:X2} {e.Data1} @{e.Sample}"))
                );
                Check(take is { TrackId: 4, Events.Count: 2 }, "the live notes were recorded");
            } else {
                Check(false, $"recordingFinished with one MIDI take, got {finished}");
            }

            Check(pushed.OfType<MidiActivityMessage>().Any(), "live MIDI shows as activity");
            var states = await Request(new RequestInstrumentStatesMessage());
            Check(
                states is InstrumentStatesMessage { States: [{ TrackId: 4 }] },
                $"instrumentStates: {(states as InstrumentStatesMessage)?.States[0].State?.Length ?? 0} base64 chars"
            );
        }
    }

    static PlaybackEvent Note(long at, int note, bool on) =>
        new() {
            SamplePos = at,
            Track = 4,
            Kind = on ? PlaybackEventKind.NoteOn : PlaybackEventKind.NoteOff,
            Data1 = (byte)note,
            Data2 = (byte)(on ? 100 : 0)
        };

    static void Report(string label, TelemetryHeader h, MeterEntry[] meters) {
        Console.WriteLine(
            $"  [{label}] state {h.State}, position {h.PositionSamples}, callbacks {h.Callbacks}, overloads {h.Overloads}, "
            + $"underruns {h.Underruns}, meters "
            + string.Join(
                " ",
                meters.Take(h.TrackCount + 1).Select(static m => $"{m.TrackId}:{m.PeakLeft:0.00}/{m.PeakRight:0.00}")
            )
        );
    }

    static unsafe TelemetryHeader Read(SharedSegment region, out MeterEntry[] meters) {
        meters = new MeterEntry[TelemetryHeader.MaxTracks + 1];
        return TelemetryWriter.TryRead(region.Pointer, out var header, meters)
            ? header
            : throw new InvalidOperationException("telemetry busy");
    }
}

static class Bench {
    // instruments: instrument tracks besides the audio tracks; instrument: what they play, probe.sine (a managed
    // sine), a built-in id, or a CLAP id found in clapPath (default: CLAP_PATH, else engine/artifacts/native/clap).
    public static async Task<int> RunAsync(
        bool useNull,
        string? device,
        int tracks,
        int seconds,
        int buffer,
        int instruments = 0,
        string instrument = ProbeSine.Id,
        string? clapPath = null,
        CancellationToken cancellationToken = default
    ) {
        IAudioDevice audio = useNull
            ? new NullAudioDevice(48_000, buffer)
            : NativeAudioSession.Open(device, 48_000, buffer, 0);
        if (audio is NativeAudioSession session) {
            session.OutputGain = 0f; // measured, not heard
        }

        var rate = audio.SampleRate;
        var dir = Path.Combine(Path.GetTempPath(), "anode-bench");
        var file = Media.Tone(dir, $"bench-{rate}.wav", 330, 120, rate);
        var models = Enumerable.Range(0, tracks)
            .Select(t => new TrackModel(
                    t,
                    [],
                    [
                        ..Enumerable.Range(0, 30)
                            .Select(r => new AudioPlaylistEntry {
                                    Start = r * 2L * rate,
                                    Length = 2L * rate - 100,
                                    FileOffset = (t * 7919L + r * 31L * rate) % (100L * rate),
                                    FileId = 0,
                                    Gain = 0.5f,
                                    FadeIn = 64,
                                    FadeOut = 64
                                }
                            )
                    ]
                )
            )
            .Concat(Enumerable.Range(0, instruments).Select(i => new TrackModel(1000 + i, Chords(rate, i, 60), [])))
            .Append(new(TrackEntry.MetronomeTrack, Media.Clicks(rate, 60), []))
            .ToArray();
        var plugins = new PluginRegistry { Log = static m => Console.WriteLine($"  {m}") };
        plugins.AddBuiltin(ProbeSine.Info, static () => new ProbeSine());
        if (instruments > 0 && instrument.Contains('.', StringComparison.Ordinal) && instrument != ProbeSine.Id) {
            plugins.ScanClap([clapPath ?? Environment.GetEnvironmentVariable("CLAP_PATH") ?? DefaultClapFolder()]);
        }

        var catalogEntry = plugins.Catalog().FirstOrDefault(p => p.Id == instrument);
        if (instruments > 0 && catalogEntry is null) {
            Console.WriteLine($"no instrument '{instrument}' (catalogue: {string.Join(", ", plugins.Catalog().Select(static p => p.Id))})");
            return 2;
        }

        var parameters = catalogEntry?.Parameters.ToDictionary(static p => p.Id, static p => p.Default) ?? [];
        using var controller = new EngineController(new(rate, audio.BufferFrames), plugins);
        using (var segment = SegmentWriter.Write(models, 1, 60L * rate, "an-bench", Environment.ProcessId, true)) {
            controller.LoadModel(
                new(
                    new(segment.Name, segment.Length, 1),
                    true,
                    [
                        ..Enumerable.Range(0, tracks)
                            .Select(static t => new EngineTrack(
                                    t,
                                    -1,
                                    false,
                                    false,
                                    false,
                                    new(0.8f, t % 3 - 1, false, false)
                                )
                            ),
                        ..Enumerable.Range(0, instruments)
                            .Select(i => new EngineTrack(
                                    1000 + i,
                                    -1,
                                    true,
                                    false,
                                    false,
                                    new(0.5f, 0f, false, false),
                                    new(instrument, parameters)
                                )
                            )
                    ],
                    [new(0, file, 1, rate, 120L * rate)],
                    new(0.5f, 0f, false, false),
                    dir
                )
            );
        }

        controller.Engine.SetMetronome(true, 0.5f);
        controller.Start();
        var timed = new TimedProcessor(controller.Engine, audio.BufferFrames, rate);
        audio.Start(timed);
        controller.SetCycle(true, 10L * rate, 18L * rate);
        await controller.PlayAsync(5L * rate, cancellationToken);
        Thread.Sleep(1000);
        timed.Reset();
        var gcs = GC.CollectionCount(0);
        var overloads = audio.Overloads;
        await Task.Delay(seconds * 1000, cancellationToken);
        var (p50, p99, max, count, allocated) = timed.Summary();
        var period = 1e6 * audio.BufferFrames / rate;
        Console.WriteLine(
            $"{audio.Backend}, {rate} Hz, {audio.BufferFrames} frames ({period:0} µs), {tracks} mono tracks + "
            + $"{instruments} × {(instruments > 0 ? instrument : "no instrument")} (4-note chords) + click, {seconds} s, "
            + "cycle 10-18 s from 5 s"
        );
        Console.WriteLine(
            $"callback (managed) p50 {p50:0.0} µs, p99 {p99:0.0} µs, max {max:0.0} µs ({max / period:P1} of the period), "
            + $"{count} callbacks, {allocated} B allocated on the audio thread"
        );
        if (audio is NativeAudioSession s) {
            var timing = s.Timing();
            Console.WriteLine(
                $"callback (native, around the call) p99 ≤ {timing.P99Percent}% , max {timing.MaxPercent:0.0}% of the period, over budget {timing.OverBudget}"
            );
        }

        var snapshot = controller.Engine.Snapshot;
        Console.WriteLine(
            $"overloads {audio.Overloads - overloads}, underruns {snapshot.Underruns}, gen0 GCs {GC.CollectionCount(0) - gcs}, "
            + $"disk {controller.Streamer.BytesRead / 1e6:0.0} MB in {controller.Streamer.Reads} reads"
        );
        audio.Stop();
        audio.Dispose();
        return 0;
    }

    /// <summary>
    ///     Four-note chords every half second, each held for 0.4 s, on instrument track <paramref name="index" />
    ///     (a different chord per track): four voices sounding most of the time, eight at each overlap.
    /// </summary>
    static PlaybackEvent[] Chords(int rate, int index, int seconds) {
        var events = new List<PlaybackEvent>();
        for (var c = 0; c < seconds * 2; c++) {
            long on = c * (long)rate / 2, off = on + rate * 4 / 10;
            for (var n = 0; n < 4; n++) {
                var note = (byte)(48 + index % 12 + n * 4 + c % 3);
                events.Add(new() { SamplePos = on, Track = 1000 + index, Kind = PlaybackEventKind.NoteOn, Data1 = note, Data2 = 90 });
                events.Add(new() { SamplePos = off, Track = 1000 + index, Kind = PlaybackEventKind.NoteOff, Data1 = note });
            }
        }

        return [..events.OrderBy(static e => e.SamplePos).ThenBy(static e => e.Kind == PlaybackEventKind.NoteOn ? 1 : 0)];
    }

    static string DefaultClapFolder() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "artifacts", "native", "clap"))) {
            dir = dir.Parent;
        }

        return dir is null ? "clap" : Path.Combine(dir.FullName, "artifacts", "native", "clap");
    }

    /// <summary>Times each callback into a histogram of 0.5 µs bins; allocation-free.</summary>
    sealed unsafe class TimedProcessor(IAudioProcessor inner, int frames, int rate) : IAudioProcessor {
        readonly long[] bins = new long[200_000];
        long baseline = -1;
        long allocated;
        volatile bool reset;

        public void Reset() => reset = true;

        public void Process(float** inputs, int inputChannels, float** outputs, int outputChannels, int count) {
            if (reset) {
                Array.Clear(bins);
                baseline = GC.GetAllocatedBytesForCurrentThread();
                reset = false;
            }

            var start = Stopwatch.GetTimestamp();
            inner.Process(inputs, inputChannels, outputs, outputChannels, count);
            var micros = (Stopwatch.GetTimestamp() - start) * 1e6 / Stopwatch.Frequency;
            bins[Math.Min(bins.Length - 1, (int)(micros * 2))]++;
            if (baseline >= 0) {
                Volatile.Write(ref allocated, GC.GetAllocatedBytesForCurrentThread() - baseline);
            }
        }

        public (double P50, double P99, double Max, long Count, long Allocated) Summary() {
            var copy = (long[])bins.Clone();
            var count = copy.Sum();

            double Quantile(double q) {
                long running = 0;
                for (var i = 0; i < copy.Length; i++) {
                    running += copy[i];
                    if (running >= q * count) {
                        return (i + 1) / 2.0;
                    }
                }

                return copy.Length / 2.0;
            }

            var max = 0.0;
            for (var i = copy.Length - 1; i >= 0; i--) {
                if (copy[i] > 0) {
                    max = (i + 1) / 2.0;
                    break;
                }
            }

            _ = frames;
            _ = rate;
            return (Quantile(0.5), Quantile(0.99), max, count, Volatile.Read(ref allocated));
        }
    }
}

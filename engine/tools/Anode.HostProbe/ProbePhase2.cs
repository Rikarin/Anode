using Anode.Ipc;
using Anode.Ipc.Segments;

namespace Anode.HostProbe;

/// <summary>
///     The probe's side of the socket: requests and their replies, with what the host pushes unasked (the catalogue,
///     MIDI ports, activity) set aside in <see cref="Pushed" />.
/// </summary>
sealed class ProbeLink(Stream stream, CancellationToken cancellationToken) {
    public List<IpcMessage> Pushed { get; } = [];

    public CancellationToken Token => cancellationToken;

    public async Task<IpcMessage?> Request(IpcMessage message, bool catalogIsReply = false) {
        await IpcFraming.WriteAsync(stream, message, cancellationToken);
        while (true) {
            var reply = await IpcFraming.ReadAsync(stream, cancellationToken);
            if (reply is MidiPortsMessage or MidiActivityMessage
                || (reply is PluginCatalogMessage && !catalogIsReply)) {
                Pushed.Add(reply);
                continue;
            }

            return reply;
        }
    }

    /// <summary>Sends a message without a reply, and checks the host took it (a ping round trip).</summary>
    public async Task Send(IpcMessage message) {
        await IpcFraming.WriteAsync(stream, message, cancellationToken);
        Smoke.Check(await Request(new PingMessage(0)) is PongMessage, $"{message.GetType().Name} accepted");
    }

    public Task Write(IpcMessage message) => IpcFraming.WriteAsync(stream, message, cancellationToken);
}

/// <summary>
///     The smoke test's Phase 2 part: ports and catalogue, an instrument track (the test CLAP plugin when it is on
///     CLAP_PATH, else the first instrument), a parameter, live MIDI recorded into a MIDI take, and the states.
/// </summary>
static class Phase2 {
    const string TestClap = "org.anode.test-sine";

    public static async Task RunAsync(ProbeLink link, SharedSegment region, int rate, string dir, EngineStrip unity) {
        Smoke.Check(link.Pushed.OfType<MidiPortsMessage>().Any(), "midiPorts after the hello");
        foreach (var port in link.Pushed.OfType<MidiPortsMessage>().LastOrDefault()?.Ports ?? []) {
            Console.WriteLine($"  midi port {port.Id} \"{port.Name}\" {(port.Input ? "in" : "out")}");
        }

        var catalog = await link.Request(new ScanPluginsMessage(), true) as PluginCatalogMessage;
        Smoke.Check(catalog is not null, "scanPlugins answers the catalogue");
        var plugins = catalog?.Plugins ?? [];
        foreach (var plugin in plugins) {
            Console.WriteLine(
                $"  plugin {plugin.Id} ({plugin.Format}) {plugin.Name}, {plugin.Parameters.Count} parameters"
            );
        }

        var instrument = plugins.FirstOrDefault(static p => p.Id == TestClap)
            ?? plugins.FirstOrDefault(static p => p.IsInstrument);
        if (instrument is null) {
            Console.WriteLine("  (no instrument in the catalogue: build engine/native and set CLAP_PATH to test one)");
            return;
        }

        await Instruments(link, instrument, region, rate, dir, unity);
    }

    static async Task Instruments(
        ProbeLink link,
        EnginePlugin plugin,
        SharedSegment region,
        int rate,
        string dir,
        EngineStrip unity
    ) {
        Console.WriteLine($"instrument track with {plugin.Id}");
        var parameters = plugin.Parameters.ToDictionary(static p => p.Id, static p => p.Default);
        var notes = Enumerable.Range(0, 16)
            .SelectMany(i => new[] {
                    // Legato (each note ends where the next starts), so the meter never reads a gap.
                    Note(i * (long)rate / 4, 60 + i % 3 * 4, true),
                    Note((i + 1) * (long)rate / 4, 60 + i % 3 * 4, false)
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
            var loaded = await link.Request(
                new LoadModelMessage(
                    new(segment.Name, segment.Length, 2),
                    true,
                    [new(4, -1, true, true, false, unity, new(plugin.Id, parameters))],
                    [],
                    new(0.5f, 0f, false, false),
                    Path.Combine(dir, "Audio Files")
                )
            );
            Smoke.Check(loaded is ModelLoadedMessage { Version: 2 }, $"loadModel with an instrument answered {loaded}");
        }

        await link.Send(new SetMetronomeMessage(false, 0.3f, false));
        await link.Send(new TransportMessage(TransportAction.Play, 0));
        await Task.Delay(1500, link.Token);
        var playing = Smoke.Read(region, out var meters);
        Smoke.Report("instrument", playing, meters);
        Smoke.Check(meters[0].TrackId == 4 && meters[0].PeakLeft > 0.01f, "the instrument track sounds");
        await link.Send(new TransportMessage(TransportAction.Stop));
        if (plugin.Parameters.Count > 0) {
            await link.Send(new SetParameterMessage(4, plugin.Parameters[0].Id, plugin.Parameters[0].Min));
        }

        await link.Write(new TransportMessage(TransportAction.Record, rate));
        await Task.Delay(300, link.Token);
        await link.Send(new LiveMidiMessage(4, [new(0x90, 72, 100)]));
        await Task.Delay(250, link.Token);
        await link.Send(new LiveMidiMessage(4, [new(0x80, 72, 0)]));
        await Task.Delay(200, link.Token);
        var finished = await link.Request(new TransportMessage(TransportAction.Stop));
        if (finished is RecordingFinishedMessage { MidiTakes: [var take] }) {
            Console.WriteLine(
                $"  midi take: track {take.TrackId} {take.StartSample}…{take.EndSample}: "
                + string.Join(", ", take.Events.Select(static e => $"{e.Status:X2} {e.Data1} @{e.Sample}"))
            );
            Smoke.Check(take is { TrackId: 4, Events.Count: 2 }, "the live notes were recorded");
        } else {
            Smoke.Check(false, $"recordingFinished with one MIDI take, got {finished}");
        }

        Smoke.Check(link.Pushed.OfType<MidiActivityMessage>().Any(), "live MIDI shows as activity");
        var states = await link.Request(new RequestInstrumentStatesMessage()) as InstrumentStatesMessage;
        Smoke.Check(
            states is { States: [{ TrackId: 4 }] },
            $"instrumentStates: {states?.States[0].State?.Length ?? 0} base64 chars"
        );
    }

    static PlaybackEvent Note(long at, int note, bool on) =>
        new() {
            SamplePos = at,
            Track = 4,
            Kind = on ? PlaybackEventKind.NoteOn : PlaybackEventKind.NoteOff,
            Data1 = (byte)note,
            Data2 = (byte)(on ? 100 : 0)
        };
}

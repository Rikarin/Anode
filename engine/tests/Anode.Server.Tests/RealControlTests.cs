using Anode.Contracts;
using Anode.Control.Osc;
using Anode.Ipc.Telemetry;
using Anode.Server.Control;
using Anode.Server.Engine;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Anode.Server.Tests;

/// <summary>
///     The control service with workstream D's real protocols (<see cref="ControlProtocols" />, no stand-ins): a Mackie
///     Control and a HUI session through <see cref="FakeHost" />, held to D's byte vectors
///     (<c>McuVectors.json</c>, <c>HuiVectors.json</c>: the handshake and the ping, byte for byte) and to the protocol as
///     docs/control/README.md specifies it (faders as 14-bit pitch bend or HUI's CC pairs, the scribble strip, touch,
///     transport buttons on press only, banking, the timecode display); and an OSC round trip over UDP with D's codec
///     and address space.
/// </summary>
public sealed class RealControlTests : IAsyncDisposable {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly ServerFixture server = new(configure: static s => s.AddSingleton<IControlProtocols>(new ControlProtocols()));
    readonly HttpClient client;

    public RealControlTests() {
        client = server.CreateClient();
    }

    public async ValueTask DisposeAsync() {
        client.Dispose();
        await server.DisposeAsync();
    }

    FakeHost Host => server.Services.GetRequiredService<EngineConnection>().InProcessHost!;

    async Task<IReadOnlyList<string>> StartAsync(int tracks) {
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(client)).Engine.State == EngineState.InProcess
        );
        await client.CreateProjectAsync();
        await client.EditAsync([..Enumerable.Range(1, tracks).Select(static n => new AddTrackCommand($"T{n}"))]);
        return [..(await client.ProjectAsync()).Tracks.Select(static t => t.Id)];
    }

    async Task<ControlSurface> AddAsync(ControlSurfaceKind kind, string input, string output) {
        var response = await client.PostAsJsonAsync(
            "/api/v1/control/surfaces",
            new(kind, input, output),
            ContractsJsonContext.Default.AddSurfaceRequest,
            Token
        );
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ControlSurface, Token))!;
    }

    async Task<SurfaceStatus> StatusAsync(string id) =>
        (await client.GetFromJsonAsync(
            "/api/v1/control/surfaces/status",
            ContractsJsonContext.Default.IReadOnlyListSurfaceStatus,
            Token
        ))!.Single(s => s.Id == id);

    async Task<double> VolumeAsync(string trackId) =>
        (await client.ProjectAsync()).Tracks.Single(t => t.Id == trackId).Strip.VolumeDb;

    /// <summary>Every whole message sent to <paramref name="port" />, split out of the host's sends.</summary>
    IReadOnlyList<byte[]> SentTo(string port) => [..Host.SentMidi.Where(m => m.Port == port).SelectMany(static m => Split(m.Data))];

    bool Sent(string port, byte[] message) => SentTo(port).Any(m => m.AsSpan().SequenceEqual(message));

    /// <summary>A step of a session in D's vector file.</summary>
    static JsonElement Step(string file, string session, int step) {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Vectors", file));
        var root = JsonDocument.Parse(stream).RootElement.Clone();
        return root.GetProperty("sessions")
            .EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == session)
            .GetProperty("steps")[step];
    }

    static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

    /// <summary>Whole MIDI messages: SysEx to its F7, channel messages by their status's length.</summary>
    static IEnumerable<byte[]> Split(byte[] data) {
        var i = 0;
        while (i < data.Length) {
            var status = data[i];
            int length;
            if (status == 0xF0) {
                var end = Array.IndexOf(data, (byte)0xF7, i);
                length = (end < 0 ? data.Length : end + 1) - i;
            } else {
                length = (status & 0xF0) switch {
                    0xC0 or 0xD0 => 2,
                    0xF0 => 1,
                    _ => 3
                };
            }

            yield return data[i..Math.Min(data.Length, i + length)];
            i += length;
        }
    }

    [Fact]
    public async Task A_Mackie_Control_session_runs_through_the_service_as_the_vectors_and_the_protocol_say() {
        var tracks = await StartAsync(16);
        var surface = await AddAsync(ControlSurfaceKind.MackieControl, "mcu-in", "mcu-out");

        // The handshake, byte for byte as McuVectors.json's "handshake and full refresh": the device query, then the
        // host connection reply to the unit's query, sent at once.
        var query = Hex(Step("McuVectors.json", "handshake and full refresh", 0).GetProperty("out")[0][0].GetString()!);
        await ServerFixture.Eventually(() => Task.FromResult(Sent("mcu-out", query)));
        await Host.PlayControlAsync(
            "mcu-in",
            Hex(Step("McuVectors.json", "handshake and full refresh", 1).GetProperty("in").GetString()!)
        );
        var reply = Hex(Step("McuVectors.json", "handshake and full refresh", 2).GetProperty("out")[0][0].GetString()!);
        await ServerFixture.Eventually(() => Task.FromResult(Sent("mcu-out", reply)));
        await ServerFixture.Eventually(async () => (await StatusAsync(surface.Id)).Connected);

        // The first eight faders as 14-bit pitch bend (0 dB), and the names on the scribble strip's top line.
        var zeroDb = (int)Math.Round(Taper.Position(0) * 16383);
        for (var strip = 0; strip < 8; strip++) {
            byte[] fader = [(byte)(0xE0 | strip), (byte)(zeroDb & 0x7F), (byte)(zeroDb >> 7)];
            await ServerFixture.Eventually(() => Task.FromResult(Sent("mcu-out", fader)));
        }

        Assert.StartsWith("T1     T2     T3", Display("mcu-out"), StringComparison.Ordinal);

        // Fader 1 with touch: one gesture on track 1's volume.
        var position = 12_000;
        await Host.PlayControlAsync(
            "mcu-in",
            [0x90, 0x68, 0x7F],
            [0xE0, (byte)(position & 0x7F), (byte)(position >> 7)],
            [0x90, 0x68, 0x00]
        );
        await ServerFixture.Eventually(async () =>
            Math.Abs(await VolumeAsync(tracks[0]) - Taper.Db(position / 16383.0)) < 1e-6
        );

        // Play, Stop and Record act on the press; the release does nothing (no double toggle).
        await Host.PlayControlAsync("mcu-in", [0x90, 0x5E, 0x7F], [0x90, 0x5E, 0x00]);
        await ServerFixture.Eventually(() => Task.FromResult(Host.State == TelemetryTransportState.Playing));
        // While playing, the timecode display (CC 0x40…0x49) shows the playhead.
        await ServerFixture.Eventually(() => Task.FromResult(
                SentTo("mcu-out").Any(static m => m is [0xB0, >= 0x40 and <= 0x49, _])
            )
        );
        await Task.Delay(200, Token);
        Assert.Equal(TelemetryTransportState.Playing, Host.State);
        await Host.PlayControlAsync("mcu-in", [0x90, 0x5D, 0x7F], [0x90, 0x5D, 0x00]);
        await ServerFixture.Eventually(() => Task.FromResult(Host.State == TelemetryTransportState.Stopped));
        await client.EditAsync(new SetTrackCommand(tracks[0], RecordArmed: true));
        await Host.PlayControlAsync("mcu-in", [0x90, 0x5F, 0x7F], [0x90, 0x5F, 0x00]);
        await ServerFixture.Eventually(() => Task.FromResult(Host.State == TelemetryTransportState.Recording));
        await Host.PlayControlAsync("mcu-in", [0x90, 0x5D, 0x7F], [0x90, 0x5D, 0x00]);
        await ServerFixture.Eventually(() => Task.FromResult(Host.State == TelemetryTransportState.Stopped));

        // A strip's mute: one toggle per click.
        await Host.PlayControlAsync("mcu-in", [0x90, 0x11, 0x7F], [0x90, 0x11, 0x00]);
        await ServerFixture.Eventually(async () => (await client.ProjectAsync()).Tracks[1].Strip.Mute);
        await Task.Delay(200, Token);
        Assert.True((await client.ProjectAsync()).Tracks[1].Strip.Mute);

        // Bank right: tracks 9–16 on the strips.
        await Host.PlayControlAsync("mcu-in", [0x90, 0x2F, 0x7F], [0x90, 0x2F, 0x00]);
        await ServerFixture.Eventually(async () => (await StatusAsync(surface.Id)).BankOffset == 8);
        await ServerFixture.Eventually(() => Task.FromResult(
                Display("mcu-out").StartsWith("T9     T10    T11", StringComparison.Ordinal)
            )
        );
    }

    /// <summary>
    ///     The Mackie Control's two-line display (112 characters) as the scribble strip SysEx sent to
    ///     <paramref name="port" /> left it: each message writes its characters from its offset.
    /// </summary>
    string Display(string port) {
        var display = Enumerable.Repeat((byte)' ', 112).ToArray();
        foreach (var m in SentTo(port)) {
            if (m is [0xF0, 0x00, 0x00, 0x66, 0x14, 0x12, var offset, .., 0xF7]) {
                m.AsSpan(7, Math.Min(m.Length - 8, 112 - offset)).CopyTo(display.AsSpan(offset));
            }
        }

        return Encoding.ASCII.GetString(display);
    }

    [Fact]
    public async Task A_HUI_session_answers_the_ping_and_its_fader_moves_the_volume() {
        var tracks = await StartAsync(4);
        var surface = await AddAsync(ControlSurfaceKind.Hui, "hui-in", "hui-out");

        // The ping, as HuiVectors.json's "ping and offline": Anode pings; the unit's answer connects it.
        var ping = Hex(Step("HuiVectors.json", "ping and offline", 0).GetProperty("out")[0][0].GetString()!);
        await ServerFixture.Eventually(() => Task.FromResult(Sent("hui-out", ping)));
        await Host.PlayControlAsync(
            "hui-in",
            Hex(Step("HuiVectors.json", "ping and offline", 1).GetProperty("in").GetString()!)
        );
        await ServerFixture.Eventually(async () => (await StatusAsync(surface.Id)).Connected);

        // A unit that pings is answered at once.
        Host.ClearSentMidi();
        await Host.PlayControlAsync("hui-in", [0x90, 0x00, 0x00]);
        await ServerFixture.Eventually(() => Task.FromResult(Sent("hui-out", [0x90, 0x00, 0x7F])));

        // The strip names (4 characters) and fader 1: touch (zone 0, port 0), then the MSB/LSB pair.
        await ServerFixture.Eventually(() => Task.FromResult(
                SentTo("hui-out").Any(static m => m is [0xF0, 0x00, 0x00, 0x66, 0x05, 0x00, 0x10, 0x00, (byte)'T', (byte)'1', ..])
            )
        );
        await Host.PlayControlAsync(
            "hui-in",
            [0xB0, 0x0F, 0x00],
            [0xB0, 0x2F, 0x40],
            [0xB0, 0x00, 0x60],
            [0xB0, 0x20, 0x00],
            [0xB0, 0x0F, 0x00],
            [0xB0, 0x2F, 0x00]
        );
        var value = 0x60 << 7;
        await ServerFixture.Eventually(async () =>
            Math.Abs(await VolumeAsync(tracks[0]) - Taper.Db(value / 16383.0)) < 1e-6
        );

        // Play (zone 0x0E, port 4) on the press only.
        await Host.PlayControlAsync("hui-in", [0xB0, 0x0F, 0x0E], [0xB0, 0x2F, 0x44], [0xB0, 0x0F, 0x0E], [0xB0, 0x2F, 0x04]);
        await ServerFixture.Eventually(() => Task.FromResult(Host.State == TelemetryTransportState.Playing));
    }

    [Fact]
    public async Task OSC_round_trips_over_UDP_with_the_real_codec_and_address_space() {
        await StartAsync(2);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port;
        using (var free = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) {
            port = ((IPEndPoint)free.Client.LocalEndPoint!).Port;
        }

        var response = await client.PutAsJsonAsync(
            "/api/v1/control/osc",
            new OscSettings(true, port, []),
            ContractsJsonContext.Default.OscSettings,
            Token
        );
        Assert.True(response.IsSuccessStatusCode);
        var to = new IPEndPoint(IPAddress.Loopback, port);

        Task Send(OscPacket packet) => udp.SendAsync(OscCodec.Encode(packet), to, Token).AsTask();

        // A bundle of two: track 1's volume and track 2's mute.
        await Send(
            new OscBundle(
                1,
                [
                    new OscMessage("/anode/track/1/volume", [new OscFloat(0.5f)]),
                    new OscMessage("/anode/track/2/mute", [new OscInt(1)])
                ]
            )
        );
        await ServerFixture.Eventually(async () => {
                var project = await client.ProjectAsync();
                return Math.Abs(project.Tracks[0].Strip.VolumeDb - Taper.Db(0.5)) < 1e-4 && project.Tracks[1].Strip.Mute;
            }
        );
        await Send(new OscMessage("/anode/transport/play", []));
        await ServerFixture.Eventually(() => Task.FromResult(Host.State == TelemetryTransportState.Playing));
        await Send(new OscMessage("/anode/transport/stop", [new OscInt(1)]));
        await ServerFixture.Eventually(() => Task.FromResult(Host.State == TelemetryTransportState.Stopped));

        // Subscribed, the client hears every address, then a change made in the editor.
        var me = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        await Send(new OscMessage("/anode/subscribe", [new OscInt(me)]));
        var heard = new List<OscMessage>();
        async Task<OscMessage> Next() {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var packet = OscCodec.Decode((await udp.ReceiveAsync(timeout.Token)).Buffer);
            var message = packet as OscMessage ?? ((OscBundle)packet).Elements.OfType<OscMessage>().First();
            heard.Add(message);
            return message;
        }

        while ((await Next()).Address != "/anode/track/1/volume") {
            // the round of every address
        }

        var track = (await client.ProjectAsync()).Tracks[0].Id;
        await client.EditAsync(new SetStripCommand(track, -12));
        var expected = (float)Taper.Position(-12);
        while (await Next() is not { Address: "/anode/track/1/volume", Arguments: [OscFloat f] } || Math.Abs(f.Value - expected) > 1e-5) {
            // other addresses, and the value before the change
        }

        Assert.Contains(heard, static m => m.Address == "/anode/transport/stop");
    }
}

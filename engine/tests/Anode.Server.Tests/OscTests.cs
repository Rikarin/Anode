using Anode.Contracts;
using Anode.Control.Osc;
using Anode.Ipc.Telemetry;
using Anode.Server.Control;
using Anode.Server.Engine;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace Anode.Server.Tests;

/// <summary>
///     The OSC server over real UDP on localhost (docs/09-phase-8.md, "OSC"), with a stand-in for workstream D's codec
///     and address space (<see cref="TestOsc" />): requests carried out, subscriptions with feedback on change at most
///     30 a second per address, rebinding to a new port, and the settings API.
/// </summary>
public sealed class OscTests : IAsyncDisposable {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly ServerFixture server = new(configure: static s => s.AddSingleton<IControlProtocols>(new TestProtocols()));
    readonly HttpClient client;
    readonly UdpClient udp = Loopback();

    public OscTests() {
        client = server.CreateClient();
    }

    public async ValueTask DisposeAsync() {
        udp.Dispose();
        client.Dispose();
        await server.DisposeAsync();
    }

    FakeHost Host => server.Services.GetRequiredService<EngineConnection>().InProcessHost!;

    /// <summary>A UDP socket on a free loopback port: the OSC client.</summary>
    static UdpClient Loopback() => new(new IPEndPoint(IPAddress.Loopback, 0));

    static int FreePort() {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    async Task<OscStatus> SetAsync(OscSettings settings) {
        var response = await client.PutAsJsonAsync(
            "/api/v1/control/osc",
            settings,
            ContractsJsonContext.Default.OscSettings,
            Token
        );
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.OscStatus, Token))!;
    }

    Task SendAsync(int port, string address, params OscArgument[] arguments) =>
        udp.SendAsync(TestOsc.Encode(new(address, arguments)), new(IPAddress.Loopback, port), Token)
            .AsTask();

    async Task<string> TrackAsync() {
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(client)).Engine.State == EngineState.InProcess
        );
        await client.CreateProjectAsync();
        await client.EditAsync(new AddTrackCommand("One"));
        return (await client.ProjectAsync()).Tracks[0].Id;
    }

    [Fact]
    public async Task Requests_over_UDP_set_volumes_and_run_the_transport() {
        var track = await TrackAsync();
        var port = FreePort();
        var status = await SetAsync(new(true, port, []));
        Assert.True(status.Listening);
        Assert.Equal(port, status.Settings.Port);

        await SendAsync(port, "/anode/track/1/volume", new OscFloat(0.5f));
        await ServerFixture.Eventually(async () =>
            Math.Abs((await client.ProjectAsync()).Tracks.Single(t => t.Id == track).Strip.VolumeDb - Taper.Db(0.5))
            < 1e-6
        );
        await SendAsync(port, "/anode/transport/play");
        await ServerFixture.Eventually(() => Task.FromResult(Host.State == TelemetryTransportState.Playing));
        await SendAsync(port, "/anode/transport/stop");
        await ServerFixture.Eventually(() => Task.FromResult(Host.State == TelemetryTransportState.Stopped));
        // Not OSC, or outside the address space: ignored.
        await udp.SendAsync("nonsense"u8.ToArray(), new(IPAddress.Loopback, port), Token);
        await SendAsync(port, "/anode/track/9/volume", new OscFloat(0.1f));
        await SendAsync(port, "/anode/track/1/mute", new OscInt(1));
        await ServerFixture.Eventually(async () => (await client.ProjectAsync()).Tracks[0].Strip.Mute);
    }

    [Fact]
    public async Task A_subscriber_gets_every_value_then_changes_at_most_30_a_second_per_address() {
        var track = await TrackAsync();
        var port = FreePort();
        await SetAsync(new(true, port, []));
        var me = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        await SendAsync(port, "/anode/subscribe", new OscInt(me));

        var first = await ReceiveAsync();
        Assert.Equal("/anode/track/1/volume", first.Address);
        Assert.Equal((float)Taper.Position(0), ((OscFloat)first.Arguments[0]).Value, 5);
        var status = await client.GetFromJsonAsync(
            "/api/v1/control/osc",
            ContractsJsonContext.Default.OscStatus,
            Token
        );
        Assert.Equal(new("127.0.0.1", me), Assert.Single(status!.Subscribed));

        // A change in the editor.
        await client.EditAsync(new SetStripCommand(track, -12));
        var expected = (float)Taper.Position(-12);
        while (Math.Abs(((OscFloat)(await ReceiveAsync()).Arguments[0]).Value - expected) > 1e-5) {
            // the values before the change (the first round of feedback)
        }

        // Many changes a second: no more than 30 values of the address go out.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var received = 0;
        var receiving = Task.Run(
            async () => {
                using var window = new CancellationTokenSource(TimeSpan.FromSeconds(1.2));
                try {
                    while (true) {
                        await udp.ReceiveAsync(window.Token);
                        received++;
                    }
                } catch (OperationCanceledException) {
                    // the window closed
                }
            },
            Token
        );
        for (var i = 0; stopwatch.Elapsed < TimeSpan.FromSeconds(1); i++) {
            await client.EditAsync(new SetStripCommand(track, -20 + i % 20));
        }

        await receiving;
        Assert.InRange(received, 1, 37);

        await SendAsync(port, "/anode/unsubscribe", new OscInt(me));
        await ServerFixture.Eventually(async () =>
            (await client.GetFromJsonAsync("/api/v1/control/osc", ContractsJsonContext.Default.OscStatus, Token))!
            .Subscribed.Count
            == 0
        );
    }

    async Task<OscMessage> ReceiveAsync() {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var datagram = await udp.ReceiveAsync(timeout.Token);
        return TestOsc.Decode(datagram.Buffer);
    }

    [Fact]
    public async Task A_new_port_rebinds_and_turning_it_off_stops_listening() {
        await TrackAsync();
        var first = FreePort();
        await SetAsync(new(true, first, []));
        var second = FreePort();
        var status = await SetAsync(new(true, second, [new("localhost", 9000)]));
        Assert.True(status.Listening);
        Assert.Equal([new OscClient("localhost", 9000)], status.Settings.Clients);

        // The old port is free again: something else can bind it.
        using (new UdpClient(new IPEndPoint(IPAddress.IPv6Any, first))) { }

        await SendAsync(second, "/anode/track/1/volume", new OscFloat(0.25f));
        await ServerFixture.Eventually(async () =>
            Math.Abs((await client.ProjectAsync()).Tracks[0].Strip.VolumeDb - Taper.Db(0.25)) < 1e-6
        );

        var off = await SetAsync(new(false, second, []));
        Assert.False(off.Listening);
        Assert.True(File.Exists(Path.Combine(server.DataDirectory, "settings", "osc.json")));
    }

    [Fact]
    public async Task Osc_settings_are_checked() {
        var response = await client.PutAsJsonAsync(
            "/api/v1/control/osc",
            new OscSettings(true, 80, []),
            ContractsJsonContext.Default.OscSettings,
            Token
        );
        await ApiCalls.AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        var initial = await client.GetFromJsonAsync(
            "/api/v1/control/osc",
            ContractsJsonContext.Default.OscStatus,
            Token
        );
        Assert.Equal((false, 8000, false), (initial!.Settings.Enabled, initial.Settings.Port, initial.Listening));
    }

    [Fact]
    public void Bonjour_advertises_on_macOS() {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Bonjour registration is macOS's.");
        using var registration = Bonjour.Advertise(
            "Anode test",
            OscServer.ServiceType,
            FreePort(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
        );
        Assert.NotNull(registration);
    }
}

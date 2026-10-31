using Anode.Contracts;
using Anode.Ipc;
using Anode.Server.Control;
using Anode.Server.Engine;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace Anode.Server.Tests;

/// <summary>
///     Control surfaces' plumbing (docs/09-phase-8.md, "Surfaces"), with a made-up protocol (<see cref="TestSurface" />)
///     in place of workstream D's Mackie Control and HUI: the surfaces API, control ports, feedback to the output port and
///     its rates, faders with touch as gestures, transport buttons, banking with extenders, status events, and the full
///     refresh after a host reconnects.
/// </summary>
public sealed class SurfaceTests : IAsyncDisposable {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly TestProtocols protocols = new();
    readonly string socketPath = Path.Combine(Path.GetTempPath(), $"an-{Guid.NewGuid().ToString("n")[..8]}.sock");
    FakeHost? socketHost;
    ServerFixture? server;
    HttpClient? client;

    public async ValueTask DisposeAsync() {
        client?.Dispose();
        if (server is not null) {
            await server.DisposeAsync();
        }

        if (socketHost is not null) {
            await socketHost.DisposeAsync();
        }
    }

    FakeHost Host => socketHost ?? server!.Services.GetRequiredService<EngineConnection>().InProcessHost!;

    HttpClient Client => client!;

    async Task<IReadOnlyList<string>> StartAsync(int tracks, bool overSocket = false, IControlProtocols? real = null) {
        if (overSocket) {
            socketHost = new(EngineConnection.Version);
            socketHost.Listen(socketPath);
        }

        server = new(overSocket ? socketPath : null, configure: s => s.AddSingleton(real ?? protocols));
        client = server.CreateClient();
        await ServerFixture.Eventually(async () =>
            (await server.SystemAsync(Client)).Engine.State is EngineState.InProcess or EngineState.Connected
        );
        await Client.CreateProjectAsync();
        await Client.EditAsync([..Enumerable.Range(1, tracks).Select(static n => new AddTrackCommand($"T{n}"))]);
        return [..(await Client.ProjectAsync()).Tracks.Select(static t => t.Id)];
    }

    async Task<ControlSurface> AddAsync(ControlSurfaceKind kind, string input, string output) {
        var response = await Client.PostAsJsonAsync(
            "/api/v1/control/surfaces",
            new(kind, input, output),
            ContractsJsonContext.Default.AddSurfaceRequest,
            Token
        );
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ControlSurface, Token))!;
    }

    async Task<double> VolumeAsync(string trackId) =>
        (await Client.ProjectAsync()).Tracks.Single(t => t.Id == trackId).Strip.VolumeDb;

    static byte[] Fader(int strip, double position) {
        var value = (int)Math.Round(position * 16383);
        return [(byte)(0xE0 | strip), (byte)(value & 0x7F), (byte)(value >> 7)];
    }

    /// <summary>
    ///     A Mackie Control's (device 0x14) or XT's (0x15) two-line display as the scribble strip SysEx sent to
    ///     <paramref name="port" /> left it (each message writes from its offset).
    /// </summary>
    string Display(string port, byte device) {
        var display = Enumerable.Repeat((byte)' ', 112).ToArray();
        foreach (var data in SentTo(port)) {
            for (var i = 0; i + 7 < data.Length; i++) {
                if (data[i] == 0xF0 && data.AsSpan(i + 1, 5).SequenceEqual(new byte[] { 0x00, 0x00, 0x66, device, 0x12 })) {
                    var end = Array.IndexOf(data, (byte)0xF7, i);
                    var offset = data[i + 6];
                    data.AsSpan(i + 7, Math.Min(end - i - 7, 112 - offset)).CopyTo(display.AsSpan(offset));
                }
            }
        }

        return System.Text.Encoding.ASCII.GetString(display);
    }

    IEnumerable<byte[]> SentTo(string port) => Host.SentMidi.Where(m => m.Port == port).Select(static m => m.Data);

    [Fact]
    public async Task A_surface_is_a_control_port_and_its_faders_and_buttons_work_the_project() {
        var tracks = await StartAsync(3);
        var surface = await AddAsync(ControlSurfaceKind.MackieControl, "fake-in", "fake-out");
        await ServerFixture.Eventually(() => Task.FromResult(Host.ControlRouting?.ControlPorts is ["fake-in"]));

        // Feedback: every strip's fader (0 dB at 0.78) and name go to the output port.
        var zeroDb = Fader(0, Taper.Position(0));
        await ServerFixture.Eventually(() => Task.FromResult(
                SentTo("fake-out").Any(d => d.AsSpan().IndexOf(zeroDb) >= 0)
            )
        );
        Assert.Contains(SentTo("fake-out"), static d => d.AsSpan().IndexOf("T1"u8) >= 0);

        // Touch, move, hold still past the gesture timeout, move, release: one undo step.
        var history = await StepsAsync();
        await Host.PlayControlAsync("fake-in", [0x90, 0x68, 0x7F], Fader(0, 0.5));
        await Task.Delay(ControlService.GestureTimeout + TimeSpan.FromMilliseconds(200), Token);
        await Host.PlayControlAsync("fake-in", Fader(0, 0.4), [0x90, 0x68, 0x00]);
        await ServerFixture.Eventually(async () => Math.Abs(await VolumeAsync(tracks[0]) - Taper.Db(0.4)) < 1e-3);
        Assert.Equal(history + 1, await StepsAsync());
        // Released: the next move is another gesture.
        await Host.PlayControlAsync("fake-in", Fader(0, 0.3));
        await ServerFixture.Eventually(async () => Math.Abs(await VolumeAsync(tracks[0]) - Taper.Db(0.3)) < 1e-3);
        Assert.Equal(history + 2, await StepsAsync());

        // A volume changed in the editor reaches the fader.
        await Client.EditAsync(new SetStripCommand(tracks[1], -6));
        var minusSix = Fader(1, Taper.Position(-6));
        await ServerFixture.Eventually(() => Task.FromResult(
                SentTo("fake-out").Any(d => d.AsSpan().IndexOf(minusSix) >= 0)
            )
        );

        // Mute (a toggle) and the transport.
        await Host.PlayControlAsync("fake-in", [0x90, 0x11, 0x7F]);
        await ServerFixture.Eventually(async () => (await Client.ProjectAsync()).Tracks[1].Strip.Mute);
        await Host.PlayControlAsync("fake-in", [0x90, 0x5E, 0x7F]);
        await ServerFixture.Eventually(() =>
            Task.FromResult(Host.State == Ipc.Telemetry.TelemetryTransportState.Playing)
        );
        await Host.PlayControlAsync("fake-in", [0x90, 0x5D, 0x7F]);
        await ServerFixture.Eventually(() =>
            Task.FromResult(Host.State == Ipc.Telemetry.TelemetryTransportState.Stopped)
        );

        var statuses = await Client.GetFromJsonAsync(
            "/api/v1/control/surfaces/status",
            ContractsJsonContext.Default.IReadOnlyListSurfaceStatus,
            Token
        );
        Assert.Equal(new(surface.Id, true, 0), Assert.Single(statuses!));
    }

    async Task<int> StepsAsync() =>
        (await Client.GetFromJsonAsync("/api/v1/project/history", ContractsJsonContext.Default.UndoHistory, Token))!
        .Undo.Count;

    [Fact]
    public async Task A_surfaces_port_is_not_offered_to_Learn() {
        await StartAsync(1);
        await AddAsync(ControlSurfaceKind.Hui, "fake-in", "fake-out");
        await using var events = await EventStream.ConnectAsync(server!);
        await Client.PostAsJsonAsync(
            "/api/v1/control/learn/start",
            new(),
            ContractsJsonContext.Default.LearnRequest,
            Token
        );
        await Host.PlayControlAsync("fake-in", [0xB0, 1, 2]);
        await Host.PlayControlAsync("other-in", [0xB0, 3, 4]);
        await events.WaitAsync<ControlLearnEvent>(static e => e.Message.Port == "other-in");
        Assert.DoesNotContain(events.OfType<ControlLearnEvent>(), static e => e.Message.Port == "fake-in");
    }

    [Fact]
    public async Task Faders_and_LEDs_go_out_at_most_30_times_a_second_and_meters_10() {
        await StartAsync(2);
        await AddAsync(ControlSurfaceKind.MackieControl, "fake-in", "fake-out");
        var surface = await SurfaceAsync();
        await Task.Delay(500, Token);
        var (encodes, meters) = (surface.Encodes, surface.MeterCalls);
        await Task.Delay(2000, Token);
        var encodeRate = (surface.Encodes - encodes) / 2.0;
        var meterRate = (surface.MeterCalls - meters) / 2.0;
        Assert.InRange(encodeRate, 10, 30.5);
        Assert.InRange(meterRate, 3, 10.5);
        // No two encodes closer than the tick allows.
        var times = surface.EncodeTimes.ToArray();
        Assert.All(
            times.Zip(times.Skip(1)),
            static p => Assert.True(p.Second - p.First >= 32, $"{p.Second - p.First} ms")
        );
    }

    async Task<TestSurface> SurfaceAsync() {
        TestSurface? surface = null;
        await ServerFixture.Eventually(() => Task.FromResult(protocols.Surfaces.TryPeek(out surface)));
        return surface!;
    }

    [Fact]
    public async Task Banking_moves_the_extenders_after_it_and_is_reported() {
        // Workstream D's Mackie Control and XT in a SurfaceGroup: the group owns the bank.
        await StartAsync(20, real: new ControlProtocols());
        var main = await AddAsync(ControlSurfaceKind.MackieControl, "mcu-in", "mcu-out");
        var xt = await AddAsync(ControlSurfaceKind.MackieControlXt, "xt-in", "xt-out");
        var taken = await Client.PostAsJsonAsync(
            "/api/v1/control/surfaces",
            new(ControlSurfaceKind.Hui, "xt-out", "hui-out"),
            ContractsJsonContext.Default.AddSurfaceRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(taken, HttpStatusCode.UnprocessableEntity);
        await using var events = await EventStream.ConnectAsync(server!);
        // A unit banks over the tracks it was last shown: wait for the first feedback.
        await ServerFixture.Eventually(() => Task.FromResult(Display("xt-out", 0x15).StartsWith("T9 ", StringComparison.Ordinal)));
        await Host.PlayControlAsync("mcu-in", [0x90, 0x2F, 0x7F]);
        var banked = await events.WaitAsync<SurfacesEvent>(static e => e.Surfaces[0].BankOffset == 8);
        Assert.Equal([(main.Id, 8), (xt.Id, 16)], banked.Surfaces.Select(static s => (s.Id, s.BankOffset)));
        await ServerFixture.Eventually(() => Task.FromResult(
                Display("xt-out", 0x15).StartsWith("T17    T18", StringComparison.Ordinal)
            )
        );

        // Reordered, the extender comes first and follows nothing; removed, its ports play tracks again.
        var reordered = await Client.PutAsJsonAsync(
            "/api/v1/control/surfaces/order",
            new ReorderSurfacesRequest([xt.Id, main.Id]),
            ContractsJsonContext.Default.ReorderSurfacesRequest,
            Token
        );
        var order = await reordered.Content.ReadFromJsonAsync(
            ContractsJsonContext.Default.IReadOnlyListControlSurface,
            Token
        );
        Assert.Equal([(xt.Id, 0), (main.Id, 1)], order!.Select(static s => (s.Id, s.Order)));
        var bad = await Client.PutAsJsonAsync(
            "/api/v1/control/surfaces/order",
            new ReorderSurfacesRequest([xt.Id]),
            ContractsJsonContext.Default.ReorderSurfacesRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(bad, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client.DeleteAsync($"/api/v1/control/surfaces/{xt.Id}", Token)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client.DeleteAsync($"/api/v1/control/surfaces/{xt.Id}", Token)).StatusCode
        );
        await ServerFixture.Eventually(() => Task.FromResult(Host.ControlRouting?.ControlPorts is ["mcu-in"]));
        Assert.Equal(
            [main.Id],
            (await Client.GetFromJsonAsync(
                "/api/v1/control/surfaces",
                ContractsJsonContext.Default.IReadOnlyListControlSurface,
                Token
            ))!.Select(static s => s.Id)
        );
    }

    [Fact]
    public async Task A_reconnected_host_gets_the_routing_the_sync_settings_and_every_surface_again() {
        await StartAsync(2, true);
        await AddAsync(ControlSurfaceKind.MackieControl, "fake-in", "fake-out");
        var surface = await SurfaceAsync();
        await ServerFixture.Eventually(() => Task.FromResult(SentTo("fake-out").Any()));
        var invalidations = surface.Invalidations;

        await socketHost!.DisposeAsync();
        socketHost = null;
        await ServerFixture.Eventually(async () =>
            (await server!.SystemAsync(Client)).Engine.State == EngineState.Disconnected
        );
        socketHost = new(EngineConnection.Version);
        socketHost.Listen(socketPath);
        await ServerFixture.Eventually(async () =>
            (await server!.SystemAsync(Client)).Engine.State == EngineState.Connected
        );

        await ServerFixture.Eventually(() => Task.FromResult(Host.ControlRouting?.ControlPorts is ["fake-in"]));
        await ServerFixture.Eventually(() => Task.FromResult(Host.Sync is { Source: EngineSyncSource.Internal }));
        Assert.True(surface.Invalidations > invalidations);
        // Everything again: both strips' faders and names.
        await ServerFixture.Eventually(() => Task.FromResult(
                SentTo("fake-out").Any(static d => d.AsSpan().IndexOf("T1"u8) >= 0)
                && SentTo("fake-out").Any(static d => d.AsSpan().IndexOf("T2"u8) >= 0)
            )
        );
    }

    [Fact]
    public async Task Surfaces_are_checked() {
        await StartAsync(1);
        var noPort = await Client.PostAsJsonAsync(
            "/api/v1/control/surfaces",
            new(ControlSurfaceKind.Hui, string.Empty, "out"),
            ContractsJsonContext.Default.AddSurfaceRequest,
            Token
        );
        await ApiCalls.AssertProblemAsync(noPort, HttpStatusCode.UnprocessableEntity);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await Client.DeleteAsync("/api/v1/control/surfaces/srf_x", Token)).StatusCode
        );
    }
}

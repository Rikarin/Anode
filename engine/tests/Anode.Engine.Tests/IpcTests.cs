using Anode.EngineHost;
using Anode.Ipc;
using System.Net.Sockets;

namespace Anode.Engine.Tests;

public sealed class IpcTests {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Frames_round_trip() {
        await using var stream = new MemoryStream();
        await IpcFraming.WriteAsync(stream, new HelloMessage("1.2.3", "null"), Token);
        await IpcFraming.WriteAsync(stream, new PingMessage(7), Token);
        stream.Position = 0;

        Assert.Equal(new HelloMessage("1.2.3", "null"), await IpcFraming.ReadAsync(stream, Token));
        Assert.Equal(new PingMessage(7), await IpcFraming.ReadAsync(stream, Token));
        Assert.Null(await IpcFraming.ReadAsync(stream, Token));
    }

    [Fact]
    public async Task An_oversized_frame_is_refused() {
        await using var stream = new MemoryStream(BitConverter.GetBytes(IpcFraming.MaxFrameBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcFraming.ReadAsync(stream, Token));
    }

    [Fact]
    public async Task Host_answers_hello_and_ping_over_its_socket() {
        // Unix socket paths are limited to ~104 bytes on macOS.
        var path = Path.Combine(Path.GetTempPath(), $"an-{Guid.NewGuid().ToString("n")[..8]}.sock");
        await using var host = new EngineHostServer(path, "9.9.9", "null");
        host.Start();

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), Token);
        await using var stream = new NetworkStream(socket);
        await IpcFraming.WriteAsync(stream, new HelloMessage("test"), Token);
        Assert.Equal(new HelloMessage("9.9.9", "null"), await IpcFraming.ReadAsync(stream, Token));
        await IpcFraming.WriteAsync(stream, new PingMessage(42), Token);
        Assert.Equal(new PongMessage(42), await IpcFraming.ReadAsync(stream, Token));
    }
}

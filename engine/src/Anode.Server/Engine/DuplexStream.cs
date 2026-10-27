using System.IO.Pipelines;

namespace Anode.Server.Engine;

/// <summary>
///     One end of an in-memory, two-way byte stream: what the in-process engine mode uses instead of the host's Unix
///     domain socket, so the server and <see cref="FakeHost" /> speak the very IPC framing and messages they would
///     over the socket.
/// </summary>
public sealed class DuplexStream(Stream input, Stream output) : Stream {
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Two connected ends: what one writes, the other reads.</summary>
    public static (DuplexStream A, DuplexStream B) CreatePair() {
        var ab = new Pipe();
        var ba = new Pipe();
        return (new(ba.Reader.AsStream(), ab.Writer.AsStream()), new(ab.Reader.AsStream(), ba.Writer.AsStream()));
    }

    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        input.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        input.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        output.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        output.WriteAsync(buffer, offset, count, cancellationToken);

    public override void Flush() => output.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) {
        if (disposing) {
            // Completing the output ends the peer's reads; completing the input ends its writes.
            output.Dispose();
            input.Dispose();
        }

        base.Dispose(disposing);
    }
}

using Anode.Audio;
using Microsoft.Win32.SafeHandles;

namespace Anode.Engine.Streaming;

/// <summary>
///     An audio file the disk thread streams from: opened once with the page cache bypassed (<c>F_NOCACHE</c>, spike
///     S2), read with positional reads widened to page boundaries, and decoded to float.
/// </summary>
public sealed class StreamFile : IDisposable {
    static readonly int Page = Math.Max(4096, Environment.SystemPageSize);

    readonly SafeFileHandle handle;
    readonly long fileLength;

    public StreamFile(int id, string path) {
        Id = id;
        Path = path;
        handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try {
            Format = WavFormat.Read(handle);
            fileLength = RandomAccess.GetLength(handle);
            Realtime.BypassPageCache(handle);
        } catch {
            handle.Dispose();
            throw;
        }
    }

    public int Id { get; }

    public string Path { get; }

    public WavFormat Format { get; }

    public int Channels => Format.Channels;

    public long Frames => Format.Frames;

    /// <summary>Scratch bytes a read of <paramref name="frames" /> frames needs (the read is widened to whole pages).</summary>
    public static int ScratchBytes(int frames, int bytesPerFrame) => frames * bytesPerFrame + 2 * Page;

    /// <summary>
    ///     Reads and decodes frames [<paramref name="frame" />, + <paramref name="count" />) (inside the file) into
    ///     <paramref name="destination" />, interleaved in the file's channel count. <paramref name="scratch" /> must
    ///     hold <see cref="ScratchBytes" />. Returns the bytes read from disk.
    /// </summary>
    public int Read(long frame, int count, Span<byte> scratch, Span<float> destination) {
        var from = Format.DataOffset + frame * Format.BytesPerFrame;
        var to = from + (long)count * Format.BytesPerFrame;
        var alignedFrom = from & ~(long)(Page - 1);
        var alignedTo = Math.Min((to + Page - 1) & ~(long)(Page - 1), fileLength);
        var length = (int)(alignedTo - alignedFrom);
        var read = 0;
        while (read < length) {
            var n = RandomAccess.Read(handle, scratch.Slice(read, length - read), alignedFrom + read);
            if (n <= 0) {
                break;
            }

            read += n;
        }

        var offset = (int)(from - alignedFrom);
        var available = Math.Clamp((read - offset) / Format.BytesPerFrame, 0, count);
        Format.Decode(scratch.Slice(offset, available * Format.BytesPerFrame), destination);
        destination[(available * Format.Channels)..(count * Format.Channels)].Clear();
        return read;
    }

    public void Dispose() => handle.Dispose();
}

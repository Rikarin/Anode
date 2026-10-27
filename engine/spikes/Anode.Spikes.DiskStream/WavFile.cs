using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace Anode.Spikes.DiskStream;

/// <summary>
///     Mono 48 kHz / 24-bit test files. The audio data starts at byte 4096 (a JUNK chunk pads the header, as Logic and
///     Pro Tools do for BWF), so page-aligned reads are frame-aligned too. Every sample is a function of its frame
///     index and its track, which lets the callback check that it plays the right frame of the right file.
/// </summary>
static class WavFile {
    public const int SampleRate = 48_000;
    public const int BytesPerFrame = 3;
    public const int DataOffset = 4096;

    public static string PathOf(string dir, int track) => Path.Combine(dir, $"track{track:000}.wav");

    /// <summary>The 24-bit sample at <paramref name="frame" /> of <paramref name="track" />'s file.</summary>
    public static int Pattern(long frame, int track) => (int)((frame * 3 + track * 7919L) & 0x7F_FFFF) - 0x40_0000;

    public static int Generate(Options o) {
        var dir = Path.GetFullPath(o.Text("dir") ?? "artifacts/spike-audio");
        var tracks = o.Int("tracks", 128);
        var minSeconds = o.Int("min-seconds", 120);
        var maxSeconds = o.Int("max-seconds", 300);
        var noCache = o.Flag("nocache");
        Directory.CreateDirectory(dir);
        var buffer = new byte[BytesPerFrame * 4096 * 64];
        var clock = Stopwatch.StartNew();
        long total = 0;
        for (var t = 0; t < tracks; t++) {
            // Lengths spread evenly over [min, max], rounded to whole pages of frames.
            var seconds = minSeconds + (long)(maxSeconds - minSeconds) * t / Math.Max(1, tracks - 1);
            var frames = seconds * SampleRate / 4096 * 4096;
            total += Write(PathOf(dir, t), t, frames, buffer, noCache);
        }

        var elapsed = clock.Elapsed.TotalSeconds;
        Console.WriteLine(
            $"wrote {tracks} files, {total / 1e9:0.00} GB in {elapsed:0.0} s ({total / 1e6 / elapsed:0} MB/s) to {dir}"
        );
        return 0;
    }

    static long Write(string path, int track, long frames, byte[] buffer, bool noCache) {
        var dataBytes = frames * BytesPerFrame;
        if (File.Exists(path) && new FileInfo(path).Length == DataOffset + dataBytes) {
            return 0; // already generated
        }

        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write);
        if (noCache) {
            Posix.SetNoCache(handle);
        }

        var header = new byte[DataOffset];
        WriteHeader(header, dataBytes);
        RandomAccess.Write(handle, header, 0);
        long frame = 0;
        while (frame < frames) {
            var count = (int)Math.Min(buffer.Length / BytesPerFrame, frames - frame);
            for (var i = 0; i < count; i++) {
                var s = Pattern(frame + i, track);
                buffer[i * 3] = (byte)s;
                buffer[i * 3 + 1] = (byte)(s >> 8);
                buffer[i * 3 + 2] = (byte)(s >> 16);
            }

            RandomAccess.Write(handle, buffer.AsSpan(0, count * BytesPerFrame), DataOffset + frame * BytesPerFrame);
            frame += count;
        }

        return DataOffset + dataBytes;
    }

    static void WriteHeader(Span<byte> h, long dataBytes) {
        Ascii(h, 0, "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(DataOffset - 8 + dataBytes));
        Ascii(h, 8, "WAVE");
        Ascii(h, 12, "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], 1); // mono
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], SampleRate * BytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], BytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 24);
        Ascii(h, 36, "JUNK");
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], DataOffset - 44 - 8);
        Ascii(h, DataOffset - 8, "data");
        BinaryPrimitives.WriteUInt32LittleEndian(h[(DataOffset - 4)..], (uint)dataBytes);
    }

    static void Ascii(Span<byte> h, int at, string s) => Encoding.ASCII.GetBytes(s, h[at..]);

    /// <summary>Walks the RIFF chunks; returns the data chunk's offset and frame count of a mono 24-bit file.</summary>
    public static (long DataOffset, long Frames) ReadHeader(SafeFileHandle handle) {
        Span<byte> chunk = stackalloc byte[12];
        Span<byte> fmt = stackalloc byte[16];
        RandomAccess.Read(handle, chunk, 0);
        if (!chunk[..4].SequenceEqual("RIFF"u8) || !chunk[8..12].SequenceEqual("WAVE"u8)) {
            throw new InvalidDataException("not a RIFF/WAVE file");
        }

        long at = 12;
        var formatOk = false;
        var length = RandomAccess.GetLength(handle);
        while (at + 8 <= length) {
            RandomAccess.Read(handle, chunk[..8], at);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
            if (chunk[..4].SequenceEqual("fmt "u8)) {
                RandomAccess.Read(handle, fmt, at + 8);
                formatOk = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]) == 1
                    && BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]) == SampleRate
                    && BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]) == 24;
            } else if (chunk[..4].SequenceEqual("data"u8)) {
                return formatOk
                    ? (at + 8, size / BytesPerFrame)
                    : throw new InvalidDataException("expected mono 48 kHz 24-bit PCM");
            }

            at += 8 + size + (size & 1);
        }

        throw new InvalidDataException("no data chunk");
    }
}

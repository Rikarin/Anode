using Anode.Contracts;
using Anode.Model.Audio;
using System.Buffers.Binary;

namespace Anode.Model.Tests;

/// <summary>WAV reading and writing, and the peaks format.</summary>
public sealed class AudioTests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("anode-audio-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    public static TheoryData<WavEncoding, int> Formats() {
        var data = new TheoryData<WavEncoding, int>();
        foreach (var encoding in Enum.GetValues<WavEncoding>()) {
            data.Add(encoding, 1);
            data.Add(encoding, 2);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Every_supported_format_reads_back_what_was_written(WavEncoding encoding, int channels) {
        var path = Path.Combine(folder, "a.wav");
        var samples = Enumerable.Range(0, 1000 * channels).Select(static i => MathF.Sin(i * 0.01f) * 0.9f).ToArray();
        using (var writer = new WavWriter(path, channels, 44_100, encoding)) {
            writer.Write(samples);
        }

        using var reader = WavReader.Open(path);
        Assert.Equal(new(channels, 44_100, encoding, 44, 1000), reader.Info);
        var back = new float[samples.Length + 10];
        Assert.Equal(1000, reader.Read(back));
        Assert.Equal(0, reader.Read(back));
        var tolerance = encoding == WavEncoding.Pcm16 ? 1e-4 : 1e-6;
        for (var i = 0; i < samples.Length; i++) {
            Assert.InRange(back[i] - samples[i], -tolerance, tolerance);
        }
    }

    [Fact]
    public void Extensible_headers_and_extra_chunks_are_understood() {
        var bytes = new List<byte>();
        bytes.AddRange("RIFF"u8.ToArray());
        bytes.AddRange(new byte[4]);
        bytes.AddRange("WAVE"u8.ToArray());
        bytes.AddRange("LIST"u8.ToArray());
        bytes.AddRange(Le32(3));
        bytes.AddRange(new byte[] { 1, 2, 3, 0 }); // odd chunk, padded
        bytes.AddRange("fmt "u8.ToArray());
        bytes.AddRange(Le32(40));
        var fmt = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), 48_000);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(16), 22);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(24), 3); // float sub-format
        bytes.AddRange(fmt);
        bytes.AddRange("data"u8.ToArray());
        bytes.AddRange(Le32(16));
        bytes.AddRange(new byte[16]);
        using var reader = WavReader.Open(new MemoryStream([..bytes]));
        Assert.Equal(
            (2, 48_000, WavEncoding.Float32, 2L),
            (reader.Info.Channels, reader.Info.SampleRate,
                reader.Info.Encoding, reader.Info.Frames)
        );
    }

    [Fact]
    public void Unsupported_files_are_refused() {
        Assert.Throws<WavFormatException>(static () => WavReader.Open(new MemoryStream([.."not a wav file at all"u8])));
        var path = Path.Combine(folder, "8bit.wav");
        using (var writer = new WavWriter(path, 1, 48_000, WavEncoding.Pcm16)) {
            writer.Write([0f, 0f]);
        }

        var bytes = File.ReadAllBytes(path);
        bytes[34] = 8; // bits per sample
        File.WriteAllBytes(path, bytes);
        Assert.Throws<WavFormatException>(() => WavReader.Probe(path));
    }

    [Fact]
    public void Peaks_are_built_at_every_level_and_sliced() {
        var path = Path.Combine(folder, "p.wav");
        const int frames = 10_000;
        using (var writer = new WavWriter(path, 2, 48_000, WavEncoding.Float32)) {
            var samples = new float[frames * 2];
            for (var i = 0; i < frames; i++) {
                samples[i * 2] = i == 100 ? 0.5f : 0; // left: one spike in the second 64-frame peak
                samples[i * 2 + 1] = i == 9_999 ? -1f : 0; // right: a full-scale dip in the last frame
            }

            writer.Write(samples);
        }

        var peaks = Path.Combine(folder, "Peaks");
        Peaks.Build(path, peaks, "aud_1", TestContext.Current.CancellationToken);
        Assert.True(Peaks.Exist(peaks, "aud_1"));
        foreach (var level in PeakFormat.Levels) {
            var body = Peaks.Slice(Peaks.PathOf(peaks, "aud_1", level), 0, null);
            var count = (frames + level - 1) / level;
            AssertHeader(body, 2, level, 0, count);
            Assert.Equal(PeakFormat.HeaderBytes + count * 2 * 4, body.Length);
            Assert.Equal(16_384, Sample(body, level == 64 ? 1 : 0, 0, true)); // 0.5 × 32767, rounded
            Assert.Equal(-32_767, Sample(body, count - 1, 1, false));
        }

        var slice = Peaks.Slice(Peaks.PathOf(peaks, "aud_1", 64), 1, 2);
        AssertHeader(slice, 2, 64, 1, 2);
        Assert.Equal(16_384, Sample(slice, 0, 0, true));
        var clipped = Peaks.Slice(Peaks.PathOf(peaks, "aud_1", 4096), 2, 100);
        AssertHeader(clipped, 2, 4096, 2, 1);
    }

    [Theory]
    [InlineData(null, 1024)]
    [InlineData(1, 64)]
    [InlineData(300, 256)]
    [InlineData(2000, 1024)]
    [InlineData(3000, 4096)]
    [InlineData(1_000_000, 16_384)]
    public void Requests_get_the_nearest_kept_resolution(int? wanted, int level) =>
        Assert.Equal(level, Peaks.NearestLevel(wanted));

    static void AssertHeader(byte[] body, int channels, int framesPerPeak, uint first, long count) {
        Assert.Equal(PeakFormat.Magic, BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(PeakFormat.Version, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)));
        Assert.Equal(channels, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6)));
        Assert.Equal((uint)framesPerPeak, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(first, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal((ulong)count, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(16)));
    }

    /// <summary>The min or max of one channel of one peak of a body.</summary>
    static short Sample(byte[] body, long peak, int channel, bool max) =>
        BinaryPrimitives.ReadInt16LittleEndian(
            body.AsSpan(PeakFormat.HeaderBytes + (int)((peak * 2 + channel) * 4 + (max ? 2 : 0)))
        );

    static byte[] Le32(int value) {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        return b;
    }
}

using Anode.Contracts;
using Anode.Model.Audio;
using System.Buffers.Binary;

namespace Anode.Model.Tests;

/// <summary>WAV and AIFF writing, read back through <see cref="WavReader" /> and <see cref="AiffReader" />.</summary>
public sealed class AudioFileWriterTests : IDisposable {
    readonly string folder = Directory.CreateTempSubdirectory("anode-export-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    static int[][] Integers(int bits, int channelCount, int frames) {
        var max = (1 << (bits - 1)) - 1;
        var random = new Random(bits * 10 + channelCount);
        var channels = new int[channelCount][];
        for (var c = 0; c < channelCount; c++) {
            channels[c] = new int[frames];
            for (var i = 0; i < frames; i++) {
                channels[c][i] = random.Next(-max - 1, max + 1);
            }

            channels[c][0] = max;
            channels[c][1] = -max - 1;
            channels[c][2] = 0;
        }

        return channels;
    }

    static float[][] Floats(int channelCount, int frames) {
        var random = new Random(channelCount);
        var channels = new float[channelCount][];
        for (var c = 0; c < channelCount; c++) {
            channels[c] = new float[frames];
            for (var i = 0; i < frames; i++) {
                channels[c][i] = (float)(random.NextDouble() * 2.5 - 1.25);
            }
        }

        return channels;
    }

    static float[][] ReadWav(string path, out WavInfo info) {
        using var reader = WavReader.Open(path);
        info = reader.Info;
        var interleaved = new float[info.Frames * info.Channels];
        var read = 0;
        while (reader.Read(interleaved.AsSpan(read * info.Channels)) is var n and > 0) {
            read += n;
        }

        Assert.Equal(info.Frames, read);
        var channels = new float[info.Channels][];
        for (var c = 0; c < info.Channels; c++) {
            channels[c] = new float[read];
            for (var i = 0; i < read; i++) {
                channels[c][i] = interleaved[i * info.Channels + c];
            }
        }

        return channels;
    }

    [Theory]
    [InlineData(16, 1)]
    [InlineData(16, 2)]
    [InlineData(24, 1)]
    [InlineData(24, 2)]
    public void Integer_WAV_reads_back_exactly(int bits, int channelCount) {
        var path = Path.Combine(folder, "a.wav");
        var written = Integers(bits, channelCount, 1001);
        AudioFileWriter.WriteInteger(path, AudioFormat.Wav, 48_000, bits, written);
        var back = ReadWav(path, out var info);
        Assert.Equal(new(channelCount, 48_000, bits == 16 ? WavEncoding.Pcm16 : WavEncoding.Pcm24, 44, 1001), info);
        AssertIntegers(written, back, bits);
        Assert.Equal(
            44 + 1001 * channelCount * bits / 8 + ((1001 * channelCount * bits / 8) & 1),
            new FileInfo(path).Length
        );
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Float_WAV_reads_back_exactly(int channelCount) {
        var path = Path.Combine(folder, "a.wav");
        var written = Floats(channelCount, 1000);
        AudioFileWriter.WriteFloat(path, AudioFormat.Wav, 44_100, written);
        var back = ReadWav(path, out var info);
        Assert.Equal(WavEncoding.Float32, info.Encoding);
        Assert.Equal(44_100, info.SampleRate);
        for (var c = 0; c < channelCount; c++) {
            Assert.Equal(written[c], back[c]);
        }
    }

    [Theory]
    [InlineData(16, 1)]
    [InlineData(16, 2)]
    [InlineData(24, 1)]
    [InlineData(24, 2)]
    public void Integer_AIFF_reads_back_exactly(int bits, int channelCount) {
        var path = Path.Combine(folder, "a.aiff");
        var written = Integers(bits, channelCount, 1001);
        AudioFileWriter.WriteInteger(path, AudioFormat.Aiff, 44_100, bits, written);
        var (back, rate, depth) = AiffReader.Read(path);
        Assert.Equal(44_100, rate);
        Assert.Equal(bits, depth);
        AssertIntegers(written, back, bits);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Float_AIFF_C_reads_back_exactly(int channelCount) {
        var path = Path.Combine(folder, "a.aif");
        var written = Floats(channelCount, 1000);
        AudioFileWriter.WriteFloat(path, AudioFormat.Aiff, 96_000, written);
        var (back, rate, depth) = AiffReader.Read(path);
        Assert.Equal(96_000, rate);
        Assert.Equal(32, depth);
        for (var c = 0; c < channelCount; c++) {
            Assert.Equal(written[c], back[c]);
        }
    }

    [Theory]
    [InlineData(44_100, new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 })]
    [InlineData(48_000, new byte[] { 0x40, 0x0E, 0xBB, 0x80, 0, 0, 0, 0, 0, 0 })]
    [InlineData(96_000, new byte[] { 0x40, 0x0F, 0xBB, 0x80, 0, 0, 0, 0, 0, 0 })]
    [InlineData(8_000, new byte[] { 0x40, 0x0B, 0xFA, 0x00, 0, 0, 0, 0, 0, 0 })]
    public void The_AIFF_header_is_what_the_format_says(int rate, byte[] extended) {
        var path = Path.Combine(folder, "h.aiff");
        AudioFileWriter.WriteInteger(path, AudioFormat.Aiff, rate, 24, Integers(24, 2, 11));
        var file = File.ReadAllBytes(path).AsSpan();
        Assert.True(file[..4].SequenceEqual("FORM"u8));
        Assert.Equal((uint)(file.Length - 8), BinaryPrimitives.ReadUInt32BigEndian(file[4..]));
        Assert.True(file[8..12].SequenceEqual("AIFF"u8));
        Assert.True(file[12..16].SequenceEqual("COMM"u8));
        Assert.Equal(18u, BinaryPrimitives.ReadUInt32BigEndian(file[16..]));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(file[20..]));
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32BigEndian(file[22..]));
        Assert.Equal(24, BinaryPrimitives.ReadUInt16BigEndian(file[26..]));
        Assert.Equal(extended, file[28..38].ToArray());
        Assert.True(file[38..42].SequenceEqual("SSND"u8));
        Assert.Equal((uint)(8 + 11 * 2 * 3), BinaryPrimitives.ReadUInt32BigEndian(file[42..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(file[46..])); // offset
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(file[50..])); // block size
        Assert.Equal(54 + 11 * 2 * 3, file.Length);
    }

    [Fact]
    public void The_AIFF_C_header_names_the_float_compression() {
        var path = Path.Combine(folder, "f.aif");
        AudioFileWriter.WriteFloat(path, AudioFormat.Aiff, 48_000, Floats(1, 5));
        var file = File.ReadAllBytes(path).AsSpan();
        Assert.True(file[..4].SequenceEqual("FORM"u8));
        Assert.Equal((uint)(file.Length - 8), BinaryPrimitives.ReadUInt32BigEndian(file[4..]));
        Assert.True(file[8..12].SequenceEqual("AIFC"u8));
        Assert.True(file[12..16].SequenceEqual("FVER"u8));
        Assert.Equal(0xA2805140u, BinaryPrimitives.ReadUInt32BigEndian(file[20..]));
        Assert.True(file[24..28].SequenceEqual("COMM"u8));
        var commSize = (int)BinaryPrimitives.ReadUInt32BigEndian(file[28..]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(file[32..]));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32BigEndian(file[34..]));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16BigEndian(file[38..]));
        Assert.Equal(new byte[] { 0x40, 0x0E, 0xBB, 0x80, 0, 0, 0, 0, 0, 0 }, file[40..50].ToArray());
        Assert.True(file[50..54].SequenceEqual("fl32"u8));
        Assert.Equal(21, file[54]);
        Assert.Equal("32-bit floating point", System.Text.Encoding.ASCII.GetString(file.Slice(55, 21)));
        Assert.Equal(0, commSize & 1);
        var ssnd = 32 + commSize;
        Assert.True(file.Slice(ssnd, 4).SequenceEqual("SSND"u8));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(file[(ssnd + 8)..]));
    }

    [Fact]
    public void Odd_sized_data_is_padded() {
        var path = Path.Combine(folder, "odd.aiff");
        AudioFileWriter.WriteInteger(path, AudioFormat.Aiff, 48_000, 24, Integers(24, 1, 3));
        Assert.Equal(54 + 9 + 1, new FileInfo(path).Length);
        Assert.Equal(3, AiffReader.Read(path).Channels[0].Length);
        var wav = Path.Combine(folder, "odd.wav");
        AudioFileWriter.WriteInteger(wav, AudioFormat.Wav, 48_000, 24, Integers(24, 1, 3));
        Assert.Equal(44 + 9 + 1, new FileInfo(wav).Length);
        Assert.Equal(3, ReadWav(wav, out _)[0].Length);
    }

    [Fact]
    public void Dithered_audio_survives_the_whole_chain() {
        var input = Signals.Sine(48_000, 1000, -1, 4800);
        var quantised = Dither.Quantize([input, input], 16, DitherMode.Tpdf, 9);
        var path = Path.Combine(folder, "chain.aiff");
        AudioFileWriter.WriteInteger(path, AudioFormat.Aiff, 48_000, 16, quantised);
        var (back, _, _) = AiffReader.Read(path);
        for (var i = 0; i < input.Length; i++) {
            Assert.Equal(input[i], back[0][i], 2.0 / 32768);
        }
    }

    static void AssertIntegers(int[][] written, float[][] back, int bits) {
        var scale = Math.Pow(2, bits - 1);
        Assert.Equal(written.Length, back.Length);
        for (var c = 0; c < written.Length; c++) {
            Assert.Equal(written[c].Length, back[c].Length);
            for (var i = 0; i < written[c].Length; i++) {
                Assert.Equal(written[c][i], (int)(back[c][i] * scale));
            }
        }
    }
}

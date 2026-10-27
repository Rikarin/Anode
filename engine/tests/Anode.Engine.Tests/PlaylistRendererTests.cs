using Anode.Engine.Streaming;
using Anode.Ipc.Segments;
using System.Buffers.Binary;

namespace Anode.Engine.Tests;

public sealed unsafe class PlaylistRendererTests : IDisposable {
    readonly string dir = TestAudio.TempDirectory();

    public void Dispose() => Directory.Delete(dir, true);

    static float[] Render(AudioPlaylistEntry[] playlist, StreamFile?[] files, long position, int frames, int channels) {
        using var renderer = new PlaylistRenderer(DiskStreamer.ReadFrames);
        var output = new float[frames * channels];
        fixed (AudioPlaylistEntry* p = playlist) {
            renderer.Render(new(null, 0, p, playlist.Length), files, position, frames, channels, output);
        }

        return output;
    }

    [Fact]
    public void Sixteen_bit_and_float_files_decode_exactly_and_map_to_the_tracks_channels() {
        var pcm16 = new byte[1000 * 2 * 2];
        var float32 = new byte[1000 * 4];
        for (var i = 0; i < 1000; i++) {
            BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(4 * i), (short)(i * 30 - 15_000));
            BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(4 * i + 2), (short)(15_000 - i * 30));
            BinaryPrimitives.WriteSingleLittleEndian(float32.AsSpan(4 * i), i / 1000f - 0.5f);
        }

        using var stereo16 = new StreamFile(0, TestAudio.WriteRaw(dir, "s16.wav", 1, 16, 2, pcm16));
        using var monoFloat = new StreamFile(1, TestAudio.WriteRaw(dir, "f32.wav", 3, 32, 1, float32));
        Assert.Equal(SampleEncoding.Pcm16, stereo16.Format.Encoding);
        Assert.Equal(SampleEncoding.Float32, monoFloat.Format.Encoding);
        StreamFile?[] files = [stereo16, monoFloat];

        // A stereo file on a mono track is averaged; a mono file on a stereo track feeds both sides.
        var mono = Render([TestAudio.Region(100, 1000, 0)], files, 0, 300, 1);
        Assert.Equal(0f, mono[99]);
        for (var f = 100; f < 300; f++) {
            var l = (short)((f - 100) * 30 - 15_000) * (1f / 32_768f);
            var r = (short)(15_000 - (f - 100) * 30) * (1f / 32_768f);
            Assert.Equal((l + r) / 2 * 1f, mono[f]);
        }

        var stereo = Render([TestAudio.Region(0, 1000, 1, 10, 0.5f)], files, 0, 100, 2);
        for (var f = 0; f < 100; f++) {
            var s = ((f + 10) / 1000f - 0.5f) * 0.5f;
            Assert.Equal(s, stereo[2 * f]);
            Assert.Equal(s, stereo[2 * f + 1]);
        }
    }

    [Fact]
    public void Regions_past_the_files_end_and_missing_files_are_silent() {
        using var file = new StreamFile(0, TestAudio.Write24(dir, "short.wav", 1, 500, 4));
        var output = Render(
            [TestAudio.Region(0, 1_000, 0), TestAudio.Region(1_000, 1_000, 7)],
            [file],
            0,
            2_000,
            1
        );
        for (var f = 0; f < 500; f++) {
            Assert.Equal(TestAudio.Sample(f, 4), output[f]);
        }

        Assert.All(output[500..], v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Fades_are_linear_from_and_to_zero() {
        var entry = TestAudio.Region(0, 100, 0, 0, 0.5f, 10, 20);
        Assert.Equal(0f, PlaylistRenderer.Level(entry, 0));
        Assert.Equal(0.25f, PlaylistRenderer.Level(entry, 5));
        Assert.Equal(0.5f, PlaylistRenderer.Level(entry, 10));
        Assert.Equal(0.5f, PlaylistRenderer.Level(entry, 79));
        Assert.Equal(0.5f * (19f / 20), PlaylistRenderer.Level(entry, 80));
        Assert.Equal(0f, PlaylistRenderer.Level(entry, 99));
    }

    [Fact]
    public void A_wav_without_a_data_chunk_or_with_an_unknown_encoding_is_refused() {
        var bad = TestAudio.WriteRaw(dir, "alaw.wav", 6, 8, 1, new byte[10]);
        Assert.Throws<InvalidDataException>(() => new StreamFile(0, bad));
        var text = Path.Combine(dir, "text.wav");
        File.WriteAllText(text, "not audio at all");
        Assert.Throws<InvalidDataException>(() => new StreamFile(0, text));
    }
}

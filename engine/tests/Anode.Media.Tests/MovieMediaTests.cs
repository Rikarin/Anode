using Anode.Contracts;
using Anode.Dsp.IO;
using Anode.Media.Ffmpeg;
using System.Globalization;

namespace Anode.Media.Tests;

/// <summary>
///     Every media operation against the golden path's generated test movie (<see cref="MediaFixture.Spec" />): probe
///     values exact, beeps on their frames within 1 ms, thumbnails of the requested frames, scene cuts within one frame,
///     remux with the video packets unchanged and the new audio within 1 ms.
/// </summary>
public sealed class MovieMediaTests(MediaFixture media) : IClassFixture<MediaFixture> {
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_probe_reads_the_test_movie_exactly() {
        var probe = await MovieMedia.ProbeAsync(media.Require(), media.Movie, Token);

        Assert.Equal("h264", probe.VideoCodec);
        Assert.Equal((640, 360), (probe.Width, probe.Height));
        Assert.Equal(new(25, 1), probe.FrameRate);
        Assert.Equal(150, probe.Frames);
        Assert.Equal(10.0, probe.TimecodeStartSeconds, 9);
        Assert.Equal(6.0, probe.DurationSeconds, 6);
        Assert.Equal(new("aac", 2, 48_000), probe.Audio);
    }

    [Fact]
    public async Task A_file_without_video_is_refused() {
        var runner = media.Require();
        var wav = media.NewPath(".wav");
        MediaFixture.WriteWav(wav, new float[9600], 2, 48_000);

        var error = await Assert.ThrowsAsync<FfmpegException>(() => MovieMedia.ProbeAsync(runner, wav, Token));
        Assert.Contains("no video", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    [InlineData(96_000)]
    public async Task Extracted_beeps_land_on_their_frames_within_a_millisecond(int sampleRate) {
        var wav = media.NewPath(".wav");
        var reports = new List<double>();

        await MovieMedia.ExtractAudioAsync(media.Require(), media.Movie, wav, sampleRate, new Inline(reports), Token);

        var audio = WavReader.Read(wav);
        Assert.Equal(sampleRate, audio.SampleRate);
        Assert.Equal(2, audio.Channels.Length);
        Assert.Equal(6.0 * sampleRate, audio.Frames, sampleRate / 1000.0);
        AssertBeeps(audio.Channels[0], sampleRate, [0, 1, 2, 3, 4, 5], 0);
        Assert.Equal(1, reports[^1]);
    }

    [Fact]
    public async Task Audio_that_starts_after_the_first_frame_is_padded_to_it() {
        var runner = media.Require();
        var late = media.NewPath(".mov");
        await runner.RunFfmpegAsync(
            [
                "-i", media.Movie, "-itsoffset", "0.5", "-i", media.Movie, "-map", "0:v", "-map", "1:a", "-c", "copy",
                late
            ],
            TimeSpan.FromMinutes(1),
            cancellation: Token
        );
        var wav = media.NewPath(".wav");

        await MovieMedia.ExtractAudioAsync(runner, late, wav, 48_000, cancellation: Token);

        AssertBeeps(WavReader.Read(wav).Channels[0], 48_000, [0, 1, 2, 3, 4, 5], 0.5);
    }

    [Fact]
    public async Task Thumbnails_show_the_requested_frames() {
        var runner = media.Require();
        var folder = Path.Combine(media.Folder, "thumbnails");
        long[] frames = [75, 0, 39, 40, 149];

        var paths = await MovieMedia.ThumbnailsAsync(runner, media.Movie, frames, 90, folder, Token);

        Assert.Equal(frames.Select(f => Path.Combine(folder, $"{f}.jpg")), paths);
        foreach (var (frame, path) in frames.Zip(paths)) {
            var thumbnail = await Gray(runner, path, null, 90);
            Assert.Equal(160 * 90, thumbnail.Length);
            var same = Difference(thumbnail, await Gray(runner, media.Movie, frame, 90));
            var before = frame > 0
                ? Difference(thumbnail, await Gray(runner, media.Movie, frame - 1, 90))
                : double.MaxValue;
            var after = frame < 149
                ? Difference(thumbnail, await Gray(runner, media.Movie, frame + 1, 90))
                : double.MaxValue;
            Assert.True(same < 3, $"frame {frame}: {same:0.00} grey levels from the decoded frame");
            Assert.True(
                same < before && same < after,
                $"frame {frame}: {same:0.00}, neighbours {before:0.00}, {after:0.00}"
            );
        }
    }

    [Fact]
    public async Task A_thumbnail_past_the_end_is_refused() {
        var runner = media.Require();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            MovieMedia.ThumbnailsAsync(runner, media.Movie, [150], 64, media.NewPath(string.Empty), Token)
        );
    }

    [Fact]
    public async Task Scene_cuts_are_found_at_their_frames() {
        var reports = new List<double>();

        var cuts = await MovieMedia.DetectScenesAsync(media.Require(), media.Movie, 0.3, new Inline(reports), Token);

        Assert.Equal(MediaFixture.Spec.SceneCutFrames, cuts);
        Assert.Equal(1, reports[^1]);
    }

    [Theory]
    [InlineData(true, MovieAudioCodec.Aac, ".mov", "aac")]
    [InlineData(true, MovieAudioCodec.Pcm, ".mov", "pcm_s24le")]
    [InlineData(true, MovieAudioCodec.Pcm, ".mp4", "aac")]
    [InlineData(false, MovieAudioCodec.Aac, ".mov", "aac")]
    public async Task A_remux_keeps_the_video_and_aligns_the_new_audio(
        bool replace,
        MovieAudioCodec codec,
        string extension,
        string written
    ) {
        var runner = media.Require();

        // The new soundtrack: a burst a quarter of a second after each second, so it differs from the movie's.
        const int rate = 48_000;
        var samples = new float[6 * rate * 2];
        for (var n = 0; n < 6 * rate; n++) {
            var t = n % rate - rate / 4;
            var value = t is >= 0 and < 2_400 ? (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * t / rate)) : 0;
            samples[2 * n] = samples[2 * n + 1] = value;
        }

        var wav = media.NewPath(".wav");
        MediaFixture.WriteWav(wav, samples, 2, rate);
        var output = media.NewPath(extension);
        var reports = new List<double>();

        await MovieMedia.RemuxAsync(runner, media.Movie, wav, output, replace, codec, new Inline(reports), Token);

        Assert.Equal(await VideoPackets(runner, media.Movie), await VideoPackets(runner, output));
        var probe = await MovieMedia.ProbeAsync(runner, output, Token);
        Assert.Equal(150, probe.Frames);
        Assert.Equal(10.0, probe.TimecodeStartSeconds, 9);
        var streams = await AudioCodecs(runner, output);
        Assert.Equal(replace ? [written] : ["aac", written], streams);
        var read = media.NewPath(".wav");
        await runner.RunFfmpegAsync(
            [
                "-copyts", "-i", output, "-map", $"0:a:{streams.Count - 1}",
                "-af", "aresample=48000:first_pts=0", "-c:a", "pcm_f32le", read
            ],
            TimeSpan.FromMinutes(1),
            cancellation: Token
        );
        AssertBeeps(WavReader.Read(read).Channels[0], rate, [0, 1, 2, 3, 4, 5], 0.25);
        Assert.Equal(1, reports[^1]);
    }

    [Fact]
    public async Task The_test_movie_follows_its_spec_at_another_rate_and_with_openh264() {
        var runner = media.Require();
        var spec = new TestMovieSpec(
            2,
            [24],
            new(30_000, 1001),
            320,
            180,
            3600,
            AudioSampleRate: 44_100,
            VideoEncoder: "libopenh264"
        );
        var path = media.NewPath(".mp4");
        try {
            await MovieMedia.GenerateTestMovieAsync(runner, path, spec, Token);
        } catch (FfmpegException e) when (e.StandardError.Contains("Unknown encoder", StringComparison.Ordinal)) {
            Assert.Skip("This FFmpeg has no libopenh264.");
        }

        var probe = await MovieMedia.ProbeAsync(runner, path, Token);
        Assert.Equal(
            ("h264", 320, 180, new VideoFrameRate(30_000, 1001), 60L, new MovieAudio("aac", 2, 44_100)),
            (probe.VideoCodec, probe.Width, probe.Height, probe.FrameRate, probe.Frames, probe.Audio)
        );
        // 3600 s is not a whole number of frames at 29.97: the label holds the nearest frame.
        Assert.Equal(3600, probe.TimecodeStartSeconds, 1001 / 30_000.0);
        Assert.Equal([24L], await MovieMedia.DetectScenesAsync(runner, path, 0.3, cancellation: Token));
    }

    [Theory]
    [InlineData("00:00:10:00", 25, 1, 10.0)]
    [InlineData("01:00:00:00", 30_000, 1001, 108_000 * 1001 / 30_000.0)]
    [InlineData("01:00:00;00", 30_000, 1001, 107_892 * 1001 / 30_000.0)]
    [InlineData("00:01:00;02", 30_000, 1001, 1800 * 1001 / 30_000.0)]
    [InlineData("10:00:00:12", 24_000, 1001, (36_000 * 24 + 12) * 1001 / 24_000.0)]
    [InlineData("nonsense", 25, 1, 0.0)]
    public void Timecode_labels_count_frames_as_the_engine_does(
        string label,
        int numerator,
        int denominator,
        double seconds
    ) =>
        Assert.Equal(seconds, MovieMedia.TimecodeSeconds(label, new(numerator, denominator)), 9);

    /// <summary>Bursts at <paramref name="seconds" /> (+ <paramref name="offset" />), each within 1 ms.</summary>
    static void AssertBeeps(float[] samples, int sampleRate, double[] seconds, double offset) {
        var onsets = MediaFixture.Onsets(samples, sampleRate);
        Assert.Equal(seconds.Length, onsets.Count);
        var errors = seconds.Zip(onsets).Select(p => (p.Second / sampleRate - (p.First + offset)) * 1000);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"bursts at {sampleRate} Hz off by {string.Join(", ", errors.Select(static e => e.ToString("0.000", CultureInfo.InvariantCulture)))} ms"
        );
        foreach (var (second, onset) in seconds.Zip(onsets)) {
            var error = (onset - (second + offset) * sampleRate) / sampleRate * 1000;
            Assert.True(Math.Abs(error) < 1, $"the burst at {second + offset} s is {error:0.000} ms off");
        }
    }

    /// <summary>A picture as 8-bit grey at <paramref name="height" />: an image file, or a movie's frame.</summary>
    async Task<byte[]> Gray(FfmpegRunner runner, string path, long? frame, int height) {
        var raw = media.NewPath(".gray");
        var filter = frame is null
            ? $"scale=-2:{height}"
            : $"select=eq(n\\,{frame.Value.ToString(CultureInfo.InvariantCulture)}),scale=-2:{height}:flags=bicubic";
        await runner.RunFfmpegAsync(
            ["-i", path, "-vf", filter, "-frames:v", "1", "-pix_fmt", "gray", "-f", "rawvideo", raw],
            TimeSpan.FromMinutes(1),
            cancellation: Token
        );
        return await File.ReadAllBytesAsync(raw);
    }

    static double Difference(byte[] a, byte[] b) {
        Assert.Equal(a.Length, b.Length);
        return a.Zip(b).Average(static p => Math.Abs(p.First - p.Second));
    }

    /// <summary>Every video packet's timestamps, flags and SHA-256 (<c>ffprobe -show_data_hash</c>).</summary>
    static async Task<string> VideoPackets(FfmpegRunner runner, string path) {
        var result = await runner.RunFfprobeAsync(
            [
                "-v", "error", "-select_streams", "v:0", "-show_data_hash", "sha256", "-show_entries",
                "packet=pts,dts,duration,flags,size,data_hash", "-of", "csv=p=0", path
            ],
            TimeSpan.FromMinutes(1),
            Token
        );
        Assert.Equal(150, result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        return result.StandardOutput;
    }

    static async Task<List<string>> AudioCodecs(FfmpegRunner runner, string path) {
        var result = await runner.RunFfprobeAsync(
            ["-v", "error", "-select_streams", "a", "-show_entries", "stream=codec_name", "-of", "csv=p=0", path],
            TimeSpan.FromMinutes(1),
            Token
        );
        return [
            ..result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ];
    }

    /// <summary>Progress collected synchronously (<see cref="Progress{T}" /> posts to the thread pool).</summary>
    sealed class Inline(List<double> reports) : IProgress<double> {
        public void Report(double value) {
            lock (reports) {
                reports.Add(value);
            }
        }
    }
}

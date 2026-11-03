using Anode.Media.Ffmpeg;
using System.Buffers.Binary;

namespace Anode.Media.Tests;

/// <summary>
///     The FFmpeg tests' shared state: the tools (<see cref="FfmpegTools.Locate" />: <c>ANODE_FFMPEG</c>, beside the
///     tests, or <c>engine/artifacts/ffmpeg/&lt;platform&gt;</c> from <c>node scripts/fetch-ffmpeg.mjs</c>) and the
///     golden path's test movie, generated once into a temporary folder. Without the tools every test that needs them
///     is skipped with how to get them.
/// </summary>
public sealed class MediaFixture : IDisposable {
    /// <summary>The golden path's movie: 6 s at 25 fps, scenes from frames 40 and 100, timecode 00:00:10:00.</summary>
    public static readonly TestMovieSpec Spec = new(6, [40, 100], TimecodeStartSeconds: 10);

    readonly Lazy<string> movie;

    public MediaFixture() {
        Folder = Directory.CreateTempSubdirectory("anode-media-").FullName;
        var tools = FfmpegTools.Locate();
        Runner = tools is null ? null : new FfmpegRunner(tools);
        movie = new(() => {
                var path = Path.Combine(Folder, "Test Movie.mov");
                MovieMedia.GenerateTestMovieAsync(Require(), path, Spec).GetAwaiter().GetResult();
                return path;
            }
        );
    }

    public string Folder { get; }

    public FfmpegRunner? Runner { get; }

    /// <summary>The generated test movie (made on first use).</summary>
    public string Movie {
        get {
            Require();
            return movie.Value;
        }
    }

    public void Dispose() => Directory.Delete(Folder, true);

    /// <summary>The runner, or the test is skipped.</summary>
    public FfmpegRunner Require() {
        Assert.SkipWhen(
            Runner is null,
            "No FFmpeg: run `node scripts/fetch-ffmpeg.mjs` (the pinned LGPL build) or set ANODE_FFMPEG to a folder with "
            + "ffmpeg and ffprobe."
        );
        return Runner!;
    }

    /// <summary>A new file name in the fixture's folder.</summary>
    public string NewPath(string extension) => Path.Combine(Folder, $"{Guid.NewGuid():N}{extension}");

    /// <summary>Writes interleaved float samples as a 32-bit float WAV.</summary>
    public static void WriteWav(string path, float[] interleaved, int channels, int sampleRate) {
        var data = interleaved.Length * 4;
        var bytes = new byte[44 + data];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 36 + data);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 3);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), sampleRate * channels * 4);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), (short)(channels * 4));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 32);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), data);
        for (var i = 0; i < interleaved.Length; i++) {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(44 + 4 * i), interleaved[i]);
        }

        File.WriteAllBytes(path, bytes);
    }

    /// <summary>
    ///     Where each 1 kHz burst of <paramref name="samples" /> (at half scale) starts: the sample at which the RMS over
    ///     the last millisecond first reaches half the burst's, less a quarter of that millisecond (an ideal burst's RMS
    ///     there is half its own when a quarter of the window holds it). Robust to a codec's low-level pre-echo.
    /// </summary>
    public static List<double> Onsets(float[] samples, int sampleRate, double amplitude = 0.5) {
        var length = (int)Math.Round(sampleRate / 1000.0);
        var threshold = 0.5 * amplitude / Math.Sqrt(2);
        var energy = new double[samples.Length + 1];
        for (var n = 0; n < samples.Length; n++) {
            energy[n + 1] = energy[n] + samples[n] * (double)samples[n];
        }

        // n is the end (exclusive) of the window [n − length, n), silent before sample 0.
        var onsets = new List<double>();
        for (var n = 1; n <= samples.Length; n++) {
            if (Math.Sqrt((energy[n] - energy[Math.Max(0, n - length)]) / length) >= threshold) {
                onsets.Add(n - length / 4.0);
                n += sampleRate / 2; // bursts are at least half a second apart
            }
        }

        return onsets;
    }
}

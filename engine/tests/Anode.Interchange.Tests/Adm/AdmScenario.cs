using Anode.Contracts;
using Anode.Interchange.Adm;
using Anode.Model.Audio;
using System.Runtime.CompilerServices;

namespace Anode.Interchange.Tests.Adm;

/// <summary>
///     A scratch folder with an ADM scene's sources: noise beds and objects written as WAV files, and the golden path's
///     object (a mono object moving from front left to rear right overhead over the scene, in blocks of 20 ms).
/// </summary>
sealed class AdmScenario : IDisposable {
    public const int Rate = 48_000;

    public AdmScenario() {
        Folder = Path.Combine(Path.GetTempPath(), "anode-adm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    public void Dispose() {
        try {
            Directory.Delete(Folder, true);
        } catch (IOException) {
            // A scratch folder left behind is the system temp folder's to clean.
        } catch (UnauthorizedAccessException) {
            // As above.
        }
    }

    public string PathOf(string name) => Path.Combine(Folder, name);

    /// <summary>
    ///     A WAV of independent noise per channel, channel c at <paramref name="levels" />[c] (RMS of the uniform noise ×
    ///     level).
    /// </summary>
    public string Noise(
        string name,
        int channels,
        long frames,
        float[] levels,
        int seed,
        WavEncoding encoding = WavEncoding.Float32
    ) {
        var path = PathOf(name);
        using var writer = new WavWriter(path, channels, Rate, encoding);
        var random = new Random(seed);
        var block = new float[4800 * channels];
        for (long start = 0; start < frames; start += 4800) {
            var n = (int)Math.Min(4800, frames - start);
            for (var i = 0; i < n * channels; i++) {
                block[i] = (random.NextSingle() * 2 - 1) * levels[i % channels];
            }

            writer.Write(block.AsSpan(0, n * channels));
        }

        return path;
    }

    /// <summary>The golden path's object path: (30°, 0°) to (−135°, 30°), linear, over <paramref name="frames" />.</summary>
    public static IReadOnlyList<AdmBlock> FrontLeftToRearRightOverhead(long frames) =>
        AdmBlocks.FromPoints([(0, 30, 0, 1, 0, true), (frames, -135, 30, 1, 0, false)], frames, Rate);

    /// <summary>A 7.1.4 bed (quiet noise) and the moving object (louder noise).</summary>
    public AdmScene GoldenPath(long frames) {
        var bed = Noise("bed.wav", 12, frames, [..Enumerable.Range(0, 12).Select(static c => 0.02f + 0.01f * c)], 1);
        var mover = Noise("object.wav", 1, frames, [0.5f], 2);
        return new(
            "Golden path",
            Rate,
            frames,
            [new("Bed", ChannelFormat.Surround714, bed)],
            [new("Mover", mover, 1, FrontLeftToRearRightOverhead(frames))]
        );
    }

    public static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "..", ".."));
}

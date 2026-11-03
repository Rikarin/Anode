using Anode.Contracts;
using Anode.Dsp.Spatial;
using Anode.Interchange.Adm;
using Anode.Media.Ffmpeg;
using DspLayout = Anode.Dsp.Spatial.SpeakerLayout;

namespace Anode.Server.Projects;

/// <summary>
///     The movie operations the jobs use (docs/11-phase-10.md, workstream E's <see cref="MovieMedia" /> over FFmpeg): a
///     seam so the jobs' tests run with doubles. <see cref="Available" /> is false without FFmpeg (the media endpoints
///     answer 503).
/// </summary>
public interface IMovieMedia {
    bool Available { get; }

    Task<MovieProbe> ProbeAsync(string moviePath, CancellationToken cancellation);

    Task ExtractAudioAsync(
        string moviePath,
        string wavPath,
        int sampleRate,
        IProgress<double>? progress,
        CancellationToken cancellation
    );

    Task<IReadOnlyList<string>> ThumbnailsAsync(
        string moviePath,
        IReadOnlyList<long> frames,
        int height,
        string folder,
        CancellationToken cancellation
    );

    Task<IReadOnlyList<long>> DetectScenesAsync(
        string moviePath,
        double threshold,
        IProgress<double>? progress,
        CancellationToken cancellation
    );

    Task RemuxAsync(
        string moviePath,
        string wavPath,
        string outputPath,
        bool replace,
        MovieAudioCodec codec,
        IProgress<double>? progress,
        CancellationToken cancellation
    );
}

/// <summary>
///     <see cref="IMovieMedia" /> over <see cref="MovieMedia" /> with the FFmpeg <see cref="FfmpegTools.Locate" /> finds
///     (in <c>Anode:FfmpegDirectory</c> when set, else beside the Server, then the development fetch folder), looked
///     for once.
/// </summary>
public sealed class FfmpegMovieMedia(IConfiguration configuration) : IMovieMedia {
    readonly Lazy<FfmpegRunner?> runner = new(() =>
        FfmpegTools.Locate(configuration["Anode:FfmpegDirectory"]) is { } tools ? new(tools) : null
    );

    public bool Available => runner.Value is not null;

    FfmpegRunner Runner =>
        runner.Value ?? throw new ProjectException(StatusCodes.Status503ServiceUnavailable, "FFmpeg is not available.");

    public Task<MovieProbe> ProbeAsync(string moviePath, CancellationToken cancellation) =>
        MovieMedia.ProbeAsync(Runner, moviePath, cancellation);

    public Task ExtractAudioAsync(
        string moviePath,
        string wavPath,
        int sampleRate,
        IProgress<double>? progress,
        CancellationToken cancellation
    ) =>
        MovieMedia.ExtractAudioAsync(Runner, moviePath, wavPath, sampleRate, progress, cancellation);

    public Task<IReadOnlyList<string>> ThumbnailsAsync(
        string moviePath,
        IReadOnlyList<long> frames,
        int height,
        string folder,
        CancellationToken cancellation
    ) =>
        MovieMedia.ThumbnailsAsync(Runner, moviePath, frames, height, folder, cancellation);

    public Task<IReadOnlyList<long>> DetectScenesAsync(
        string moviePath,
        double threshold,
        IProgress<double>? progress,
        CancellationToken cancellation
    ) =>
        MovieMedia.DetectScenesAsync(Runner, moviePath, threshold, progress, cancellation);

    public Task RemuxAsync(
        string moviePath,
        string wavPath,
        string outputPath,
        bool replace,
        MovieAudioCodec codec,
        IProgress<double>? progress,
        CancellationToken cancellation
    ) =>
        MovieMedia.RemuxAsync(Runner, moviePath, wavPath, outputPath, replace, codec, progress, cancellation);
}

/// <summary>
///     The ADM BWF writer and reader and the down-mix the jobs use (workstream D's <see cref="AdmWriter" />,
///     <see cref="AdmReader" /> and <see cref="Downmixer" />): a seam for the jobs' tests.
/// </summary>
public interface ISpatialFiles {
    void WriteAdm(AdmScene scene, string path, int bitDepth);

    AdmScene ReadAdm(string path, string mediaFolder);

    /// <summary>The gain of <paramref name="from" />'s channel <c>input</c> in stereo channel <c>output</c> (ITU-R BS.775).</summary>
    float[,] StereoDownmix(ChannelFormat from);
}

/// <summary><see cref="ISpatialFiles" /> over <c>Anode.Interchange.Adm</c> and <c>Anode.Dsp.Spatial</c>.</summary>
public sealed class SpatialFiles : ISpatialFiles {
    public void WriteAdm(AdmScene scene, string path, int bitDepth) => AdmWriter.Write(scene, path, bitDepth);

    public AdmScene ReadAdm(string path, string mediaFolder) => AdmReader.Read(path, mediaFolder);

    public float[,] StereoDownmix(ChannelFormat from) {
        var source = DspLayout.Of((SpeakerFormat)from);
        var mixer = new Downmixer(source, DspLayout.Of(SpeakerFormat.Stereo));
        var gains = new float[2, source.ChannelCount];
        for (var output = 0; output < 2; output++) {
            for (var input = 0; input < source.ChannelCount; input++) {
                gains[output, input] = mixer.Gain(output, input);
            }
        }

        return gains;
    }
}

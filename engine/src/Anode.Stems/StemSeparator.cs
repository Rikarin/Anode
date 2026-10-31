using Anode.Contracts;

namespace Anode.Stems;

/// <summary>The separation models (docs/adr/0010-stem-splitter.md).</summary>
public enum StemModel {
    /// <summary><c>htdemucs</c>: drums, bass, other, vocals.</summary>
    FourStems,

    /// <summary><c>htdemucs_6s</c>: drums, bass, other, vocals, guitar, piano.</summary>
    SixStems
}

/// <summary>Where ONNX Runtime runs the model.</summary>
public enum StemExecutionProvider {
    /// <summary>The CPU provider: the default, everywhere.</summary>
    Cpu,

    /// <summary>
    ///     CoreML on macOS (ignored elsewhere). Measured slower than the CPU on the HTDemucs graph
    ///     (docs/adr/0010-stem-splitter.md), so only on request.
    /// </summary>
    CoreMl
}

/// <summary>How a separator runs.</summary>
public sealed record StemSeparatorOptions {
    public static StemSeparatorOptions Default { get; } = new();

    public StemExecutionProvider Provider { get; init; } = StemExecutionProvider.Cpu;

    /// <summary>ONNX Runtime's threads for one run; 0 lets it choose (one per physical core).</summary>
    public int Threads { get; init; }
}

/// <summary>
///     A loaded separation model (docs/adr/0010-stem-splitter.md, workstream E): the STFT and iSTFT in C# around the
///     ONNX graph, chunks of 7.8 s with overlap-add (memory bounded for any length), resampling in and out of the
///     model's rate. Separation never runs on the audio thread. One call at a time per instance.
/// </summary>
public interface IStemSeparator : IDisposable {
    StemModel Model { get; }

    /// <summary>The stems the model makes, in its output order.</summary>
    IReadOnlyList<StemKind> Stems { get; }

    /// <summary>The model's sample rate (44100 for HTDemucs); other rates are resampled in and out.</summary>
    int SampleRate { get; }

    /// <summary>
    ///     Separates <paramref name="channels" /> (planar, <c>channels[c][frame]</c>, 1 or 2 channels of equal length) at
    ///     <paramref name="sampleRate" /> into <paramref name="stems" /> (a subset of <see cref="Stems" />): per stem, as
    ///     many channels of the same length and rate as the input. Reports progress 0…1 (after each chunk);
    ///     cancellation throws <see cref="OperationCanceledException" /> (checked between chunks and inside a run).
    ///     Deterministic for a model, input and provider.
    /// </summary>
    IReadOnlyDictionary<StemKind, float[][]> Separate(
        float[][] channels,
        int sampleRate,
        IReadOnlyList<StemKind> stems,
        IProgress<double>? progress,
        CancellationToken cancellationToken
    );
}

/// <summary>Opens separation models.</summary>
public static class StemSeparators {
    static readonly StemKind[] Four = [StemKind.Drums, StemKind.Bass, StemKind.Other, StemKind.Vocals];

    static readonly StemKind[] Six = [
        StemKind.Drums, StemKind.Bass, StemKind.Other, StemKind.Vocals, StemKind.Guitar, StemKind.Piano
    ];

    /// <summary>The model that makes every stem of <paramref name="stems" />: six stems with guitar or piano, else four.</summary>
    public static StemModel ModelFor(IReadOnlyList<StemKind> stems) =>
        stems.Any(static s => s is StemKind.Guitar or StemKind.Piano) ? StemModel.SixStems : StemModel.FourStems;

    /// <summary>The stems <paramref name="model" /> makes, in its output order.</summary>
    public static IReadOnlyList<StemKind> StemsOf(StemModel model) => model == StemModel.SixStems ? Six : Four;

    /// <summary>
    ///     The model's file name in the models folder (<c>htdemucs.onnx</c>, <c>htdemucs_6s.onnx</c>), fetched by
    ///     checksum by <c>scripts/fetch-models.mjs</c> or by the server into the user library (<see cref="ModelStore" />).
    /// </summary>
    public static string FileName(StemModel model) =>
        model == StemModel.SixStems ? "htdemucs_6s.onnx" : "htdemucs.onnx";

    /// <summary>
    ///     Loads <paramref name="model" /> from <paramref name="path" /> (an ONNX file; a fixture model for tests); a
    ///     missing or damaged file, or one that does not make the model's stems, is an
    ///     <see cref="InvalidDataException" />.
    /// </summary>
    public static IStemSeparator Open(StemModel model, string path, StemSeparatorOptions? options = null) {
        var network = OnnxSegmentModel.Load(path, options ?? StemSeparatorOptions.Default);
        if (!network.Stems.Order().SequenceEqual(StemsOf(model).Order())) {
            network.Dispose();
            throw new InvalidDataException(
                $"'{path}' makes {string.Join(", ", network.Stems)}, not the stems of {model}."
            );
        }

        return new HtDemucsSeparator(model, network);
    }
}

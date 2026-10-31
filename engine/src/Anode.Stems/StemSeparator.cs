using Anode.Contracts;

namespace Anode.Stems;

/// <summary>The separation models (docs/adr/0010-stem-splitter.md).</summary>
public enum StemModel {
    /// <summary><c>htdemucs</c>: vocals, drums, bass, other.</summary>
    FourStems,

    /// <summary><c>htdemucs_6s</c>: vocals, drums, bass, other, guitar, piano.</summary>
    SixStems
}

/// <summary>
///     A loaded separation model (docs/adr/0010-stem-splitter.md, workstream E): the STFT and iSTFT in C# around the
///     ONNX graph, chunks of about 8 s with overlap-add (memory bounded for any length), resampling in and out of the
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
    ///     many channels of the same length and rate as the input. Reports progress 0…1; cancellation throws
    ///     <see cref="OperationCanceledException" />. Deterministic for a model, input and provider.
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
    /// <summary>The model that makes every stem of <paramref name="stems" />: six stems with guitar or piano, else four.</summary>
    public static StemModel ModelFor(IReadOnlyList<StemKind> stems) => throw Pending.WorkstreamE();

    /// <summary>
    ///     The model's file name in the models folder (<c>htdemucs.onnx</c>, <c>htdemucs_6s.onnx</c>), fetched by
    ///     checksum by <c>scripts/fetch-models.mjs</c> or by the server into the user library.
    /// </summary>
    public static string FileName(StemModel model) => throw Pending.WorkstreamE();

    /// <summary>
    ///     Loads <paramref name="model" /> from <paramref name="path" /> (an ONNX file; a fixture model for tests); a
    ///     missing or damaged file is an <see cref="InvalidDataException" />.
    /// </summary>
    public static IStemSeparator Open(StemModel model, string path) => throw Pending.WorkstreamE();
}

using Anode.Contracts;
using Microsoft.ML.OnnxRuntime;
using System.Globalization;

namespace Anode.Stems;

/// <summary>
///     The network between HTDemucs' STFT and iSTFT for one segment (scripts/export-demucs.py): the waveform
///     <c>mix</c> [1, 2, L] and its spectrogram <c>spec</c> [1, 4, 2048, T] in; the frequency branch
///     <c>spec_out</c> [1, S, 4, 2048, T] and the time branch <c>wave_out</c> [1, S, 2, L] out. A stem is
///     <c>wave_out + iSTFT(spec_out)</c>.
/// </summary>
interface ISegmentModel : IDisposable {
    /// <summary>The stems in output order.</summary>
    IReadOnlyList<StemKind> Stems { get; }

    int SampleRate { get; }

    /// <summary>L, the segment length in samples.</summary>
    int SegmentLength { get; }

    /// <summary>
    ///     Runs one segment: <paramref name="mix" /> (2·L, planar), <paramref name="spectrum" /> (4·2048·T) in;
    ///     <paramref name="spectrumOut" /> (S·4·2048·T) and <paramref name="waveOut" /> (S·2·L) overwritten.
    /// </summary>
    void Run(float[] mix, float[] spectrum, float[] spectrumOut, float[] waveOut, CancellationToken cancellationToken);
}

/// <summary>An exported HTDemucs (or a fixture with its inputs and outputs) on ONNX Runtime.</summary>
sealed class OnnxSegmentModel : ISegmentModel {
    static readonly string[] InputNames = ["mix", "spec"];
    static readonly string[] OutputNames = ["spec_out", "wave_out"];

    readonly InferenceSession session;
    readonly long[] mixShape;
    readonly long[] specShape;
    readonly long[] specOutShape;
    readonly long[] waveOutShape;

    OnnxSegmentModel(InferenceSession session, IReadOnlyList<StemKind> stems, int sampleRate, int length, int frames) {
        this.session = session;
        Stems = stems;
        SampleRate = sampleRate;
        SegmentLength = length;
        mixShape = [1, 2, length];
        specShape = [1, 4, Spectrogram.Bins, frames];
        specOutShape = [1, stems.Count, 4, Spectrogram.Bins, frames];
        waveOutShape = [1, stems.Count, 2, length];
    }

    public IReadOnlyList<StemKind> Stems { get; }

    public int SampleRate { get; }

    public int SegmentLength { get; }

    public void Run(
        float[] mix,
        float[] spectrum,
        float[] spectrumOut,
        float[] waveOut,
        CancellationToken cancellationToken
    ) {
        using var options = new RunOptions();
        using var stop = cancellationToken.Register(() => options.Terminate = true);
        using var mixValue = OrtValue.CreateTensorValueFromMemory(mix, mixShape);
        using var specValue = OrtValue.CreateTensorValueFromMemory(spectrum, specShape);
        using var specOutValue = OrtValue.CreateTensorValueFromMemory(spectrumOut, specOutShape);
        using var waveOutValue = OrtValue.CreateTensorValueFromMemory(waveOut, waveOutShape);
        try {
            session.Run(options, InputNames, [mixValue, specValue], OutputNames, [specOutValue, waveOutValue]);
        } catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested) {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public void Dispose() => session.Dispose();

    /// <summary>
    ///     Loads <paramref name="path" />, checking its inputs and outputs and reading its metadata (<c>anode.stems</c>,
    ///     <c>anode.sample_rate</c>); anything else is an <see cref="InvalidDataException" />.
    /// </summary>
    public static OnnxSegmentModel Load(string path, StemSeparatorOptions options) {
        if (!File.Exists(path)) {
            throw new InvalidDataException($"No model at '{path}'.");
        }

        using var sessionOptions = new SessionOptions();
        sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        if (options.Threads > 0) {
            sessionOptions.IntraOpNumThreads = options.Threads;
        }

        if (options.Provider == StemExecutionProvider.CoreMl && OperatingSystem.IsMacOS()) {
            sessionOptions.AppendExecutionProvider_CoreML(CoreMLFlags.COREML_FLAG_CREATE_MLPROGRAM);
        }

        InferenceSession session;
        try {
            session = new(path, sessionOptions);
        } catch (OnnxRuntimeException e) {
            throw new InvalidDataException($"'{path}' is not a model ONNX Runtime can load: {e.Message}", e);
        }

        try {
            var metadata = session.ModelMetadata.CustomMetadataMap;
            var stems = metadata.TryGetValue("anode.stems", out var names)
                ? names.Split(',').Select(ParseStem).ToArray()
                : throw new InvalidDataException($"'{path}' does not name its stems (anode.stems).");
            var sampleRate = metadata.TryGetValue("anode.sample_rate", out var rate)
                ? int.Parse(rate, CultureInfo.InvariantCulture)
                : 44100;
            var mix = Dimensions(session.InputMetadata, "mix", path);
            var spec = Dimensions(session.InputMetadata, "spec", path);
            var specOut = Dimensions(session.OutputMetadata, "spec_out", path);
            var waveOut = Dimensions(session.OutputMetadata, "wave_out", path);
            var length = mix is [1, 2, var l] ? l : throw Shape(path, "mix");
            var frames = (length + Spectrogram.Hop - 1) / Spectrogram.Hop;
            if (spec is not [1, 4, Spectrogram.Bins, var t] || t != frames) {
                throw Shape(path, "spec");
            }

            if (specOut is not [1, var s1, 4, Spectrogram.Bins, var t1] || s1 != stems.Length || t1 != frames) {
                throw Shape(path, "spec_out");
            }

            if (waveOut is not [1, var s2, 2, var l2] || s2 != stems.Length || l2 != length) {
                throw Shape(path, "wave_out");
            }

            return new(session, stems, sampleRate, length, frames);
        } catch {
            session.Dispose();
            throw;
        }
    }

    static int[] Dimensions(IReadOnlyDictionary<string, NodeMetadata> nodes, string name, string path) =>
        nodes.TryGetValue(name, out var node) && node.ElementType == typeof(float)
            ? node.Dimensions
            : throw new InvalidDataException($"'{path}' has no float tensor '{name}'.");

    static InvalidDataException Shape(string path, string name) =>
        new($"'{path}': '{name}' does not have HTDemucs' shape (scripts/export-demucs.py).");

    static StemKind ParseStem(string name) =>
        Enum.TryParse<StemKind>(name.Trim(), true, out var kind)
            ? kind
            : throw new InvalidDataException($"Unknown stem '{name}'.");
}

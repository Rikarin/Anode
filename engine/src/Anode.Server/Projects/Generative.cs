using Anode.Contracts;
using Anode.Dsp.Analysis.Chords;
using Anode.Dsp.Mastering;
using Anode.Generative;

namespace Anode.Server.Projects;

/// <summary>
///     The Session Player generator and its style library (docs/12-phase-11.md, workstream D's
///     <see cref="SessionPlayerGenerator" /> and <see cref="StyleLibrary" />): a seam so the server's tests run with
///     doubles. <see cref="Catalog" /> throws <see cref="ProjectException" /> (503) when the library cannot be loaded.
/// </summary>
public interface ISessionPlayerLibrary {
    SessionPlayerCatalog Catalog { get; }

    GeneratedNotes Generate(GenerationInput input);

    string Hash(GenerationInput input);

    Pattern ToPattern(IReadOnlyList<Note> notes, long lengthTicks);
}

/// <summary>
///     <see cref="ISessionPlayerLibrary" /> over the built-in <see cref="StyleLibrary" /> (loaded once, on first use)
///     and its <see cref="SessionPlayerGenerator" />.
/// </summary>
public sealed class BuiltinSessionPlayers : ISessionPlayerLibrary {
    readonly Lazy<SessionPlayerGenerator> generator = new(static () => new(StyleLibrary.Builtin()));

    SessionPlayerGenerator Generator {
        get {
            try {
                return generator.Value;
            } catch (Exception e) when (e is not OutOfMemoryException) {
                throw new ProjectException(
                    StatusCodes.Status503ServiceUnavailable,
                    $"The Session Player style library is not available: {e.Message}"
                );
            }
        }
    }

    public SessionPlayerCatalog Catalog => Generator.Library.Catalog;

    public GeneratedNotes Generate(GenerationInput input) => Generator.Generate(input);

    public string Hash(GenerationInput input) => Generator.Hash(input);

    public Pattern ToPattern(IReadOnlyList<Note> notes, long lengthTicks) =>
        SessionPlayerGenerator.ToPattern(notes, lengthTicks);
}

/// <summary>
///     Chord ID's recognisers and key detection (docs/adr/0014-session-players-and-chord-id.md, workstream D's
///     <c>Anode.Dsp.Analysis.Chords</c>): a seam so the jobs' tests run with doubles.
/// </summary>
public interface IChordRecognition {
    IReadOnlyList<RecognisedChord> FromNotes(
        IReadOnlyList<ChordNote> notes,
        IReadOnlyList<long> beats,
        long resolutionTicks,
        DetectedMusicalKey? key
    );

    IReadOnlyList<RecognisedChord> FromAudio(float[] mono, float sampleRate, IReadOnlyList<long> beats, DetectedMusicalKey? key);

    DetectedMusicalKey KeyOfNotes(IReadOnlyList<ChordNote> notes);

    DetectedMusicalKey KeyOfAudio(float[] mono, float sampleRate);
}

/// <summary><see cref="IChordRecognition" /> over <c>Anode.Dsp.Analysis.Chords</c>.</summary>
public sealed class DspChordRecognition : IChordRecognition {
    public IReadOnlyList<RecognisedChord> FromNotes(
        IReadOnlyList<ChordNote> notes,
        IReadOnlyList<long> beats,
        long resolutionTicks,
        DetectedMusicalKey? key
    ) =>
        MidiChordRecognizer.Recognise(notes, beats, resolutionTicks, key);

    public IReadOnlyList<RecognisedChord> FromAudio(
        float[] mono,
        float sampleRate,
        IReadOnlyList<long> beats,
        DetectedMusicalKey? key
    ) =>
        AudioChordRecognizer.Recognise(mono, sampleRate, beats, key);

    public DetectedMusicalKey KeyOfNotes(IReadOnlyList<ChordNote> notes) => KeyDetector.FromNotes(notes);

    public DetectedMusicalKey KeyOfAudio(float[] mono, float sampleRate) => KeyDetector.FromAudio(mono, sampleRate);
}

/// <summary>
///     The Mastering Assistant's analysis (workstream E's <see cref="MasteringAnalyzer" />): a seam so the analysis job's
///     tests run with doubles. The chain's design is the model's <c>IMasteringDesigner</c>.
/// </summary>
public interface IMasteringAnalysis {
    MasteringMeasurement Analyse(float[] left, float[] right, float sampleRate);

    IReadOnlyList<float> Curve(MasteringCharacter character);
}

/// <summary><see cref="IMasteringAnalysis" /> over <c>Anode.Dsp.Mastering</c>.</summary>
public sealed class DspMasteringAnalysis : IMasteringAnalysis {
    public MasteringMeasurement Analyse(float[] left, float[] right, float sampleRate) =>
        MasteringAnalyzer.Analyse(left, right, sampleRate);

    public IReadOnlyList<float> Curve(MasteringCharacter character) =>
        MasteringAnalyzer.Curve((MasteringCharacterKind)character);
}

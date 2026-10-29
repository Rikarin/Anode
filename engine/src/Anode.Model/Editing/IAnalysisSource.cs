using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     What edits that need a file's audio analysed ask for (docs/06-phase-5.md, "Server"): quantise audio, Strip
///     Silence, adapt tempo and follow run the analysis synchronously. The server's source analyses (through
///     <c>Anode.Dsp.Analysis</c>) and caches; tests use a deterministic fake.
/// </summary>
public interface IAnalysisSource {
    /// <summary>
    ///     The file's transients and tempo (beats and downbeats), analysed now when they are not cached; null when the
    ///     file cannot be analysed.
    /// </summary>
    FileAnalysis? Rhythm(AudioFile file);

    /// <summary>
    ///     The file's non-silent ranges (start inclusive, end exclusive, in file frames, ascending) for Strip Silence's
    ///     settings; null when the file cannot be analysed.
    /// </summary>
    IReadOnlyList<(long Start, long End)>? Silence(
        AudioFile file,
        double thresholdDb,
        double minTimeMs,
        double preAttackMs,
        double postReleaseMs
    );

    /// <summary>
    ///     The file's onsets for a groove template (docs/08-phase-7.md, "Grooves"): file frames, ascending, with their
    ///     strengths 0…1 (1: the strongest of the file); null when the file cannot be analysed.
    /// </summary>
    IReadOnlyList<(long Frame, double Strength)>? Onsets(AudioFile file);
}

/// <summary>No analysis: edits that need one use what the project already holds (a file's tempo), or refuse.</summary>
public sealed class NoAnalysis : IAnalysisSource {
    public static NoAnalysis Instance { get; } = new();

    public FileAnalysis? Rhythm(AudioFile file) => null;

    public IReadOnlyList<(long Start, long End)>? Silence(
        AudioFile file,
        double thresholdDb,
        double minTimeMs,
        double preAttackMs,
        double postReleaseMs
    ) =>
        null;

    public IReadOnlyList<(long Frame, double Strength)>? Onsets(AudioFile file) => null;
}

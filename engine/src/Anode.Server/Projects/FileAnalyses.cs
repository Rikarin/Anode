using Anode.Contracts;
using Anode.Dsp.Analysis;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Editing;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Anode.Server.Projects;

/// <summary>
///     The analyses the server runs on audio (docs/06-phase-5.md, "Analysis"): the seam between the server and
///     <c>Anode.Dsp.Analysis</c>, so tests can put a deterministic analyzer in its place.
/// </summary>
public interface IAudioAnalyzer {
    long[] Transients(float[] mono, int sampleRate);

    BeatTrack Beats(float[] mono, int sampleRate);

    PitchTrack Pitch(float[] mono, int sampleRate, int hop);

    NoteSegment[] Notes(PitchTrack track, int sampleRate);

    /// <summary>Whether a file sounds polyphonic (Flex Pitch then works less well).</summary>
    bool Polyphonic(float[] mono, int sampleRate, PitchTrack track);

    (long Start, long End)[] Silence(
        float[] mono,
        int sampleRate,
        double thresholdDb,
        double minTimeMs,
        double preAttackMs,
        double postReleaseMs
    );
}

/// <summary>The analyses of <c>Anode.Dsp.Analysis</c> (workstream D).</summary>
public sealed class DspAudioAnalyzer : IAudioAnalyzer {
    public long[] Transients(float[] mono, int sampleRate) => Dsp.Analysis.Transients.Detect(mono, sampleRate);

    public BeatTrack Beats(float[] mono, int sampleRate) => BeatTracker.Track(mono, sampleRate);

    public PitchTrack Pitch(float[] mono, int sampleRate, int hop) => PitchTracker.Track(mono, sampleRate, hop);

    public NoteSegment[] Notes(PitchTrack track, int sampleRate) => NoteSegmenter.Segment(track, sampleRate);

    public bool Polyphonic(float[] mono, int sampleRate, PitchTrack track) =>
        Polyphony.IsPolyphonic(mono, sampleRate, track);

    public (long Start, long End)[] Silence(
        float[] mono,
        int sampleRate,
        double thresholdDb,
        double minTimeMs,
        double preAttackMs,
        double postReleaseMs
    ) =>
        Dsp.Analysis.Silence.Detect(mono, sampleRate, thresholdDb, minTimeMs, preAttackMs, postReleaseMs);
}

/// <summary>An analysis that could not be made (the file unreadable, or the analyzer failing).</summary>
public sealed class AnalysisException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
///     Files' analyses, cached in the project package as <c>Analysis/&lt;fileId&gt;.json</c> (docs/06-phase-5.md,
///     "Server"): rebuilt on demand, not undoable. Rhythm (transients and beats) is analysed synchronously when an edit
///     or request needs it; pitch (curve and notes) by the <c>analyze</c> job.
/// </summary>
public sealed class FileAnalyses(IAudioAnalyzer analyzer, IAudioEditor editor) {
    public const string Folder = "Analysis";

    readonly ConcurrentDictionary<string, Lock> locks = new(StringComparer.Ordinal);

    /// <summary>The pitch hop at <paramref name="sampleRate" />: about 5 ms.</summary>
    public static int Hop(int sampleRate) => Math.Max(1, (int)Math.Round(sampleRate * 0.005));

    public static string PathOf(string projectFolder, string fileId) =>
        Path.Combine(
            projectFolder,
            Folder,
            Model.Storage.ProjectPackage.IsSafeId(fileId)
                ? fileId + ".json"
                : throw new ArgumentException($"Unsafe file id '{fileId}'.")
        );

    /// <summary>The cached analysis of a file, or null.</summary>
    public static FileAnalysis? Cached(string projectFolder, string fileId) {
        var path = PathOf(projectFolder, fileId);
        try {
            if (!File.Exists(path)) {
                return null;
            }

            var a = JsonSerializer.Deserialize(File.ReadAllText(path), ContractsJsonContext.Default.FileAnalysis);
            return a is { FileId: not null, Transients: not null, Pitch: not null, Notes: not null }
                && a.FileId == fileId
                    ? a
                    : null;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    /// <summary>The cached analyses of the files of a project, by file id.</summary>
    public static Dictionary<string, FileAnalysis> CachedAll(string projectFolder, IEnumerable<AudioFile> files) {
        var analyses = new Dictionary<string, FileAnalysis>(StringComparer.Ordinal);
        foreach (var file in files) {
            if (Model.Storage.ProjectPackage.IsSafeId(file.Id) && Cached(projectFolder, file.Id) is { } a) {
                analyses[file.Id] = a;
            }
        }

        return analyses;
    }

    /// <summary>
    ///     The file's analysis with its transients and tempo, analysed (and cached) now when they are missing; whether it
    ///     was built.
    /// </summary>
    public (FileAnalysis Analysis, bool Built) Rhythm(string projectFolder, AudioFile file) {
        lock (LockOf(projectFolder, file.Id)) {
            var cached = Cached(projectFolder, file.Id);
            if (cached is { Tempo: not null }) {
                return (cached, false);
            }

            var (mono, rate) = Mono(projectFolder, file);
            FileAnalysis analysis;
            try {
                var transients = analyzer.Transients(mono, rate);
                var beats = analyzer.Beats(mono, rate);
                analysis = new(
                    file.Id,
                    cached?.Hop ?? Hop(rate),
                    transients,
                    new(beats.Bpm, beats.Variable, beats.Beats, beats.Downbeats),
                    cached?.Pitch ?? [],
                    cached?.Notes ?? [],
                    cached?.Polyphonic ?? false
                );
            } catch (Exception e) when (e is not OperationCanceledException) {
                throw new AnalysisException($"Could not analyse '{file.Name}': {e.Message}", e);
            }

            Write(projectFolder, analysis);
            return (analysis, true);
        }
    }

    /// <summary>The file's whole analysis, with its pitch curve and notes (Flex Pitch), analysed and cached now.</summary>
    public FileAnalysis Pitch(string projectFolder, AudioFile file, CancellationToken cancellationToken = default) {
        var rhythm = Rhythm(projectFolder, file).Analysis;
        lock (LockOf(projectFolder, file.Id)) {
            var (mono, rate) = Mono(projectFolder, file);
            cancellationToken.ThrowIfCancellationRequested();
            FileAnalysis analysis;
            try {
                var hop = Hop(rate);
                var track = analyzer.Pitch(mono, rate, hop);
                var notes = analyzer.Notes(track, rate);
                analysis = rhythm with {
                    Hop = track.Hop,
                    Pitch = [..track.Cents.Select(static c => float.IsFinite(c) ? c : (float?)null)],
                    Notes = [
                        ..notes.Select(static n => new PitchSegment(n.StartFrame, n.EndFrame, n.Cents, n.Confidence))
                    ],
                    Polyphonic = analyzer.Polyphonic(mono, rate, track)
                };
            } catch (Exception e) when (e is not OperationCanceledException) {
                throw new AnalysisException($"Could not analyse the pitch of '{file.Name}': {e.Message}", e);
            }

            Write(projectFolder, analysis);
            return analysis;
        }
    }

    /// <summary>The file's non-silent ranges for Strip Silence's settings (not cached: they depend on them).</summary>
    public (long Start, long End)[] Silence(
        string projectFolder,
        AudioFile file,
        double thresholdDb,
        double minTimeMs,
        double preAttackMs,
        double postReleaseMs
    ) {
        var (mono, rate) = Mono(projectFolder, file);
        try {
            return analyzer.Silence(mono, rate, thresholdDb, minTimeMs, preAttackMs, postReleaseMs);
        } catch (Exception e) when (e is not OperationCanceledException) {
            throw new AnalysisException($"Could not find the silence of '{file.Name}': {e.Message}", e);
        }
    }

    /// <summary>
    ///     The file's onsets for a groove template (docs/08-phase-7.md; not cached: a groove is made once), through the
    ///     Audio File Editor's algorithms.
    /// </summary>
    public IReadOnlyList<(long Frame, double Strength)> Onsets(string projectFolder, AudioFile file) {
        var (mono, rate) = Mono(projectFolder, file);
        try {
            return [..editor.Onsets(mono, rate).Select(static o => (o.Frame, o.Strength))];
        } catch (Exception e) when (e is not OperationCanceledException) {
            throw new AnalysisException($"Could not find the onsets of '{file.Name}': {e.Message}", e);
        }
    }

    /// <summary>Drops the cached analysis of a file (its audio changed: another version).</summary>
    public static void Forget(string projectFolder, string fileId) {
        try {
            File.Delete(PathOf(projectFolder, fileId));
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
            // A cache that cannot be removed is overwritten when the file is analysed again.
            _ = e;
        }
    }

    /// <summary>A file of the project as one channel (the mean of its channels), and its rate.</summary>
    public static (float[] Mono, int Rate) Mono(string projectFolder, AudioFile file) {
        try {
            var (channels, rate) = BounceProcessor.Read(Path.Combine(projectFolder, file.Path));
            return (Downmix.ToMono(channels), rate);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or WavFormatException) {
            throw new AnalysisException($"Could not read '{file.Name}': {e.Message}", e);
        }
    }

    static void Write(string projectFolder, FileAnalysis analysis) {
        var path = PathOf(projectFolder, analysis.FileId);
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Model.Storage.AtomicRename.Write(
                path,
                JsonSerializer.Serialize(analysis, ContractsJsonContext.Default.FileAnalysis)
            );
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // A cache that cannot be written is rebuilt next time.
            _ = e;
        }
    }

    Lock LockOf(string projectFolder, string fileId) =>
        locks.GetOrAdd(PathOf(projectFolder, fileId), static _ => new());
}

/// <summary>
///     The analysis edits of the open project ask for (<see cref="IAnalysisSource" />): through
///     <see cref="FileAnalyses" />, remembering what it built so the session can publish it.
/// </summary>
sealed class SessionAnalysis(FileAnalyses analyses, string folder) : IAnalysisSource {
    public List<FileAnalysis> Built { get; } = [];

    public FileAnalysis? Rhythm(AudioFile file) {
        try {
            var (analysis, built) = analyses.Rhythm(folder, file);
            if (built) {
                Built.Add(analysis);
            }

            return analysis;
        } catch (AnalysisException e) {
            throw new EditException(e.Message, e);
        }
    }

    public IReadOnlyList<(long Start, long End)>? Silence(
        AudioFile file,
        double thresholdDb,
        double minTimeMs,
        double preAttackMs,
        double postReleaseMs
    ) {
        try {
            return analyses.Silence(folder, file, thresholdDb, minTimeMs, preAttackMs, postReleaseMs);
        } catch (AnalysisException e) {
            throw new EditException(e.Message, e);
        }
    }

    public IReadOnlyList<(long Frame, double Strength)>? Onsets(AudioFile file) {
        try {
            return analyses.Onsets(folder, file);
        } catch (AnalysisException e) {
            throw new EditException(e.Message, e);
        }
    }
}

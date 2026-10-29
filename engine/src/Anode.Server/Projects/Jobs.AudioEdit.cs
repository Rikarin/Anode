using Anode.Contracts;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Editing;

namespace Anode.Server.Projects;

/// <summary>
///     The Audio File Editor's operations (docs/08-phase-7.md): each an <c>audioEdit</c> job that reads the file's
///     current version, applies the operation (<see cref="IAudioEditor" />), writes <c>&lt;name&gt;.vN.wav</c> beside
///     it at the file's bit depth, and points the file at it, with the region shifts a length change needs, as one
///     undo step (<see cref="ProjectSession.ApplyFileVersion" />). Loop search runs on the file's current version.
/// </summary>
public sealed partial class Jobs {
    public const int MaxLoops = 20;
    public const int DefaultLoops = 5;

    /// <summary>Queues an operation on a file: 404 for an unknown file, 422 for one that cannot be carried out.</summary>
    public Job EditFile(string fileId, AudioEditOperation? operation) {
        var view = session.View ?? throw ProjectException.NoProject();
        var file = view.State.Files.TryGetValue(fileId, out var f)
            ? f
            : throw ProjectException.NotFound($"No audio file '{fileId}'.");
        var op = Check(operation, file);
        try {
            // Refused now rather than when the job finishes: a region would vanish, or keep the wrong rate.
            _ = Edits.FileLengthChanged(view.State, file.Id, op.Kind, op.StartFrame, op.EndFrame, op.TargetRate ?? 0);
        } catch (EditException e) {
            throw ProjectException.Unprocessable(e.Message);
        }

        return Enqueue(
            JobKind.AudioEdit,
            (entry, token) => Task.Run(() => EditFileAsync(entry, view.Session, view.Folder, file, op, token), token)
        );
    }

    /// <summary>An operation checked against the file: a range inside it, values in range.</summary>
    static AudioEditOperation Check(AudioEditOperation? operation, AudioFile file) {
        if (operation is null || !Enum.IsDefined(operation.Kind)) {
            throw ProjectException.Unprocessable("Name an operation.");
        }

        if (operation.Kind == AudioEditKind.ConvertRate) {
            return operation.TargetRate is { } rate && ProjectDefaults.SampleRates.Contains(rate)
                ? rate == file.SampleRate
                    ? throw ProjectException.Unprocessable($"'{file.Name}' is at {rate} Hz already.")
                    : operation with { StartFrame = 0, EndFrame = file.Frames }
                : throw ProjectException.Unprocessable(
                    $"Convert to one of {string.Join(", ", ProjectDefaults.SampleRates)} Hz."
                );
        }

        if (operation.StartFrame < 0 || operation.EndFrame > file.Frames || operation.EndFrame <= operation.StartFrame) {
            throw ProjectException.Unprocessable($"A selection is inside the file's 0…{file.Frames} frames, not empty.");
        }

        if (operation.Value is { } value && !(double.IsFinite(value) && Math.Abs(value) <= 96)) {
            throw ProjectException.Unprocessable($"A level is −96…96 dB, not {value}.");
        }

        if (operation.Curve is { } curve && !(curve is >= -1 and <= 1)) {
            throw ProjectException.Unprocessable($"A fade's curve is −1…1, not {curve}.");
        }

        return operation;
    }

    IReadOnlyList<string> EditFileAsync(
        Entry entry,
        long sessionId,
        string folder,
        AudioFile file,
        AudioEditOperation operation,
        CancellationToken token
    ) {
        var source = Path.Combine(folder, file.Path);
        var (channels, rate) = BounceProcessor.Read(source);
        Progress(entry, 0.3);
        token.ThrowIfCancellationRequested();
        (float[][] Channels, int SampleRate) edited;
        try {
            edited = editor.Apply(channels, rate, operation);
        } catch (ArgumentException e) {
            throw ProjectException.Unprocessable($"Could not {operation.Kind} '{file.Name}': {e.Message}");
        }

        if (edited.Channels is not { Length: > 0 } || edited.Channels[0].Length == 0) {
            throw ProjectException.Unprocessable($"{operation.Kind} would leave '{file.Name}' empty.");
        }

        Progress(entry, 0.7);
        token.ThrowIfCancellationRequested();
        var version = ProjectSession.NextVersion(folder, file);
        var (directory, stem) = ProjectSession.VersionStem(folder, file);
        var path = Path.Combine(directory, $"{stem}.v{version}.wav");
        var bits = file.BitDepth is 16 or 24 ? file.BitDepth : 32;
        BounceProcessor.Write(
            path,
            edited.Channels,
            new(AudioFormat.Wav, bits, edited.SampleRate, DitherMode.None, NormalizeMode.Off, -16, -1)
        );
        try {
            session.ApplyFileVersion(
                sessionId,
                Label(operation.Kind),
                file,
                operation,
                version,
                path,
                WavReader.Probe(path)
            );
        } catch {
            TryDelete(path);
            throw;
        }

        return [path];
    }

    void TryDelete(string path) {
        try {
            File.Delete(path);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            ProjectLog.JobFailed(logger, path, $"could not remove the unused version: {e.Message}");
        }
    }

    /// <summary>An operation's name in the undo history, as Logic's File Editor menu has it.</summary>
    static string Label(AudioEditKind kind) =>
        kind switch {
            AudioEditKind.Normalize => "Normalize",
            AudioEditKind.Gain => "Change Gain",
            AudioEditKind.FadeIn => "Fade In",
            AudioEditKind.FadeOut => "Fade Out",
            AudioEditKind.Reverse => "Reverse",
            AudioEditKind.Invert => "Invert",
            AudioEditKind.Silence => "Silence",
            AudioEditKind.RemoveDc => "Remove DC Offset",
            AudioEditKind.Trim => "Trim",
            _ => "Convert Sample Rate"
        };

    /// <summary>Loop search over a range of a file's current version: the best loops, best first.</summary>
    public IReadOnlyList<LoopSearchResult> SearchLoops(string fileId, LoopSearchRequest? request) {
        var (_, folder, file) = session.FileOf(fileId);
        if (request is null
            || request.StartFrame < 0
            || request.EndFrame > file.Frames
            || request.EndFrame <= request.StartFrame) {
            throw ProjectException.Unprocessable($"Search a range inside the file's 0…{file.Frames} frames.");
        }

        var count = request.Count ?? DefaultLoops;
        if (count is < 1 or > MaxLoops) {
            throw ProjectException.Unprocessable($"Ask for 1…{MaxLoops} loops, not {count}.");
        }

        var minFrames = request.MinFrames ?? Math.Max(1, file.SampleRate / 100);
        if (minFrames < 1 || minFrames > request.EndFrame - request.StartFrame) {
            throw ProjectException.Unprocessable("A loop is at least one frame long, and fits the range.");
        }

        float[] mono;
        int rate;
        try {
            (mono, rate) = FileAnalyses.Mono(folder, file);
        } catch (AnalysisException e) {
            throw ProjectException.Unprocessable(e.Message);
        }

        return [
            ..editor.Loops(mono, rate, request.StartFrame, request.EndFrame, count, minFrames)
                .Select(static l => new LoopSearchResult(l.StartFrame, l.EndFrame, l.Score))
        ];
    }
}

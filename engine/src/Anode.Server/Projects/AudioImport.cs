using Anode.Contracts;
using Anode.Model.Audio;
using Anode.Model.Storage;

namespace Anode.Server.Projects;

/// <summary>
///     Brings WAV files into the open project: copied into its <c>Audio Files</c> folder under a name of their own
///     (a file already there is used in place), checked first: one or two channels, 16/24/32-bit PCM or 32-bit float,
///     at the project's sample rate (Phase 1 does not convert rates). Anything else is a 422.
/// </summary>
public sealed class AudioImport(ProjectSession session) {
    /// <summary>Imports a file from the server's own disk.</summary>
    public AudioFile Import(string path) {
        var (sessionId, absolute, info) = Bring(path);
        return session.AddFile(sessionId, absolute, info);
    }

    /// <summary>
    ///     Copies a file from the server's own disk into the open project's <c>Audio Files</c> (checked first), without
    ///     adding it to the pool: which opening of the project it is for, where it now is, and its format.
    /// </summary>
    public (long Session, string Path, WavInfo Info) Bring(string path) => Bring(path, 2);

    /// <summary>
    ///     As <see cref="Bring(string)" />, taking files of up to <paramref name="maxChannels" /> channels (Phase 10: a
    ///     surround bed or a movie's audio, up to 16).
    /// </summary>
    public (long Session, string Path, WavInfo Info) Bring(string path, int maxChannels) {
        var (sessionId, folder, rate) = session.Target();
        var source = Source(path);
        var info = Check(source, rate, maxChannels);
        return (sessionId, Copied(source, folder), info);
    }

    /// <summary>
    ///     Relinks files of the project, one undo step: each candidate must hold what its file held
    ///     (<see cref="OfflineFiles.Mismatch" />, whatever it is called), and is then copied into <c>Audio Files</c>, which
    ///     is where a project's files live. Nothing is copied or changed unless every candidate passes.
    /// </summary>
    public CommandResult Relink(RelinkRequest request) {
        if (request.Files is not { Count: > 0 } targets) {
            throw ProjectException.Unprocessable("Name the files to relink.");
        }

        var checkedTargets = new List<(long Session, string Folder, AudioFile File, string Source, WavInfo Info)>();
        foreach (var target in targets.DistinctBy(static t => t.FileId)) {
            var (sessionId, folder, file) = session.RelinkTarget(target.FileId);
            var source = Source(target.Path);
            var info = Probe(source);
            if (OfflineFiles.Mismatch(file, info) is { } problem) {
                throw ProjectException.Unprocessable($"'{Path.GetFileName(source)}' is not '{file.Name}': {problem}");
            }

            checkedTargets.Add((sessionId, folder, file, source, info));
        }

        return session.Relink(
            checkedTargets[0].Session,
            [..checkedTargets.Select(static t => (t.File, Copied(t.Source, t.Folder), t.Info))]
        );
    }

    /// <summary>Where a file named by a client is; 422 when nothing is there.</summary>
    static string Source(string? path) {
        if (string.IsNullOrWhiteSpace(path)) {
            throw ProjectException.Unprocessable("Name the file.");
        }

        var source = Path.GetFullPath(path);
        return File.Exists(source) ? source : throw ProjectException.Unprocessable($"'{source}' does not exist.");
    }

    /// <summary>A file's place in the project's <c>Audio Files</c>: where it is, when it is there, else a copy of it.</summary>
    static string Copied(string source, string folder) {
        var audio = Path.Combine(folder, ProjectPackage.AudioFolder);
        Directory.CreateDirectory(audio);
        if (string.Equals(Path.GetDirectoryName(source), Path.GetFullPath(audio), StringComparison.Ordinal)) {
            return source;
        }

        var temporary = Path.Combine(audio, $".import-{Guid.NewGuid():n}.tmp");
        try {
            File.Copy(source, temporary);
            return Claim(temporary, audio, Path.GetFileNameWithoutExtension(source));
        } finally {
            File.Delete(temporary);
        }
    }

    /// <summary>Imports an uploaded file (a browser on the network).</summary>
    public async Task<AudioFile> UploadAsync(IFormFile upload, CancellationToken cancellationToken) {
        var (sessionId, folder, rate) = session.Target();
        var audio = Path.Combine(folder, ProjectPackage.AudioFolder);
        Directory.CreateDirectory(audio);
        var temporary = Path.Combine(audio, $".upload-{Guid.NewGuid():n}.tmp");
        try {
            var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (target.ConfigureAwait(false)) {
                await upload.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            var info = Check(temporary, rate, 2);
            var name = Path.GetFileNameWithoutExtension(Path.GetFileName(upload.FileName ?? string.Empty));
            return session.AddFile(sessionId, Claim(temporary, audio, name), info);
        } finally {
            File.Delete(temporary);
        }
    }

    static WavInfo Probe(string path) {
        try {
            return WavReader.Probe(path);
        } catch (WavFormatException e) {
            throw ProjectException.Unprocessable(e.Message);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw ProjectException.Unprocessable($"Could not read '{path}': {e.Message}");
        }
    }

    static WavInfo Check(string path, int projectRate, int maxChannels) {
        var info = Probe(path);
        if (info.Channels < 1 || info.Channels > maxChannels) {
            throw ProjectException.Unprocessable(
                maxChannels == 2
                    ? $"The file has {info.Channels} channels; Anode takes mono or stereo."
                    : $"The file has {info.Channels} channels; Anode takes 1…{maxChannels}."
            );
        }

        if (info.SampleRate != projectRate) {
            throw ProjectException.Unprocessable(
                $"The file is at {info.SampleRate} Hz and the project at {projectRate} Hz; convert it first."
            );
        }

        if (info.Frames == 0) {
            throw ProjectException.Unprocessable("The file holds no audio.");
        }

        return info;
    }

    /// <summary>Moves <paramref name="temporary" /> to a free <c>&lt;name&gt;[ n].wav</c> in <paramref name="folder" />.</summary>
    static string Claim(string temporary, string folder, string name) {
        var safe = new string([..name.Select(static c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]).Trim();
        if (safe.Length == 0 || safe.StartsWith('.')) {
            safe = "Audio" + safe;
        }

        for (var n = 1;; n++) {
            var candidate = Path.Combine(folder, n == 1 ? $"{safe}.wav" : $"{safe} {n}.wav");
            if (File.Exists(candidate)) {
                continue;
            }

            try {
                File.Move(temporary, candidate, false);
                return candidate;
            } catch (IOException) when (File.Exists(candidate)) {
                // taken in the meantime
            }
        }
    }
}

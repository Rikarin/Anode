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
        var (sessionId, folder, rate) = session.Target();
        if (string.IsNullOrWhiteSpace(path)) {
            throw ProjectException.Unprocessable("Name the file to import.");
        }

        var source = Path.GetFullPath(path);
        if (!File.Exists(source)) {
            throw ProjectException.Unprocessable($"'{source}' does not exist.");
        }

        var info = Check(source, rate);
        var audio = Path.Combine(folder, ProjectPackage.AudioFolder);
        Directory.CreateDirectory(audio);
        if (string.Equals(Path.GetDirectoryName(source), Path.GetFullPath(audio), StringComparison.Ordinal)) {
            return session.AddFile(sessionId, source, info);
        }

        var temporary = Path.Combine(audio, $".import-{Guid.NewGuid():n}.tmp");
        try {
            File.Copy(source, temporary);
            return session.AddFile(sessionId, Claim(temporary, audio, Path.GetFileNameWithoutExtension(source)), info);
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

            var info = Check(temporary, rate);
            var name = Path.GetFileNameWithoutExtension(Path.GetFileName(upload.FileName ?? string.Empty));
            return session.AddFile(sessionId, Claim(temporary, audio, name), info);
        } finally {
            File.Delete(temporary);
        }
    }

    static WavInfo Check(string path, int projectRate) {
        WavInfo info;
        try {
            info = WavReader.Probe(path);
        } catch (WavFormatException e) {
            throw ProjectException.Unprocessable(e.Message);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw ProjectException.Unprocessable($"Could not read '{path}': {e.Message}");
        }

        if (info.Channels is not (1 or 2)) {
            throw ProjectException.Unprocessable($"The file has {info.Channels} channels; Anode takes mono or stereo.");
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

using Anode.Contracts;
using Anode.Model.Audio;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Anode.Model.Storage;

/// <summary>
///     Which files of a project the disk cannot give, and what may take their place. Offline is about the disk, not
///     the document, so nothing here is stored: a file comes back by itself when it is put back. A candidate is judged by
///     what it holds (length, rate, channels), never by its name.
/// </summary>
public static partial class OfflineFiles {
    /// <summary>The most channels a file of a project has (a surround bed, a movie's audio).</summary>
    public const int MaxChannels = 16;

    /// <summary>The most folders and files a search looks at.</summary>
    public const int MaxSearchEntries = 100_000;

    const string WavExtension = ".wav";

    /// <summary>
    ///     The project's audio files: those it lists (the first of each id), and an entry for each file only something
    ///     else names, so that a save keeps what names it. Such an entry has no format (channels 0), the project's rate,
    ///     and as many frames as its users reach; its path is where nothing is.
    /// </summary>
    internal static ImmutableSortedDictionary<string, AudioFile> WithUnlisted(
        ProjectFile root,
        List<TrackFile> trackFiles
    ) {
        var files = (root.AudioFiles ?? []).Where(static f => f?.Id is not null)
            .DistinctBy(static f => f.Id)
            .ToImmutableSortedDictionary(static f => f.Id, static f => f, StringComparer.Ordinal)
            .ToBuilder();
        var reach = new Dictionary<string, (string Name, long End)>(StringComparer.Ordinal);
        foreach (var (id, name, end) in FileUses(root, trackFiles)) {
            if (id is { Length: > 0 } && !files.ContainsKey(id)) {
                reach[id] = reach.TryGetValue(id, out var known)
                    ? (known.Name, Math.Max(known.End, end))
                    : (name ?? id, end);
            }
        }

        foreach (var (id, (name, end)) in reach) {
            var safe = new string([..id.Select(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_')]);
            files[id] = new(
                id,
                name,
                $"{ProjectPackage.AudioFolder}/{safe}.wav",
                root.SampleRate,
                0,
                Math.Max(end, 1),
                0,
                null,
                0
            );
        }

        return files.ToImmutable();
    }

    /// <summary>Every audio file the track files and the Live Loops grid name: its id, a name for it, how far it is used.</summary>
    static IEnumerable<(string? Id, string? Name, long End)> FileUses(ProjectFile root, List<TrackFile> trackFiles) {
        foreach (var file in trackFiles.Where(static f => f.Track.Kind == TrackKind.Audio)) {
            foreach (var use in RegionUses(file.Regions).Concat(TakeUses(file.TakeFolders))) {
                yield return use;
            }

            yield return (file.Track.FrozenFileId, file.Track.Name, 0);
            foreach (var alternative in (file.Track.Alternatives ?? []).Where(static a => a is not null)) {
                foreach (var use in RegionUses(alternative.Regions).Concat(TakeUses(alternative.TakeFolders))) {
                    yield return use;
                }
            }
        }

        var cells = (root.LiveLoops?.Cells ?? []).Select(static c => c?.Content?.Audio).OfType<AudioRegion>();
        foreach (var use in RegionUses([..cells])) {
            yield return use;
        }
    }

    static IEnumerable<(string? Id, string? Name, long End)> RegionUses(IReadOnlyList<AudioRegion>? regions) =>
        (regions ?? []).Where(static r => r is not null)
        .Select(static r => ((string?)r.FileId, (string?)r.Name, Math.Max(r.FileOffset, 0) + Math.Max(r.Length, 0)));

    static IEnumerable<(string? Id, string? Name, long End)> TakeUses(IReadOnlyList<TakeFolder>? folders) =>
        (folders ?? []).SelectMany(static f => f?.Takes ?? [])
        .Where(static t => t is not null)
        .Select(static t => ((string?)t.FileId, (string?)t.Name, Math.Max(t.FileOffset, 0) + Math.Max(t.Length, 0)));

    /// <summary>Whether the project lost this file's entry: regions name it, its format is not known.</summary>
    public static bool IsUnlisted(AudioFile file) => file.Channels == 0;

    /// <summary>The project's offline files and unreadable tracks, read from the disk now.</summary>
    public static OfflineReport Report(string folder, ProjectState state) =>
        new(
            [..state.Files.Values.Select(f => Check(folder, f)).OfType<OfflineFile>()],
            [..state.UnreadableTracks]
        );

    /// <summary>Whether two reports say the same.</summary>
    public static bool Same(OfflineReport a, OfflineReport b) =>
        a.Files.SequenceEqual(b.Files) && a.UnreadableTracks.SequenceEqual(b.UnreadableTracks);

    /// <summary>Why <paramref name="file" /> is offline, or null when the disk holds it.</summary>
    public static OfflineFile? Check(string folder, AudioFile file) {
        if (IsUnlisted(file)) {
            return Offline(file, OfflineReason.Unlisted, null);
        }

        var path = Path.Combine(folder, file.Path);
        if (!File.Exists(path)) {
            return Offline(file, OfflineReason.Missing, null);
        }

        try {
            return Mismatch(file, WavReader.Probe(path)) is { } problem
                ? Offline(file, OfflineReason.Changed, problem)
                : null;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or WavFormatException) {
            return Offline(file, OfflineReason.Changed, e.Message);
        }
    }

    /// <summary>
    ///     What keeps <paramref name="candidate" /> from being <paramref name="file" />, or null when it can be: the same
    ///     rate, channels and length. A file whose entry was lost is only known by its rate and by how far its regions
    ///     reach, so it may be any channels and longer.
    /// </summary>
    public static string? Mismatch(AudioFile file, WavInfo candidate) {
        if (candidate.SampleRate != file.SampleRate) {
            return $"It is at {candidate.SampleRate} Hz, not {file.SampleRate} Hz.";
        }

        if (IsUnlisted(file)) {
            if (candidate.Channels is < 1 or > MaxChannels) {
                return $"It has {candidate.Channels} channels; Anode takes 1…{MaxChannels}.";
            }

            return candidate.Frames < file.Frames
                ? $"It has {candidate.Frames} frames; the regions reach frame {file.Frames}."
                : null;
        }

        if (candidate.Channels != file.Channels) {
            return $"It has {candidate.Channels} channels, not {file.Channels}.";
        }

        return candidate.Frames != file.Frames ? $"It has {candidate.Frames} frames, not {file.Frames}." : null;
    }

    /// <summary>
    ///     The WAVs in <paramref name="directory" /> and the folders inside it (hidden ones and links not followed) named
    ///     as one of <paramref name="wanted" />, each checked against it; those that match first. It looks at
    ///     <see cref="MaxSearchEntries" /> entries at most.
    /// </summary>
    public static OfflineSearch Search(string directory, IReadOnlyCollection<AudioFile> wanted) {
        var byName = wanted.SelectMany(static f => Names(f).Select(n => (Name: n, File: f)))
            .ToLookup(static p => p.Name, static p => p.File, StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        };
        var found = new List<OfflineCandidate>();
        var folders = new Stack<string>([directory]);
        var seen = 0;
        while (folders.TryPop(out var folder) && seen < MaxSearchEntries) {
            try {
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", options)) {
                    if (++seen > MaxSearchEntries) {
                        break;
                    }

                    if (Directory.Exists(entry)) {
                        folders.Push(entry);
                    } else if (entry.EndsWith(WavExtension, StringComparison.OrdinalIgnoreCase)) {
                        found.AddRange(Candidates(entry, byName[Path.GetFileNameWithoutExtension(entry)]));
                    }
                }
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                // A folder that went or cannot be read: the others are still searched.
            }
        }

        return new(
            directory,
            [
                ..found.OrderBy(static c => c.Problem is not null)
                    .ThenBy(static c => c.FileId, StringComparer.Ordinal)
                    .ThenBy(static c => c.Path, StringComparer.Ordinal)
            ],
            seen >= MaxSearchEntries
        );
    }

    static OfflineCandidate[] Candidates(string path, IEnumerable<AudioFile> files) {
        var named = files.Distinct().ToArray();
        if (named.Length == 0) {
            return [];
        }

        try {
            var info = WavReader.Probe(path);
            return [..named.Select(f => new OfflineCandidate(f.Id, path, Mismatch(f, info)))];
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or WavFormatException) {
            return [..named.Select(f => new OfflineCandidate(f.Id, path, e.Message))];
        }
    }

    /// <summary>The names a file may be found under: of its path (an edited version's too, without <c>.vN</c>) and its own.</summary>
    static HashSet<string> Names(AudioFile file) {
        var stem = Path.GetFileNameWithoutExtension(file.Path);
        return new(StringComparer.OrdinalIgnoreCase) {
            stem,
            VersionSuffix().Replace(stem, string.Empty),
            file.Name.EndsWith(WavExtension, StringComparison.OrdinalIgnoreCase)
                ? file.Name[..^WavExtension.Length]
                : file.Name
        };
    }

    static OfflineFile Offline(AudioFile file, OfflineReason reason, string? detail) =>
        new(file.Id, file.Name, file.Path, reason, detail);

    [GeneratedRegex(@"\.v[0-9]+$")]
    private static partial Regex VersionSuffix();
}

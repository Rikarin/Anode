using Anode.Contracts;
using Anode.Model;
using Anode.Model.Audio;
using Anode.Model.Editing;
using Anode.Model.Storage;

namespace Anode.Server.Projects;

/// <summary>
///     Import from Project (docs/04-phase-3.md, "Import tracks from another project"): chosen tracks of a package on the
///     server's disk join the open project as one undo step. Its media is copied in first, under new ids and names of its
///     own, converted when the source runs at another rate; only then does the project change, and a failure takes the
///     copies back out. The source is read, never written. A file offline in the source comes along offline, to be found
///     from the Missing Files window, unless it would need converting.
/// </summary>
public sealed partial class Jobs {
    /// <summary>What a package on the server's disk holds, for the Import from Project browser. It opens nothing.</summary>
    public ProjectContents Contents(string? path) {
        var (folder, state, _) = ReadProject(path);
        return new(
            state.Name,
            folder,
            state.SampleRate,
            state.Settings.Tempo,
            [
                ..state.Tracks.Select(t => new ProjectTrackInfo(
                        t.Id,
                        t.Name,
                        t.Kind,
                        t.Color,
                        t.ParentId,
                        state.RegionsOf(t.Id).Count()
                        + state.MidiRegionsOf(t.Id).Count()
                        + state.TakeFoldersOf(t.Id).Count()
                        + state.PatternRegionsOf(t.Id).Count()
                        + state.SessionPlayerRegionsOf(t.Id).Count(),
                        t.Strip.Inserts.Count,
                        t.Strip.Sends.Count,
                        state.LanesOf(t.Id).Count()
                    )
                )
            ]
        );
    }

    /// <summary>Queues importing tracks of a package: 404 for no such package or track, 422 when it cannot be read.</summary>
    public Job ImportProjectTracks(ImportProjectTracksRequest? request) {
        if (request?.TrackIds is not { Count: > 0 } chosen) {
            throw ProjectException.Unprocessable("Choose the tracks to import.");
        }

        return ImportFrom(request.Path, chosen, request, "Import Tracks");
    }

    /// <summary>Queues adding a track template's tracks: 404 for no such template.</summary>
    public Job ApplyTrackTemplate(string id) {
        var folder = templates.TrackFolder(id) ?? throw ProjectException.NotFound($"No track template '{id}'.");
        var (_, source, _) = ReadProject(folder);
        return ImportFrom(
            folder,
            [..source.Tracks.Select(static t => t.Id)],
            new(folder, [], false, true, true, false),
            "Add Track Template"
        );
    }

    Job ImportFrom(string? path, IReadOnlyList<string> chosen, ImportProjectTracksRequest request, string label) {
        var (folder, source, states) = ReadProject(path);
        var known = chosen.Where(id => source.FindTrack(id ?? string.Empty) is null).ToArray();
        if (known.Length > 0) {
            throw ProjectException.NotFound($"'{source.Name}' has no track '{known[0]}'.");
        }

        var parts = new TrackParts(
            request.Regions ?? true,
            request.Inserts ?? true,
            request.Sends ?? true,
            request.Automation ?? true
        );
        var target = session.Target();
        var bundle = TrackBundle.Of(source, chosen, parts);
        if (source.SampleRate != target.SampleRate
            && bundle.Files.Where(f => OfflineFiles.Check(folder, f) is not null).ToArray() is { Length: > 0 } offline) {
            throw ProjectException.Unprocessable(
                $"'{offline[0].Name}' is offline in '{source.Name}', which is at {source.SampleRate} Hz: find it first, "
                + $"so it can be converted to {target.SampleRate} Hz."
            );
        }

        return Enqueue(
            JobKind.ProjectImport,
            (entry, token) => Task.Run(
                IReadOnlyList<string>() => {
                    var written = new List<string>();
                    try {
                        ImportTracks(entry, folder, source, chosen, parts, states, target, written, label, token);
                        return written;
                    } catch {
                        written.ForEach(TryDelete);
                        throw;
                    }
                },
                token
            )
        );
    }

    void ImportTracks(
        Entry entry,
        string folder,
        ProjectState source,
        IReadOnlyList<string> chosen,
        TrackParts parts,
        Dictionary<string, byte[]> states,
        (long Session, string Folder, int SampleRate) target,
        List<string> written,
        string label,
        CancellationToken token
    ) {
        var bundle = TrackBundle.Of(source, chosen, parts);
        var files = new Dictionary<string, AudioFile>(StringComparer.Ordinal);
        var audio = Path.Combine(target.Folder, ProjectPackage.AudioFolder);
        Directory.CreateDirectory(audio);
        foreach (var (file, index) in bundle.Files.Select(static (f, i) => (f, i))) {
            token.ThrowIfCancellationRequested();
            files[file.Id] = Imported(folder, file, audio, target, written);
            Progress(entry, 0.8 * (index + 1) / bundle.Files.Count);
        }

        var located = ProjectMedia.Copy(folder, target.Folder, bundle.Tracks, written);
        var rated = ProjectRate.Changed(source, target.SampleRate, SampleRateChangeMode.Convert, files);
        token.ThrowIfCancellationRequested();
        var inserted = TrackBundle.Of(rated, chosen, parts) with {
            Tracks = [..located.Apply(TrackBundle.Of(rated, chosen, parts).Tracks)]
        };
        session.ImportTracks(target.Session, inserted, states, label);
    }

    /// <summary>A file of the source as a new file of the project: copied, converted, or (offline) only its entry.</summary>
    AudioFile Imported(
        string folder,
        AudioFile file,
        string audio,
        (long Session, string Folder, int SampleRate) target,
        List<string> written
    ) {
        var id = RandomIds.Instance.Next(IdPrefix.File);
        var source = Path.Combine(folder, file.Path);
        var path = FreeName(audio, Path.GetFileNameWithoutExtension(source));
        var relative = Path.GetRelativePath(target.Folder, path).Replace(Path.DirectorySeparatorChar, '/');
        if (OfflineFiles.Check(folder, file) is not null) {
            return file with { Id = id, Path = relative };
        }

        written.Add(path);
        if (file.SampleRate == target.SampleRate) {
            ProjectCopy.CopyFile(source, path, CancellationToken.None);
            return file with { Id = id, Path = relative, Version = 0 };
        }

        Convert(file, source, path, target.SampleRate);
        var plan = new RatePlan(target.Session, target.Folder, null!, target.SampleRate, SampleRateChangeMode.Convert, []);
        return ProjectRate.Written(file, id, relative, Checked(file, path, plan), SampleRateChangeMode.Convert);
    }

    static string FreeName(string folder, string stem) {
        var path = Path.Combine(folder, stem + ".wav");
        for (var n = 2; File.Exists(path); n++) {
            path = Path.Combine(folder, $"{stem} {n}.wav");
        }

        return path;
    }

    /// <summary>Which package to read: its folder, its document, and its plugins' states. The package is left as it is.</summary>
    static (string Folder, ProjectState State, Dictionary<string, byte[]> States) ReadProject(string? path) {
        if (string.IsNullOrWhiteSpace(path)) {
            throw ProjectException.Unprocessable("Name the project to read.");
        }

        string folder;
        try {
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) {
            throw ProjectException.Unprocessable($"'{path}' is not a folder path: {e.Message}");
        }

        var document = ProjectHistory.Document(folder);
        if (!ProjectPackage.Exists(document)) {
            throw ProjectException.NotFound($"'{folder}' is not an Anode project.");
        }

        try {
            var state = ProjectPackage.Load(document);
            return (folder, state, ProjectPackage.LoadStates(document, state));
        } catch (PackageException e) {
            throw ProjectException.Unprocessable(e.Message);
        }
    }
}

/// <summary>Where the files instruments and inserts name went: their paths inside the project they joined.</summary>
sealed record ProjectMedia(Dictionary<string, string> Paths) {
    /// <summary>
    ///     Copies the files <paramref name="tracks" />' instruments and inserts name inside the package at
    ///     <paramref name="folder" /> (a sampler's instrument, an impulse response) to the same place in
    ///     <paramref name="target" />; a file of another size already there means a new name.
    /// </summary>
    public static ProjectMedia Copy(string folder, string target, IEnumerable<Track> tracks, List<string> written) {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in Named(tracks)) {
            var from = Path.GetFullPath(Path.Combine(folder, relative));
            if (Path.IsPathRooted(relative) || !File.Exists(from)) {
                continue;
            }

            var to = Path.Combine(target, relative);
            for (var n = 2; File.Exists(to) && new FileInfo(to).Length != new FileInfo(from).Length; n++) {
                var name = $"{Path.GetFileNameWithoutExtension(relative)} {n}{Path.GetExtension(relative)}";
                to = Path.Combine(target, Path.GetDirectoryName(relative) ?? string.Empty, name);
            }

            if (!File.Exists(to)) {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                written.Add(to);
                ProjectCopy.CopyFile(from, to, CancellationToken.None);
            }

            paths[relative] = Path.GetRelativePath(target, to).Replace(Path.DirectorySeparatorChar, '/');
        }

        return new(paths);
    }

    /// <summary>Every path the tracks' instruments and inserts name.</summary>
    static IEnumerable<string> Named(IEnumerable<Track> tracks) =>
        tracks.SelectMany(static t => t.Strip.Inserts.Select(static i => i.File).Prepend(t.Instrument?.File))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);

    public IEnumerable<Track> Apply(IEnumerable<Track> tracks) =>
        tracks.Select(t => t with {
                Instrument = t.Instrument is { File: { } file } slot
                    ? slot with { File = Paths.GetValueOrDefault(file, file) }
                    : t.Instrument,
                Strip = t.Strip with {
                    Inserts = [
                        ..t.Strip.Inserts.Select(i => i.File is { } file ? i with { File = Paths.GetValueOrDefault(file, file) } : i)
                    ]
                }
            }
        );
}

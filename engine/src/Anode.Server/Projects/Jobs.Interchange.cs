using Anode.Contracts;
using Anode.Interchange.MusicXml;
using Anode.Interchange.Smf;
using Anode.Model.Audio;
using Anode.Model.Editing;
using Anode.Model.Midi;

namespace Anode.Server.Projects;

/// <summary>
///     Interchange (docs/10-phase-9.md): MusicXML, AAF and Standard MIDI File export and import as jobs. An export is
///     checked when it is asked for, then writes its file (and an AAF's linked media) into <c>directory</c>, default
///     <c>&lt;project&gt;/Exports</c>, from the project as it was then; the job's files are what it wrote. An import reads
///     a file on the server's disk (or an upload saved in the project for the job), copies its media into the project's
///     <c>Audio Files</c>, and applies <c>importTracks</c> as one undo step; the job's files are the media it copied.
///     MusicXML and AAF go through <see cref="IInterchangeFormats" /> (workstreams D and E), the score view through
///     <see cref="INotation" /> (A), Standard MIDI Files through <c>Anode.Interchange.Smf</c>.
/// </summary>
public sealed partial class Jobs {
    /// <summary>Where exports go when the request names no folder, in the project folder.</summary>
    public const string ExportsFolder = "Exports";

    /// <summary>The largest MusicXML file an import reads.</summary>
    public const long MaxImportBytes = 256L << 20;

    /// <summary>Queues a MusicXML export of a score set or a part: 422 for a request that cannot be carried out.</summary>
    public Job ExportMusicXml(MusicXmlExportRequest request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var state = view.State;
        var path = ExportPath(
            view,
            request?.FileName,
            request?.Directory,
            MusicXmlWriter.Extension(request?.Compressed ?? false)
        );
        if (request!.SetId is { } set && !state.ScoreSets.ContainsKey(set)) {
            throw ProjectException.Unprocessable($"No score set '{set}'.");
        }

        if (request.PartTrackId is { } part && (state.FindTrack(part) is not { } track || !Edits.HoldsMidi(track))) {
            throw ProjectException.Unprocessable($"No MIDI track '{part}'.");
        }

        return Enqueue(
            JobKind.MusicXmlExport,
            (entry, token) => Task.Run(
                IReadOnlyList<string>() => {
                    ScoreView score;
                    try {
                        score = notation.Render(
                            state,
                            new(request.SetId, request.PartTrackId, ScoreViewMode.Page)
                        );
                    } catch (ArgumentException e) {
                        throw ProjectException.Unprocessable(e.Message);
                    }

                    Progress(entry, 0.5);
                    token.ThrowIfCancellationRequested();
                    WriteFile(path, formats.WriteMusicXml(score, request.Compressed));
                    return [path];
                },
                token
            )
        );
    }

    /// <summary>Queues an AAF export of audio tracks: 404 for an unknown track, 422 for one that is not audio or no audio.</summary>
    public Job ExportAaf(AafExportRequest request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var state = view.State;
        var path = ExportPath(view, request?.FileName, request?.Directory, ".aaf");
        string[] tracks;
        if (request!.TrackIds is { } chosen) {
            tracks = [
                ..chosen.Distinct(StringComparer.Ordinal)
                    .Select(id => state.FindTrack(id ?? string.Empty)
                        ?? throw ProjectException.NotFound($"No track '{id}'.")
                    )
                    .Select(static t => t.Kind == TrackKind.Audio
                            ? t.Id
                            : throw ProjectException.Unprocessable($"Track '{t.Name}' is not an audio track.")
                    )
            ];
        } else {
            tracks = [
                ..state.Tracks.Where(t => t.Kind == TrackKind.Audio && state.RegionsOf(t.Id).Any())
                    .Select(static t => t.Id)
            ];
        }

        if (tracks.Length == 0) {
            throw ProjectException.Unprocessable("There are no audio tracks with regions to export.");
        }

        var resolved = request with { TrackIds = tracks };
        return Enqueue(
            JobKind.AafExport,
            (_, token) => Task.Run(
                () => {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    return formats.WriteAaf(state, view.Folder, resolved, path);
                },
                token
            )
        );
    }

    /// <summary>Queues a Standard MIDI File export: 404 for an unknown track or region, 422 for a request that cannot be.</summary>
    public Job ExportMidi(MidiExportRequest request) {
        var view = session.View ?? throw ProjectException.NoProject();
        var state = view.State;
        var path = ExportPath(view, request?.FileName, request?.Directory, ".mid");
        if (request!.Format is not (0 or 1)) {
            throw ProjectException.Unprocessable($"A MIDI file is format 0 or 1, not {request.Format}.");
        }

        if (request.RegionId is { } regionId) {
            if (!state.MidiRegions.ContainsKey(regionId)) {
                throw ProjectException.NotFound($"No MIDI region '{regionId}'.");
            }
        } else if (request.TrackIds is { } chosen) {
            foreach (var id in chosen) {
                var track = state.FindTrack(id ?? string.Empty) ?? throw ProjectException.NotFound($"No track '{id}'.");
                if (!Edits.HoldsMidi(track)) {
                    throw ProjectException.Unprocessable($"Track '{track.Name}' holds no MIDI.");
                }
            }
        } else if (!state.MidiRegions.Values.Any(r => state.FindTrack(r.TrackId) is { } t && Edits.HoldsMidi(t))) {
            throw ProjectException.Unprocessable("The project has no MIDI regions to export.");
        }

        return Enqueue(
            JobKind.MidiExport,
            (_, token) => Task.Run(
                IReadOnlyList<string>() => {
                    WriteFile(path, SmfExport.Write(state, request));
                    return [path];
                },
                token
            )
        );
    }

    /// <summary>Queues a MusicXML import from the server's disk: 422 when there is no such file.</summary>
    public Job ImportMusicXml(MusicXmlImportRequest request) => ImportMusicXmlFrom(ImportPath(request?.Path), null);

    /// <summary>Queues an AAF import from the server's disk: 422 when there is no such file.</summary>
    public Job ImportAaf(AafImportRequest request) => ImportAafFrom(ImportPath(request?.Path), null);

    /// <summary>Queues a Standard MIDI File import from the server's disk: 422 when there is no such file.</summary>
    public Job ImportMidi(MidiImportRequest request) =>
        ImportMidiFrom(
            (request ?? throw ProjectException.Unprocessable("Name the file to import.")) with {
                Path = ImportPath(request.Path)
            },
            null
        );

    /// <summary>Queues importing an uploaded MusicXML file (saved in the project for the job, removed after it).</summary>
    public async Task<Job> UploadMusicXmlAsync(IFormFile file, CancellationToken token) {
        var (path, scratch) = await SaveUploadAsync(file, token).ConfigureAwait(false);
        return ImportMusicXmlFrom(path, scratch);
    }

    /// <summary>Queues importing an uploaded AAF (embedded media only).</summary>
    public async Task<Job> UploadAafAsync(IFormFile file, CancellationToken token) {
        var (path, scratch) = await SaveUploadAsync(file, token).ConfigureAwait(false);
        return ImportAafFrom(path, scratch);
    }

    /// <summary>Queues importing an uploaded Standard MIDI File with the options given (left out: the defaults).</summary>
    public async Task<Job> UploadMidiAsync(
        IFormFile file,
        bool? importTempo,
        bool? importMarkers,
        bool? merge,
        CancellationToken token
    ) {
        var (path, scratch) = await SaveUploadAsync(file, token).ConfigureAwait(false);
        return ImportMidiFrom(new(path, importTempo ?? true, importMarkers ?? true, merge ?? false), scratch);
    }

    Job ImportMusicXmlFrom(string path, string? scratch) {
        var sessionId = (session.View ?? throw ProjectException.NoProject()).Session;
        return Enqueue(
            JobKind.MusicXmlImport,
            (entry, token) => Task.Run(
                IReadOnlyList<string>() => {
                    try {
                        var content = formats.ReadMusicXml(ReadBounded(path));
                        Progress(entry, 0.5);
                        token.ThrowIfCancellationRequested();
                        session.ApplyJob(
                            sessionId,
                            "Import MusicXML",
                            [],
                            (job, _) => job.Apply(
                                new ImportTracksCommand(
                                    // Into an empty project only, a score's tempo, meter and key replace the project's.
                                    job.State.Tracks.Count == 0
                                        ? content
                                        : content with { TempoEvents = null, Signatures = null, Keys = null }
                                )
                            )
                        );
                        return [];
                    } finally {
                        RemoveScratch(scratch);
                    }
                },
                token
            ),
            scratch
        );
    }

    Job ImportMidiFrom(MidiImportRequest request, string? scratch) {
        var sessionId = (session.View ?? throw ProjectException.NoProject()).Session;
        return Enqueue(
            JobKind.MidiImport,
            (entry, token) => Task.Run(
                IReadOnlyList<string>() => {
                    try {
                        var file = StandardMidiFile.Read(request.Path);
                        Progress(entry, 0.5);
                        token.ThrowIfCancellationRequested();
                        session.ApplyJob(
                            sessionId,
                            "Import MIDI File",
                            [],
                            (job, _) => job.Apply(
                                new ImportTracksCommand(SmfImport.ToContent(file, job.State, request))
                            )
                        );
                        return [];
                    } finally {
                        RemoveScratch(scratch);
                    }
                },
                token
            ),
            scratch
        );
    }

    Job ImportAafFrom(string path, string? scratch) {
        var open = session.View ?? throw ProjectException.NoProject();
        return Enqueue(
            JobKind.AafImport,
            (entry, token) => Task.Run(
                IReadOnlyList<string>() => {
                    var media = Path.Combine(open.Folder, $".aaf-{entry.Job.Id}");
                    var copied = new List<string>();
                    try {
                        var composition = formats.ReadAaf(path, media);
                        Progress(entry, 0.4);
                        var sources = composition.Tracks.SelectMany(static t => t.Clips)
                            .Select(static c => c.MediaPath)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
                        var files = new List<(string Path, WavInfo Info)>();
                        foreach (var source in sources) {
                            token.ThrowIfCancellationRequested();
                            var (_, brought, info) = import.Bring(source);
                            if (!string.Equals(brought, Path.GetFullPath(source), StringComparison.Ordinal)) {
                                copied.Add(brought);
                            }

                            files.Add((brought, info));
                        }

                        Progress(entry, 0.9);
                        session.ApplyJob(
                            open.Session,
                            "Import AAF",
                            files,
                            (job, added) => job.Apply(
                                new ImportTracksCommand(
                                    formats.AafContent(
                                        composition,
                                        job.State,
                                        sources.Select((s, i) => (s, added[i].Id))
                                            .ToDictionary(static p => p.s, static p => p.Id, StringComparer.Ordinal)
                                    )
                                )
                            )
                        );
                        return [..files.Select(static f => f.Path)];
                    } catch {
                        foreach (var file in copied) {
                            TryDelete(file);
                        }

                        throw;
                    } finally {
                        RemoveScratch(media);
                        RemoveScratch(scratch);
                    }
                },
                token
            ),
            scratch
        );
    }

    /// <summary>
    ///     Where an export writes: <paramref name="fileName" /> (1…128 characters, no path separators) with
    ///     <paramref name="extension" /> added when missing, in <paramref name="directory" /> or the project's
    ///     <c>Exports</c>.
    /// </summary>
    static string ExportPath(OpenProjectView view, string? fileName, string? directory, string extension) {
        var name = (fileName ?? string.Empty).Trim();
        if (name.Length is 0 or > Edits.MaxNameLength
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.StartsWith('.')) {
            throw ProjectException.Unprocessable("A file name has 1…128 characters and no path separators.");
        }

        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) {
            name += extension;
        }

        return Path.Combine(Folder(directory) ?? Path.Combine(view.Folder, ExportsFolder), name);
    }

    /// <summary>A file to import from the server's disk, which exists: its full path; 422 otherwise.</summary>
    static string ImportPath(string? path) {
        if (string.IsNullOrWhiteSpace(path)) {
            throw ProjectException.Unprocessable("Name the file to import.");
        }

        string full;
        try {
            full = Path.GetFullPath(path);
        } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) {
            throw ProjectException.Unprocessable($"'{path}' is not a file path: {e.Message}");
        }

        return File.Exists(full) ? full : throw ProjectException.Unprocessable($"'{full}' does not exist.");
    }

    /// <summary>Writes a file whole: into a temporary file beside it, then moved over it.</summary>
    static void WriteFile(string path, byte[] bytes) {
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".export-{Guid.NewGuid():n}.tmp");
        try {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        } finally {
            if (File.Exists(temporary)) {
                File.Delete(temporary);
            }
        }
    }

    static byte[] ReadBounded(string path) =>
        new FileInfo(path).Length > MaxImportBytes
            ? throw ProjectException.Unprocessable(
                $"'{path}' is larger than a file Anode imports ({MaxImportBytes >> 20} MB)."
            )
            : File.ReadAllBytes(path);

    /// <summary>
    ///     Saves an upload for an import job in a scratch folder of the open project (<c>.upload-&lt;id&gt;</c>), under its
    ///     own file name (the job names tracks after it): the file and the folder, which the job removes.
    /// </summary>
    async Task<(string Path, string Scratch)> SaveUploadAsync(IFormFile upload, CancellationToken token) {
        var (_, folder, _) = session.Target();
        var name = Path.GetFileName(upload.FileName ?? string.Empty);
        name = new string([..name.Select(static c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]).Trim();
        if (name.Length == 0 || name.StartsWith('.')) {
            name = "Upload" + name;
        }

        var scratch = Path.Combine(folder, $".upload-{Guid.NewGuid():n}");
        Directory.CreateDirectory(scratch);
        var path = Path.Combine(scratch, name);
        try {
            var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (target.ConfigureAwait(false)) {
                await upload.CopyToAsync(target, token).ConfigureAwait(false);
            }
        } catch {
            RemoveScratch(scratch);
            throw;
        }

        return (path, scratch);
    }

    void RemoveScratch(string? folder) {
        if (folder is null || !Directory.Exists(folder)) {
            return;
        }

        try {
            Directory.Delete(folder, true);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            ProjectLog.JobFailed(logger, "interchange", $"could not remove {folder}: {e.Message}");
        }
    }
}

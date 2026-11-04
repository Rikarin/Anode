using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.Time;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Anode.Model.Storage;

/// <summary>
///     <c>project.json</c>: the project's root (docs/01-implementation-plan.md §5.6). <c>tracks</c> lists the track ids
///     in order; each track and its regions are in <c>tracks/&lt;id&gt;.json</c>. Schema 3 adds the global tracks (in
///     <c>settings</c>), markers, arrangement sections, the project's notes and the editor's screensets; schema 4 the
///     mixer (the master's inserts, mixer settings), the mixer groups and the master's automation lanes; schema 5
///     Smart Tempo and Varispeed (in <c>settings</c>), tempo ramps' shapes and files' tempos; schema 6 the master's MIDI
///     FX (empty) and the Live Loops grid; schema 7 the groove templates, articulation sets and Step Editor lane sets
///     (docs/08-phase-7.md), with the groove track in <c>settings</c> and files' versions; schema 8 the sync settings;
///     schema 9 the score content (docs/10-phase-9.md: staff styles, symbols, lyrics, chord symbols, score sets, layout),
///     with tracks' staff styles and regions' notation in the track files; schema 10 (docs/11-phase-10.md) the
///     project's surround format (in <c>settings</c>), strips' formats and panners, markers' kinds and the movie;
///     schema 11 (docs/12-phase-11.md) the chord track and the Environment, with the Mastering Assistant's analysis in
///     its insert, and regions' Pitch Source and keys and the Session Player regions in the track files; schema 12 the
///     pattern regions in track alternatives.
/// </summary>
public sealed record ProjectFile(
    int SchemaVersion,
    string Id,
    string Name,
    int SampleRate,
    ProjectSettings Settings,
    ChannelStrip Master,
    IReadOnlyList<string> Tracks,
    IReadOnlyList<AudioFile> AudioFiles,
    IReadOnlyList<Marker>? Markers = null,
    IReadOnlyList<ArrangementMarker>? Arrangement = null,
    string? Notes = null,
    IReadOnlyList<Screenset>? Screensets = null,
    IReadOnlyList<MixerGroup>? Groups = null,
    IReadOnlyList<AutomationLane>? Automation = null,
    LiveLoopsGrid? LiveLoops = null,
    IReadOnlyList<GrooveTemplate>? Grooves = null,
    IReadOnlyList<ArticulationSet>? ArticulationSets = null,
    IReadOnlyList<LaneSet>? LaneSets = null,
    ProjectScore? Score = null,
    Movie? Movie = null,
    ChordTrack? ChordTrack = null,
    MidiEnvironment? Environment = null
);

/// <summary>
///     <c>tracks/&lt;id&gt;.json</c>: one track and its regions, audio and MIDI (schema 2), its take folders (schema 3),
///     its automation lanes (schema 4), and its flex and alternatives, and its regions' flex (schema 5); its MIDI FX,
///     drum machine, Smart Controls and Live Loops activation, and its pattern regions (schema 6); its groove matching
///     and articulation set, and its notes' articulations (schema 7); its Session Player regions (schema 11); its
///     alternatives' pattern regions (schema 12).
/// </summary>
public sealed record TrackFile(
    int SchemaVersion,
    Track Track,
    IReadOnlyList<AudioRegion> Regions,
    IReadOnlyList<MidiRegion>? MidiRegions = null,
    IReadOnlyList<TakeFolder>? TakeFolders = null,
    IReadOnlyList<AutomationLane>? Automation = null,
    IReadOnlyList<PatternRegion>? PatternRegions = null,
    IReadOnlyList<SessionPlayerRegion>? SessionPlayerRegions = null
);

/// <summary>A package that cannot be read: missing, damaged, or from a newer Anode.</summary>
public sealed class PackageException : Exception {
    public PackageException() { }

    public PackageException(string message) : base(message) { }

    public PackageException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
///     Reads and writes a project package: a folder with <c>project.json</c>, <c>tracks/&lt;id&gt;.json</c>,
///     <c>Audio Files/</c>, <c>Peaks/</c> (docs/02-phase-1.md) and <c>Plugin States/</c>: each instrument's opaque state
///     as <c>&lt;track id&gt;.bin</c> (docs/03-phase-2.md, "State") and each insert's as
///     <c>&lt;track id&gt;.&lt;insert id&gt;.bin</c> (<c>master.&lt;insert id&gt;.bin</c> on the master;
///     docs/05-phase-4.md). Every file is written atomically
///     (<see cref="AtomicRename" />): the track files first, then <c>project.json</c>, which names the tracks, so a save
///     cut short leaves the previous project readable; track files no longer named are removed last.
/// </summary>
public static class ProjectPackage {
    public const string ProjectFileName = "project.json";
    public const string TracksFolder = "tracks";
    public const string AudioFolder = "Audio Files";
    public const string PeaksFolder = "Peaks";
    public const string BackupsFolder = "Backups";
    public const string AutosaveFolder = "Autosave";
    public const string PluginStatesFolder = "Plugin States";
    public const string StateExtension = ".bin";

    /// <summary>The folder extension the server gives new projects.</summary>
    public const string Extension = ".anode";

    public static bool Exists(string folder) => File.Exists(Path.Combine(folder, ProjectFileName));

    /// <summary>Creates the package's folders (they may exist).</summary>
    public static void CreateFolders(string folder) {
        Directory.CreateDirectory(Path.Combine(folder, TracksFolder));
        Directory.CreateDirectory(Path.Combine(folder, AudioFolder));
        Directory.CreateDirectory(Path.Combine(folder, PeaksFolder));
    }

    /// <summary>
    ///     Writes <paramref name="state" />'s document; with <paramref name="media" />, also creates the media folders
    ///     (a backup has none of its own).
    /// </summary>
    public static void Save(string folder, ProjectState state, bool media = true) =>
        Save(folder, state, media, AtomicRename.Write);

    /// <summary>
    ///     Writes a package as <see cref="Save(string, ProjectState, bool)" /> does, each file through
    ///     <paramref name="write" /> (an autosave skips the files that have not changed).
    /// </summary>
    public static void Save(string folder, ProjectState state, bool media, Action<string, byte[]> write) {
        if (media) {
            CreateFolders(folder);
        } else {
            Directory.CreateDirectory(Path.Combine(folder, TracksFolder));
        }

        var tracksFolder = Path.Combine(folder, TracksFolder);
        var byTrack = state.Regions.Values.ToLookup(static r => r.TrackId, StringComparer.Ordinal);
        var midiByTrack = state.MidiRegions.Values.ToLookup(static r => r.TrackId, StringComparer.Ordinal);
        foreach (var track in state.Tracks) {
            var file = new TrackFile(
                PackageMigrations.CurrentSchemaVersion,
                track,
                [..byTrack[track.Id].OrderBy(static r => r.StartTick).ThenBy(static r => r.Id, StringComparer.Ordinal)],
                [
                    ..midiByTrack[track.Id]
                        .OrderBy(static r => r.StartTick)
                        .ThenBy(static r => r.Id, StringComparer.Ordinal)
                ],
                [..state.TakeFoldersOf(track.Id)],
                [..state.LanesOf(track.Id)],
                [..state.PatternRegionsOf(track.Id)],
                [..state.SessionPlayerRegionsOf(track.Id)]
            );
            write(
                Path.Combine(tracksFolder, TrackFileName(track.Id)),
                JsonSerializer.SerializeToUtf8Bytes(file, PackageJsonContext.Default.TrackFile)
            );
        }

        var root = new ProjectFile(
            PackageMigrations.CurrentSchemaVersion,
            state.Id,
            state.Name,
            state.SampleRate,
            state.Settings,
            state.Master,
            [..state.Tracks.Select(static t => t.Id), ..state.UnreadableTracks],
            [..state.Files.Values],
            [..state.Markers.Values.OrderBy(static m => m.Tick).ThenBy(static m => m.Id, StringComparer.Ordinal)],
            [..state.Sections.Values.OrderBy(static s => s.StartTick)],
            state.Notes,
            [..state.Screensets.Values],
            [..state.Groups.Values],
            [..state.LanesOf(null)],
            state.LiveLoops,
            [..state.Grooves.Values],
            [..state.ArticulationSets.Values],
            [..state.LaneSets.Values],
            state.Score,
            state.Movie,
            state.ChordTrack,
            state.Environment
        );
        write(
            Path.Combine(folder, ProjectFileName),
            JsonSerializer.SerializeToUtf8Bytes(root, PackageJsonContext.Default.ProjectFile)
        );

        var kept = state.Tracks.Select(static t => t.Id)
            .Concat(state.UnreadableTracks)
            .Select(TrackFileName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var stale in Directory.EnumerateFiles(tracksFolder, "*.json")) {
            if (!kept.Contains(Path.GetFileName(stale))) {
                File.Delete(stale);
            }
        }
    }

    /// <summary>
    ///     Reads a package. A track file that is missing or unreadable is left out rather than failing the whole project,
    ///     and stays named (<see cref="ProjectState.UnreadableTracks" />), so saving does not lose it. Whatever names an
    ///     audio file <c>project.json</c> does not list keeps it: the file comes back as an entry of unknown format
    ///     (<see cref="OfflineFiles" />), offline until relinked, and a region stored under another track's id stays on the
    ///     track whose file holds it. A damaged or newer <c>project.json</c> throws <see cref="PackageException" />.
    /// </summary>
    public static ProjectState Load(string folder) {
        var root = Read(
            Path.Combine(folder, ProjectFileName),
            PackageJsonContext.Default.ProjectFile,
            PackageKeys.ProjectKind
        );
        // Deserialisation leaves a property the JSON lacks null, whatever its declared type.
        if (root?.Id is null
            || root.Name is null
            || root.Settings is null
            || root.Master is null
            || !ProjectDefaults.SampleRates.Contains(root.SampleRate)) {
            throw new PackageException($"'{folder}' has no readable {ProjectFileName}.");
        }

        var tracks = ImmutableList.CreateBuilder<Track>();
        var regions = ImmutableSortedDictionary.CreateBuilder<string, AudioRegion>(StringComparer.Ordinal);
        var midiRegions = ImmutableSortedDictionary.CreateBuilder<string, MidiRegion>(StringComparer.Ordinal);
        var folders = ImmutableSortedDictionary.CreateBuilder<string, TakeFolder>(StringComparer.Ordinal);
        var patterns = ImmutableSortedDictionary.CreateBuilder<string, PatternRegion>(StringComparer.Ordinal);
        var players = ImmutableSortedDictionary.CreateBuilder<string, SessionPlayerRegion>(StringComparer.Ordinal);
        var trackFiles = new List<TrackFile>();
        var unreadable = ImmutableList.CreateBuilder<string>();
        foreach (var id in (root.Tracks ?? []).Distinct(StringComparer.Ordinal)) {
            if (ReadTrack(folder, id) is { } file) {
                var track = Normalise(file.Track);
                tracks.Add(track);
                trackFiles.Add(file with { Track = track });
            } else if (id is not null && IsSafeId(id)) {
                unreadable.Add(id);
            }
        }

        var files = OfflineFiles.WithUnlisted(root, trackFiles);
        Unnest(tracks);
        foreach (var file in trackFiles) {
            AddRegions(file, file.Track, files, regions, midiRegions, folders);
            AddPatterns(file, file.Track, patterns);
            AddSessionPlayers(file, file.Track, players);
        }

        var groups = ImmutableSortedDictionary.CreateRange(
            (root.Groups ?? [])
            .Where(static g => g is {
                    Number: >= 1 and <= Edits.MaxGroup, Name: not null, Color: not null, Properties: not null
                }
            )
            .Select(static g => KeyValuePair.Create(g.Number, g))
            .DistinctBy(static p => p.Key)
        );
        Relink(tracks, groups, files);
        DrumMachines(tracks);
        var grooves = Checked(root.Grooves, Edits.CheckGroove, static g => g.Id);
        var articulationSets = Checked(root.ArticulationSets, Edits.CheckArticulationSet, static a => a.Id);
        var laneSets = Checked(root.LaneSets, Edits.CheckLaneSet, static l => l.Id);
        Unassigned(tracks, articulationSets);

        var master = Normalise(root.Master, null);
        var trackIds = tracks.Select(static t => t.Id).ToHashSet(StringComparer.Ordinal);
        var loaded = new ProjectState(
            root.Id,
            root.Name,
            root.SampleRate,
            Normalise(root.Settings, tracks),
            master with {
                Inserts = [..master.Inserts.Select(i => i with { Sidechain = Sidechain(trackIds, i.Sidechain) })]
            },
            tracks.ToImmutable(),
            regions.ToImmutable(),
            files,
            midiRegions.ToImmutable()
        ) { Groups = groups, Grooves = grooves, ArticulationSets = articulationSets, LaneSets = laneSets };
        var liveLoops = LiveLoops(root.LiveLoops, tracks, files);
        var read = loaded with {
            LastEventId = Math.Max(
                midiRegions.Values.Select(ProjectState.MaxEventId).DefaultIfEmpty().Max(),
                ProjectState.MaxEventId(liveLoops)
            ),
            TakeFolders = folders.ToImmutable(),
            PatternRegions = patterns.ToImmutable(),
            SessionPlayerRegions = players.ToImmutable(),
            UnreadableTracks = unreadable.ToImmutable(),
            ChordTrack = ChordTrack(root.ChordTrack),
            LiveLoops = liveLoops,
            Movie = Movie(root.Movie),
            Markers = Keyed(
                root.Markers?.Select(Marker).ToArray(),
                static m => m?.Id is not null && m.Name is not null,
                static m => m.Id
            ),
            Sections = Keyed(
                root.Arrangement,
                static s => s?.Id is not null && s.Name is not null && s.Color is not null,
                static s => s.Id
            ),
            Notes = root.Notes ?? string.Empty,
            Screensets = ImmutableSortedDictionary.CreateRange(
                (root.Screensets ?? [])
                .Where(static s => s is { Number: >= 1 and <= 9, Name: not null, Layout: not null })
                .Select(static s => KeyValuePair.Create(s.Number, s))
                .DistinctBy(static p => p.Key)
            ),
            Automation = Lanes(
                loaded,
                [
                    ..trackFiles.SelectMany(static f => (f.Automation ?? []).Select(l => l with { TrackId = f.Track.Id }
                        )
                    ),
                    ..(root.Automation ?? []).Where(static l => l is not null)
                        .Select(static l => l with { TrackId = null })
                ]
            )
        };
        // The Environment's track inputs and control targets, and the rhythm sources, need the tracks read.
        read = read with {
            Environment = Edits.StoredEnvironment(read, root.Environment),
            SessionPlayerRegions = Followed(read, read.SessionPlayerRegions)
        };
        // Output formats and panner modes follow the routing (docs/11-phase-10.md), whatever the file says.
        return Edits.Conformed(WithScore(read, root.Score));
    }

    /// <summary>A marker as read: its text (none: empty) and a kind there is (else a user's marker).</summary>
    static Marker Marker(Marker? marker) {
        if (marker is null) {
            return marker!;
        }

        var kind = Enum.IsDefined(marker.Kind) ? marker.Kind : MarkerKind.Marker;
        return marker with { Text = marker.Text ?? string.Empty, Kind = kind };
    }

    /// <summary>
    ///     The chord track as read (schema 11): the chords and key and scale changes that pass their commands' checks,
    ///     the first at each tick, in tick order.
    /// </summary>
    static ChordTrack ChordTrack(ChordTrack? stored) {
        if (stored is null) {
            return Contracts.ChordTrack.Empty;
        }

        var chords = new List<Chord>();
        foreach (var chord in stored.Chords ?? []) {
            try {
                if (chord?.Id is not null) {
                    chords.Add(Edits.CheckChord(chord));
                }
            } catch (EditException) {
                // a chord the chord track cannot hold
            }
        }

        var scales = (stored.Scales ?? []).Where(static s => s is { Tick: >= 0 and <= Edits.MaxTick, Tonic: not null }
            && Enum.IsDefined(s.Tonic.Step)
            && s.Tonic.Alter is >= -2 and <= 2
            && Enum.IsDefined(s.Scale)
            )
            .DistinctBy(static s => s.Tick)
            .OrderBy(static s => s.Tick);
        return new(
            [
                ..chords.DistinctBy(static c => c.Id)
                    .DistinctBy(static c => c.Tick)
                    .OrderBy(static c => c.Tick)
                    .Take(Edits.MaxChords)
            ],
            [..scales.Take(Edits.MaxScales)]
        );
    }

    /// <summary>
    ///     A track file's Session Player regions that belong to it (schema 11): on a track of their player, with a style,
    ///     a pad and a length, only their player's switches (its defaults when left out) and their values in range.
    ///     Styles are not checked against a library: a region of a style no longer installed is kept.
    /// </summary>
    static void AddSessionPlayers(
        TrackFile file,
        Track track,
        ImmutableSortedDictionary<string, SessionPlayerRegion>.Builder players
    ) {
        if (Edits.PlayerOf(track.Kind) is not { } player) {
            return;
        }

        foreach (var region in file.SessionPlayerRegions ?? []) {
            if (region?.Id is not null
                && region.TrackId == track.Id
                && region is { StartTick: >= 0 and <= Edits.MaxTick, LengthTicks: >= 1 and <= Edits.MaxTick }
                && region.Parameters is { StyleId.Length: > 0, Pad: not null } parameters) {
                players[region.Id] = region with {
                    Name = region.Name ?? track.Name, Parameters = Edits.StoredParameters(parameters, player)
                };
            }
        }
    }

    /// <summary>Session Player regions whose rhythm source is a track of the project with notes or audio (else none).</summary>
    static ImmutableSortedDictionary<string, SessionPlayerRegion> Followed(
        ProjectState state,
        ImmutableSortedDictionary<string, SessionPlayerRegion> regions
    ) =>
        regions.SetItems(
            regions.Values.Where(r => r.Parameters.Drums?.RhythmSourceTrackId is { } source
                    && (source == r.TrackId
                        || state.FindTrack(source) is not { } t
                        || !(t.Kind == TrackKind.Audio || Edits.HoldsMidi(t)))
                )
                .Select(static r => KeyValuePair.Create(
                        r.Id,
                        r with {
                            Parameters = r.Parameters with {
                                Drums = r.Parameters.Drums! with { RhythmSourceTrackId = null }
                            }
                        }
                    )
                )
        );

    /// <summary>The movie as read: one the project could have been given (<see cref="Edits.CheckMovie" />), else none.</summary>
    static Movie? Movie(Movie? movie) {
        if (movie is null) {
            return null;
        }

        try {
            return Edits.CheckMovie(movie);
        } catch (EditException) {
            return null;
        }
    }

    /// <summary>
    ///     The score content as read (schema 9, docs/10-phase-9.md): each item that passes its command's checks against
    ///     the project read so far, the first of each id; tracks name a staff style that exists (else none); a damaged
    ///     layout keeps what still holds, else is the default.
    /// </summary>
    static ProjectState WithScore(ProjectState state, ProjectScore? score) {
        var styles = Checked(score?.StaffStyles, static s => Edits.CheckStaffStyle(s, true), static s => s.Id);
        state = state with {
            StaffStyles = styles,
            Tracks = [
                ..state.Tracks.Select(t => t.StaffStyleId is { } id
                    && (!Edits.IsMidiKind(t.Kind) || !(StaffStyleIds.All.Contains(id) || styles.ContainsKey(id)))
                        ? t with { StaffStyleId = null }
                        : t
                )
            ]
        };
        var symbols = Checked(score?.Symbols, s => Edits.CheckStoredSymbol(state, s), static s => s.Id);
        var chords = Checked(score?.Chords, c => Edits.CheckStoredChord(state, c), static c => c.Id);
        var lyrics = Checked(score?.Lyrics, l => Edits.CheckStoredLyric(state, l), static l => l.Id);
        var verses = new HashSet<(long, int)>();
        var sets = Checked(score?.Sets, s => Edits.CheckStoredSet(state, s), static s => s.Id);
        return state with {
            ScoreSymbols = symbols,
            ChordSymbols = chords,
            Lyrics = lyrics.RemoveRange(
                lyrics.Values.Where(l => !verses.Add((l.NoteId, l.Verse))).Select(static l => l.Id).ToArray()
            ),
            ScoreSets = sets,
            ScoreLayout = Edits.CheckStoredLayout(state, score?.Layout)
        };
    }

    /// <summary>
    ///     The lanes that belong: of a known strip and target, with sorted, well-formed points; one per strip and
    ///     target (the first).
    /// </summary>
    static ImmutableSortedDictionary<string, AutomationLane> Lanes(
        ProjectState state,
        IEnumerable<AutomationLane> lanes
    ) {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, AutomationLane>(StringComparer.Ordinal);
        var seen = new HashSet<(string?, string)>();
        foreach (var lane in lanes) {
            if (lane?.Id is null
                || lane.Target is null
                || builder.ContainsKey(lane.Id)
                || !seen.Add((lane.TrackId, lane.Target))
                || !Edits.TargetExists(state, lane.TrackId, lane.Target)) {
                continue;
            }

            var points = (lane.Points ?? []).Where(static p => p is not null && double.IsFinite(p.Value) && p.Tick >= 0)
                .Select(static p => p with { Curve = double.IsFinite(p.Curve) ? Math.Clamp(p.Curve, -1, 1) : 0 })
                .OrderBy(static p => p.Tick)
                .ToArray();
            if (points.Length > 0) {
                builder[lane.Id] = lane with { Points = points };
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    ///     References between tracks made safe: a VCA that is one, groups that exist, a frozen file of the project, an
    ///     aux's input bus, sidechains from tracks of the project.
    /// </summary>
    static void Relink(
        ImmutableList<Track>.Builder tracks,
        ImmutableSortedDictionary<int, MixerGroup> groups,
        ImmutableSortedDictionary<string, AudioFile> files
    ) {
        var vcas = tracks.Where(static t => t.Kind == TrackKind.Vca).Select(static t => t.Id).ToHashSet();
        var ids = tracks.Select(static t => t.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < tracks.Count; i++) {
            var t = tracks[i];
            var strip = t.Strip;
            tracks[i] = t with {
                Strip = strip with {
                    VcaId = strip.VcaId is { } vca && vcas.Contains(vca) && t.Kind != TrackKind.Vca ? vca : null,
                    Groups = [..strip.Groups.Where(groups.ContainsKey).Distinct().Order()],
                    Inserts = [..strip.Inserts.Select(x => x with { Sidechain = Sidechain(ids, x.Sidechain) })]
                },
                FrozenFileId = t.FrozenFileId is { } frozen && files.ContainsKey(frozen) ? frozen : null,
                InputBus = t.Kind == TrackKind.Aux ? Math.Clamp(t.InputBus, 1, Edits.MaxBus) : 0
            };
            (var alternatives, var active) = Alternatives(tracks[i], files);
            tracks[i] = tracks[i] with {
                Alternatives = alternatives,
                AlternativeId = active,
                FlexMode = Enum.IsDefined(tracks[i].FlexMode) ? tracks[i].FlexMode : FlexMode.Polyphonic
            };
        }
    }

    /// <summary>A sidechain source that exists (a track of the project, a bus), else none.</summary>
    static string? Sidechain(HashSet<string> trackIds, string? source) =>
        source is not null && (trackIds.Contains(source) || Routing.BusOf(source) is >= 1 and <= Edits.MaxBus)
            ? source
            : null;

    /// <summary>
    ///     Stacks do not nest, and a member's stack is one of the project's: other parents are dropped. Members follow
    ///     their stack (a project saved before every command kept them there is put right).
    /// </summary>
    static void Unnest(ImmutableList<Track>.Builder tracks) {
        var stacks = tracks.Where(static t => Edits.IsStackKind(t.Kind)).Select(static t => t.Id).ToHashSet();
        for (var i = 0; i < tracks.Count; i++) {
            var t = tracks[i];
            if (t.ParentId is not null && (Edits.IsStackKind(t.Kind) || !stacks.Contains(t.ParentId))) {
                tracks[i] = t with { ParentId = null };
            }
        }

        var grouped = Edits.Grouped(tracks.ToImmutable());
        tracks.Clear();
        tracks.AddRange(grouped);
    }

    /// <summary>A track's articulation set is one of the project's, on a track that has one, or none.</summary>
    static void Unassigned(
        ImmutableList<Track>.Builder tracks,
        ImmutableSortedDictionary<string, ArticulationSet> sets
    ) {
        for (var i = 0; i < tracks.Count; i++) {
            if (tracks[i].ArticulationSetId is { } setId
                && (!sets.ContainsKey(setId) || !Edits.IsMidiKind(tracks[i].Kind))) {
                tracks[i] = tracks[i] with { ArticulationSetId = null };
            }
        }
    }

    /// <summary>The items <paramref name="check" /> accepts, as it returns them, keyed by id (the first of each id).</summary>
    static ImmutableSortedDictionary<string, T> Checked<T>(
        IReadOnlyList<T>? items,
        Func<T?, T> check,
        Func<T, string> id
    ) where T : class {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, T>(StringComparer.Ordinal);
        foreach (var item in items ?? []) {
            try {
                var good = check(item);
                builder.TryAdd(id(good), good);
            } catch (EditException) {
                // A damaged entry is left out.
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>The items that pass <paramref name="valid" />, keyed by id (the first of each id).</summary>
    static ImmutableSortedDictionary<string, T> Keyed<T>(
        IReadOnlyList<T>? items,
        Func<T, bool> valid,
        Func<T, string> id
    ) where T : class =>
        ImmutableSortedDictionary.CreateRange(
            StringComparer.Ordinal,
            (items ?? []).Where(valid).Select(i => KeyValuePair.Create(id(i), i)).DistinctBy(static p => p.Key)
        );

    /// <summary>
    ///     Settings as read: the global tracks present and well formed (else the project-start values alone), the first
    ///     events equal to <c>tempo</c> and <c>signature</c>, and recording settings.
    /// </summary>
    static ProjectSettings Normalise(ProjectSettings settings, ImmutableList<Track>.Builder tracks) {
        var signature = settings.Signature ?? new TimeSignature(4, 4);
        TempoEvent[] tempo =
            settings.TempoEvents is { Count: > 0 } t ? [..t] : [new(0, settings.Tempo, TempoCurve.Step)];
        SignatureEvent[] signatures = settings.Signatures is { Count: > 0 } s
            ? [..s]
            : [new(0, signature.Numerator, signature.Denominator)];
        KeyEvent[] keys = settings.Keys is { Count: > 0 } k ? [..k] : [new(0, 0, KeyMode.Major)];
        try {
            _ = TempoMap.Of(tempo);
        } catch (ArgumentException) {
            tempo = [new(0, Math.Clamp(settings.Tempo, TempoMap.MinBpm, TempoMap.MaxBpm), TempoCurve.Step)];
        }

        try {
            _ = new SignatureMap(signatures);
        } catch (Exception e) when (e is ArgumentException or NullReferenceException or DivideByZeroException) {
            signatures = [new(0, signature.Numerator, signature.Denominator)];
        }

        if (keys.Any(static e => e is null) || keys[0].Tick != 0) {
            keys = [new(0, 0, KeyMode.Major)];
        }

        return settings with {
            Tempo = tempo[0].Bpm,
            TempoEvents = tempo,
            Signature = new(signatures[0].Numerator, signatures[0].Denominator),
            Signatures = signatures,
            Keys = keys,
            Recording = settings.Recording ?? ProjectDefaults.Recording(signature),
            Mixer = settings.Mixer is { } mixer && Enum.IsDefined(mixer.Pdc) && double.IsFinite(mixer.LowLatencyLimitMs)
                ? mixer
                : ProjectDefaults.Mixer,
            SmartTempo = settings.SmartTempo is { } smart
                && Enum.IsDefined(smart.Mode)
                && Enum.IsDefined(smart.DefaultFollow)
                    ? smart
                    : ProjectDefaults.SmartTempo,
            Varispeed = settings.Varispeed is { Speed: >= Edits.MinSpeed and <= Edits.MaxSpeed } varispeed
                && Enum.IsDefined(varispeed.Mode)
                    ? varispeed
                    : ProjectDefaults.Varispeed,
            GrooveTrackId = GrooveTrack(settings.GrooveTrackId, tracks),
            SurroundFormat = Known(settings.SurroundFormat)
        };
    }

    /// <summary>A channel format as read: one there is, else stereo (Phase 10).</summary>
    static ChannelFormat Known(ChannelFormat format) => Enum.IsDefined(format) ? format : ChannelFormat.Stereo;

    /// <summary>The groove track as read: a track of the project with regions to take a groove from, or none.</summary>
    static string? GrooveTrack(string? id, ImmutableList<Track>.Builder tracks) =>
        id is not null && tracks.Any(t => t.Id == id && (t.Kind == TrackKind.Audio || Edits.HoldsMidi(t))) ? id : null;

    /// <summary>
    ///     A track file's regions that belong to it: audio ones on an audio track, MIDI ones on a MIDI track. An audio
    ///     region or take folder naming another track stays here, as the file that holds it says.
    /// </summary>
    static void AddRegions(
        TrackFile file,
        Track track,
        ImmutableSortedDictionary<string, AudioFile> files,
        ImmutableSortedDictionary<string, AudioRegion>.Builder regions,
        ImmutableSortedDictionary<string, MidiRegion>.Builder midiRegions,
        ImmutableSortedDictionary<string, TakeFolder>.Builder folders
    ) {
        foreach (var region in file.Regions ?? []) {
            if (region?.Id is not null
                && track.Kind == TrackKind.Audio
                && files.ContainsKey(region.FileId ?? string.Empty)) {
                regions[region.Id] = Normalise(region) with { TrackId = track.Id };
            }
        }

        foreach (var region in file.MidiRegions ?? []) {
            if (region?.TrackId == track.Id && Edits.HoldsMidi(track) && region.Id is not null) {
                midiRegions[region.Id] = Normalise(region);
            }
        }

        foreach (var folder in file.TakeFolders ?? []) {
            if (folder?.Id is not null
                && track.Kind == TrackKind.Audio
                && folder.Takes is { Count: > 0 } takes
                && takes.All(t => t is not null && files.ContainsKey(t.FileId ?? string.Empty))) {
                var ids = takes.Select(static t => t.Id).ToHashSet();
                folders[folder.Id] = folder with {
                    TrackId = track.Id,
                    Name = folder.Name ?? track.Name,
                    Comp = [..(folder.Comp ?? []).Where(s => s is not null && ids.Contains(s.TakeId))]
                };
            }
        }
    }

    /// <summary>A track file's pattern regions that belong to it: well formed, on a track that holds MIDI.</summary>
    static void AddPatterns(
        TrackFile file,
        Track track,
        ImmutableSortedDictionary<string, PatternRegion>.Builder patterns
    ) {
        if (!Edits.HoldsMidi(track)) {
            return;
        }

        foreach (var region in file.PatternRegions ?? []) {
            if (region?.Id is not null
                && region.TrackId == track.Id
                && region.Name is not null
                && Valid(region.Pattern)) {
                patterns[region.Id] = region with { Pattern = region.Pattern with { Rows = [..region.Pattern.Rows] } };
            }
        }
    }

    static bool Valid(Pattern? pattern) {
        try {
            _ = Edits.CheckPattern(pattern);
            return true;
        } catch (EditException) {
            return false;
        }
    }

    /// <summary>
    ///     The Live Loops grid as read: scenes with ids, cells on tracks and scenes of the project with exactly one
    ///     content of a kind their track holds (an audio cell of a known file), one per slot.
    /// </summary>
    static LiveLoopsGrid LiveLoops(
        LiveLoopsGrid? grid,
        ImmutableList<Track>.Builder tracks,
        ImmutableSortedDictionary<string, AudioFile> files
    ) {
        if (grid is null) {
            return ProjectDefaults.LiveLoops;
        }

        var scenes = (grid.Scenes ?? []).Where(static s => s?.Id is not null && s.Name is not null)
            .DistinctBy(static s => s.Id)
            .ToArray();
        var sceneIds = scenes.Select(static s => s.Id).ToHashSet(StringComparer.Ordinal);
        var byId = tracks.ToDictionary(static t => t.Id, StringComparer.Ordinal);
        var cells = (grid.Cells ?? [])
            .Where(c => c?.Id is not null
                && c.Name is not null
                && c.Content is not null
                && sceneIds.Contains(c.SceneId ?? string.Empty)
                && byId.TryGetValue(c.TrackId ?? string.Empty, out var track)
                && Enum.IsDefined(c.PlayMode)
                && c.LengthTicks > 0
                && Fits(c, track, files)
            )
            .Select(static c => c.Content.Midi is { } m ? c with { Content = new(Midi: Normalise(m)) } : c)
            .Select(static c => c.Content.Audio is { } a ? c with { Content = new(Normalise(a)) } : c)
            .DistinctBy(static c => c.Id)
            .DistinctBy(static c => (c.TrackId, c.SceneId))
            .ToArray();
        return grid with {
            Scenes = scenes,
            Cells = cells,
            Quantize = Enum.IsDefined(grid.Quantize) ? grid.Quantize : ProjectDefaults.LiveLoops.Quantize
        };
    }

    /// <summary>Whether a cell's content is one snapshot of its kind, on a track that holds it.</summary>
    static bool Fits(Cell cell, Track track, ImmutableSortedDictionary<string, AudioFile> files) =>
        cell.Kind switch {
            CellKind.Audio => cell.Content is { Audio: { } a, Midi: null, Pattern: null }
                && track.Kind == TrackKind.Audio
                && files.ContainsKey(a.FileId ?? string.Empty),
            CellKind.Midi => cell.Content is { Midi: not null, Audio: null, Pattern: null } && Edits.HoldsMidi(track),
            CellKind.Pattern => cell.Content is { Pattern: { } p, Audio: null, Midi: null }
                && Edits.HoldsMidi(track)
                && Valid(p.Pattern),
            _ => false
        };

    /// <summary>
    ///     A Drum Machine Designer is a stack; its pads are its own members, one per note, sorted; other tracks have no
    ///     drum machine.
    /// </summary>
    static void DrumMachines(ImmutableList<Track>.Builder tracks) {
        for (var i = 0; i < tracks.Count; i++) {
            var t = tracks[i];
            if (t.DrumMachine is null) {
                continue;
            }

            if (!Edits.IsStackKind(t.Kind)) {
                tracks[i] = t with { DrumMachine = null };
                continue;
            }

            var pads = (t.DrumMachine.Pads ?? [])
                .Where(p => p?.TrackId is not null
                    && p.Name is not null
                    && p.Note is >= 0 and <= 127
                    && tracks.Any(m => m.Id == p.TrackId && m.ParentId == t.Id)
                )
                .DistinctBy(static p => p.Note)
                .DistinctBy(static p => p.TrackId)
                .OrderBy(static p => p.Note)
                .ToArray();
            tracks[i] = t with { DrumMachine = new(pads) };
        }
    }

    /// <summary>
    ///     Writes the plugins' opaque states by key (<see cref="StateKey" />): <c>Plugin States/&lt;key&gt;.bin</c> each,
    ///     and removes the state files of keys not in <paramref name="states" />.
    /// </summary>
    public static void SaveStates(string folder, IReadOnlyDictionary<string, byte[]> states) =>
        SaveStates(folder, states, AtomicRename.Write);

    /// <summary>
    ///     Writes the plugin states as <see cref="SaveStates(string, IReadOnlyDictionary{string, byte[]})" /> does, each
    ///     file through <paramref name="write" />.
    /// </summary>
    public static void SaveStates(
        string folder,
        IReadOnlyDictionary<string, byte[]> states,
        Action<string, byte[]> write
    ) {
        var statesFolder = Path.Combine(folder, PluginStatesFolder);
        if (states.Count > 0) {
            Directory.CreateDirectory(statesFolder);
        }

        foreach (var (trackId, state) in states) {
            write(Path.Combine(statesFolder, StateFileName(trackId)), state);
        }

        if (Directory.Exists(statesFolder)) {
            foreach (var stale in Directory.EnumerateFiles(statesFolder, "*" + StateExtension)) {
                if (!states.ContainsKey(Path.GetFileNameWithoutExtension(stale))) {
                    File.Delete(stale);
                }
            }
        }
    }

    /// <summary>
    ///     The saved states of <paramref name="state" />'s plugins by key (<see cref="StateKey" />): instruments and
    ///     inserts, the master's too (unreadable ones left out).
    /// </summary>
    public static Dictionary<string, byte[]> LoadStates(string folder, ProjectState state) {
        var states = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var key in StateKeys(state)) {
            var path = Path.Combine(folder, PluginStatesFolder, StateFileName(key));
            try {
                if (File.Exists(path)) {
                    states[key] = File.ReadAllBytes(path);
                }
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                // An unreadable state: the plugin starts from its parameters.
            }
        }

        return states;
    }

    /// <summary>The state key of every plugin of the project (with a safe id), and the plugin it names.</summary>
    public static IEnumerable<string> StateKeys(ProjectState state) {
        foreach (var track in state.Tracks.Where(static t => IsSafeId(t.Id))) {
            if (track.Instrument is not null) {
                yield return track.Id;
            }

            foreach (var insert in track.Strip.Inserts.Where(static i => IsSafeId(i.Id))) {
                yield return StateKey(track.Id, insert.Id);
            }
        }

        foreach (var insert in state.Master.Inserts.Where(static i => IsSafeId(i.Id))) {
            yield return StateKey(null, insert.Id);
        }
    }

    /// <summary>The master's name in state keys.</summary>
    public const string MasterKey = "master";

    /// <summary>
    ///     The key of a plugin's state: the track id for its instrument, <c>&lt;track id&gt;.&lt;insert id&gt;</c> for an
    ///     insert (<c>master.&lt;insert id&gt;</c> on the master).
    /// </summary>
    public static string StateKey(string? trackId, string? insertId) =>
        insertId is null ? trackId ?? MasterKey : $"{trackId ?? MasterKey}.{insertId}";

    public static string StateFileName(string key) =>
        key.Split('.') is { Length: 1 or 2 } parts && parts.All(IsSafeId)
            ? key + StateExtension
            : throw new ArgumentException($"Unsafe state key '{key}'.");

    /// <summary>A track as read, in the model's canonical form (parameters in ordinal order, lists present).</summary>
    static Track Normalise(Track track) =>
        track with {
            Instrument = track.Instrument is { } slot
                ? slot with { Parameters = PluginCatalog.Sorted(slot.Parameters) }
                : null,
            MidiInput = track.MidiInput ?? ProjectDefaults.MidiInput,
            MidiOutput = track.Kind == TrackKind.Midi ? track.MidiOutput : null,
            Notes = track.Notes ?? string.Empty,
            Strip = Normalise(track.Strip, track),
            AutomationMode = Enum.IsDefined(track.AutomationMode) ? track.AutomationMode : AutomationMode.Read,
            SmartControls = Normalise(track.SmartControls, track)
        };

    /// <summary>A value 0…1 as read (0 when it is not a number).</summary>
    static double Unit(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    /// <summary>A track's Smart Controls as read: every control of its layout, mappings well formed (null: none yet).</summary>
    static SmartControls? Normalise(SmartControls? smart, Track track) {
        if (smart is null || !Edits.HasAudioStrip(track.Kind)) {
            return null;
        }

        var layout = Enum.IsDefined(smart.Layout) ? smart.Layout : SmartControlsLayout.Knobs8;
        var defaults = Edits.SmartControlsOf(
            track with { SmartControls = null, Strip = Normalise(track.Strip, track) }
        );
        var saved = (smart.Controls ?? []).Where(static c => c?.Id is not null)
            .DistinctBy(static c => c.Id)
            .ToDictionary(static c => c.Id, StringComparer.Ordinal);
        var controls = Edits.ControlIds(layout)
            .Select(id => saved.TryGetValue(id, out var c)
                    ? c with {
                        Name = c.Name ?? string.Empty,
                        Kind = Edits.SmartControlKindOf(id),
                        Value = Unit(c.Value),
                        Mappings = [
                            ..(c.Mappings ?? []).Where(static m => m?.Target is not null)
                                .Select(static m => m with { Graph = m.Graph ?? SmartMath.Line })
                        ]
                    }
                    : new SmartControl(id, string.Empty, Edits.SmartControlKindOf(id), 0, null, [])
            )
            .ToArray();
        return new(layout, controls, defaults.Arpeggiator, smart.Comparing);
    }

    /// <summary>A strip as read: its lists present and well formed, its output one there is.</summary>
    static ChannelStrip Normalise(ChannelStrip? strip, Track? track) {
        strip ??= ProjectDefaults.Strip;
        var full = track is null || Edits.HasAudioStrip(track.Kind);
        var sends = (strip.Sends ?? []).Where(static s => s?.Id is not null && s.Bus is >= 1 and <= Edits.MaxBus)
            .DistinctBy(static s => s.Bus);
        return strip with {
            Inserts = full
                ? [
                    ..(strip.Inserts ?? []).Where(static i => i?.Id is not null && i.PluginId is not null)
                        .Select(i => Normalise(i, track is null))
                        .DistinctBy(static i => i.Id)
                ]
                : [],
            Sends = full && track is not null ? [..sends] : [],
            Output = Routing.Normalise(strip.Output) ?? Routing.Master,
            Groups = strip.Groups ?? [],
            InputFormat = Known(strip.InputFormat),
            OutputFormat = Known(strip.OutputFormat),
            Panner = strip.Panner is { Surround: not null, Object: not null } panner && Enum.IsDefined(panner.Mode)
                ? panner
                : Panner.Default,
            MidiEffects = track is not null && Edits.IsMidiKind(track.Kind)
                ? [
                    ..(strip.MidiEffects ?? []).Where(static i => i?.Id is not null && i.PluginId is not null)
                        .Select(static i => i with {
                                Name = i.Name ?? i.PluginId,
                                Parameters = PluginCatalog.Sorted(i.Parameters),
                                Sidechain = null
                            }
                        )
                        .DistinctBy(static i => i.Id)
                ]
                : []
        };
    }

    /// <summary>An audio region as read: its Phase 5 lists present, a follow mode there is.</summary>
    static AudioRegion Normalise(AudioRegion region) =>
        region with {
            Follow = Enum.IsDefined(region.Follow) ? region.Follow : FlexFollow.Off,
            FlexMarkers = [..(region.FlexMarkers ?? []).Where(static m => m is not null)],
            PitchNotes = [..(region.PitchNotes ?? []).Where(static n => n is not null)],
            PitchSource = region.PitchSource == PitchSourceMode.Root ? PitchSourceMode.Root : PitchSourceMode.Off,
            Key = Key(region.Key)
        };

    /// <summary>An insert as read: named, its parameters sorted, an analysis only on the master's Mastering Assistant.</summary>
    static Insert Normalise(Insert insert, bool master) =>
        insert with {
            Name = insert.Name ?? insert.PluginId,
            Parameters = PluginCatalog.Sorted(insert.Parameters),
            Mastering = master && Edits.IsMasteringAssistant(insert) ? Analysis(insert.Mastering) : null
        };

    /// <summary>A Mastering Assistant's analysis as read: one <c>setMasteringAnalysis</c> would take, else none.</summary>
    static MasteringAnalysis? Analysis(MasteringAnalysis? analysis) {
        if (analysis is null) {
            return null;
        }

        try {
            return Edits.CheckMasteringAnalysis(analysis);
        } catch (EditException) {
            return null;
        }
    }

    /// <summary>A region's key as read: a spelled tonic and a mode, else none (the project's).</summary>
    static RegionKey? Key(RegionKey? key) {
        if (key?.Tonic is null) {
            return null;
        }

        try {
            return Edits.CheckRegionKey(key);
        } catch (EditException) {
            return null;
        }
    }

    /// <summary>
    ///     A track's alternatives as read: well formed, their regions those of known files, the active one listed (a
    ///     track without any has no active one).
    /// </summary>
    static (IReadOnlyList<TrackAlternative> Alternatives, string? Active) Alternatives(
        Track track,
        ImmutableSortedDictionary<string, AudioFile> files
    ) {
        var entries = (track.Alternatives ?? [])
            .Where(static a => a?.Id is not null)
            .DistinctBy(static a => a.Id)
            .Select(a => a with {
                    Name = a.Name ?? a.Id,
                    Regions = [
                        ..(a.Regions ?? []).Where(r => r?.Id is not null && files.ContainsKey(r.FileId ?? string.Empty))
                            .Select(r => Normalise(r) with { TrackId = track.Id })
                    ],
                    MidiRegions = [
                        ..(a.MidiRegions ?? []).Where(static r => r?.Id is not null)
                            .Select(r => Normalise(r) with { TrackId = track.Id })
                    ],
                    TakeFolders = [
                        ..(a.TakeFolders ?? []).Where(f => f?.Id is not null
                            && f.Takes is { Count: > 0 }
                            && f.Takes.All(t => t is not null && files.ContainsKey(t.FileId ?? string.Empty))
                            )
                            .Select(f => f with { TrackId = track.Id, Comp = f.Comp ?? [] })
                    ],
                    PatternRegions = Edits.HoldsMidi(track)
                        ? [
                            ..(a.PatternRegions ?? []).Where(static r => r?.Id is not null
                                && r.Name is not null
                                && Valid(r.Pattern)
                                )
                                .Select(r => r with {
                                        TrackId = track.Id, Pattern = r.Pattern with { Rows = [..r.Pattern.Rows] }
                                    }
                                )
                        ]
                        : []
                }
            )
            .ToList();
        if (entries.Count == 0) {
            return ([], null);
        }

        if (track.AlternativeId is { } active && entries.Exists(a => a.Id == active)) {
            return (entries, active);
        }

        // The live content has lost its entry: it gets one back.
        var restored = new TrackAlternative(track.AlternativeId ?? "alt_restored", "Current", [], [], [], []);
        return ([restored, ..entries.Where(a => a.Id != restored.Id)], restored.Id);
    }

    static MidiRegion Normalise(MidiRegion region) =>
        region with {
            Playback = region.Playback ?? ProjectDefaults.Playback,
            Notation = region.Notation ?? ProjectDefaults.Notation,
            Notes = region.Notes ?? [],
            Controls = region.Controls ?? [],
            PitchSource = Enum.IsDefined(region.PitchSource) ? region.PitchSource : PitchSourceMode.Off,
            Key = Key(region.Key)
        };

    /// <summary>The track file of <paramref name="id" />, or null when it is missing, unreadable or not that track's.</summary>
    static TrackFile? ReadTrack(string folder, string id) {
        if (!IsSafeId(id)) {
            return null;
        }

        try {
            var file = Read(
                Path.Combine(folder, TracksFolder, TrackFileName(id)),
                PackageJsonContext.Default.TrackFile,
                PackageKeys.TrackKind
            );
            return file?.Track?.Id == id ? file : null;
        } catch (PackageException) {
            return null;
        }
    }

    public static string TrackFileName(string trackId) =>
        IsSafeId(trackId) ? trackId + ".json" : throw new ArgumentException($"Unsafe track id '{trackId}'.");

    /// <summary>Ids become file names: letters, digits, <c>_</c> and <c>-</c> only.</summary>
    public static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 64 && id.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, string kind)
        where T : class {
        try {
            JsonNode? node;
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete
                   )) {
                node = JsonNode.Parse(stream);
            }

            return node is JsonObject obj ? PackageMigrations.Upgrade(obj, kind).Deserialize(type) : null;
        } catch (Exception e) when (e is JsonException
                                        or IOException
                                        or UnauthorizedAccessException
                                        or InvalidOperationException
                                        or FormatException) {
            throw new PackageException($"Could not read '{path}': {e.Message}", e);
        }
    }
}

/// <summary>The two kinds of package file, and the JSON properties the migrations read in both.</summary>
static class PackageKeys {
    public const string ProjectKind = "project";
    public const string TrackKind = "track";
    public const string Settings = "settings";
    public const string Track = "track";
    public const string MidiRegions = "midiRegions";
    public const string PatternRegions = "patternRegions";
}

/// <summary>
///     Upgrades a package file's JSON to <see cref="CurrentSchemaVersion" /> before it is read, as Cathode's
///     <c>PresetMigrations</c>. A file without <c>schemaVersion</c> is schema 1.
/// </summary>
/// <remarks>
///     To change the format: raise <see cref="CurrentSchemaVersion" />, append to <see cref="Steps" /> the step that
///     takes the previous schema's JSON (of either kind of file, <c>project</c> or <c>track</c>) to the new one, and
///     add a package the new build wrote to <c>Fixtures/Schemas</c> (<c>MigrationFixtureTests</c> fails without one,
///     and each folder stays as it was written). Released steps are never edited. A file from a newer Anode is refused
///     rather than read wrongly.
/// </remarks>
public static class PackageMigrations {
    public const int CurrentSchemaVersion = 12;
    const string VersionField = "schemaVersion";

    /// <summary><c>Steps[n]</c> takes schema <c>n + 1</c> to <c>n + 2</c>; it gets the kind of file and its JSON.</summary>
    static readonly Func<string, JsonObject, JsonObject>[] Steps = [
        ToSchema2, ToSchema3, ToSchema4, ToSchema5, ToSchema6, ToSchema7, ToSchema8, ToSchema9, ToSchema10, ToSchema11,
        ToSchema12
    ];

    public static JsonObject Upgrade(JsonObject json, string kind) {
        var version = json[VersionField] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 1;
        if (version > CurrentSchemaVersion) {
            throw new PackageException(
                $"This {kind} file is schema {version}, from a newer Anode; this one reads up to {CurrentSchemaVersion}."
            );
        }

        for (; version < CurrentSchemaVersion; version++) {
            json = Steps[version - 1](kind, json);
        }

        json[VersionField] = CurrentSchemaVersion;
        return json;
    }

    /// <summary>
    ///     Schema 1 (Phase 1) to 2 (Phase 2, MIDI and instruments): every track was an audio track taking no MIDI, a
    ///     track file has no MIDI regions, and MIDI recording made new regions.
    /// </summary>
    static JsonObject ToSchema2(string kind, JsonObject json) {
        if (kind == PackageKeys.ProjectKind) {
            if (json[PackageKeys.Settings] is JsonObject settings) {
                settings["mergeMidiRecording"] ??= false;
            }
        } else if (kind == PackageKeys.TrackKind) {
            if (json[PackageKeys.Track] is JsonObject track) {
                track["kind"] ??= "audio";
                track["midiInput"] ??= new JsonObject { ["channel"] = 0 };
            }

            json[PackageKeys.MidiRegions] ??= new JsonArray();
        }

        return json;
    }

    /// <summary>
    ///     Schema 2 (Phase 2) to 3 (Phase 3, arranging and takes): the tempo, signature and key tracks are the
    ///     project-start values alone; recording makes take folders, without replace or autopunch, with the punch
    ///     locators over the first four bars; no markers, sections, notes or screensets; tracks are outside any stack,
    ///     expanded, without notes; regions are unmuted with linear fades; tracks have no take folders.
    /// </summary>
    static JsonObject ToSchema3(string kind, JsonObject json) {
        if (kind == PackageKeys.ProjectKind) {
            ProjectToSchema3(json);
        } else if (kind == PackageKeys.TrackKind) {
            TrackToSchema3(json);
        }

        return json;
    }

    static void ProjectToSchema3(JsonObject json) {
        if (json[PackageKeys.Settings] is JsonObject settings) {
            var tempo = settings["tempo"]?.DeepClone() ?? JsonValue.Create(ProjectDefaults.Tempo);
            var signature = settings["signature"] as JsonObject;
            var numerator = signature?["numerator"]?.GetValue<int>() ?? 4;
            var denominator = signature?["denominator"]?.GetValue<int>() ?? 4;
            settings["tempoEvents"] ??=
                new JsonArray(new JsonObject { ["tick"] = 0, ["bpm"] = tempo, ["curve"] = "step" });
            settings["signatures"] ??= new JsonArray(
                new JsonObject { ["tick"] = 0, ["numerator"] = numerator, ["denominator"] = denominator }
            );
            settings["keys"] ??= new JsonArray(new JsonObject { ["tick"] = 0, ["tonic"] = 0, ["mode"] = "major" });
            settings["recording"] ??= new JsonObject {
                ["cycleMode"] = "takeFolder",
                ["replace"] = false,
                ["autopunch"] = false,
                ["punchInTick"] = 0,
                ["punchOutTick"] = 4L * TempoMap.Ppq * 4 / denominator * numerator
            };
        }

        json["markers"] ??= new JsonArray();
        json["arrangement"] ??= new JsonArray();
        json["notes"] ??= string.Empty;
        json["screensets"] ??= new JsonArray();
    }

    static void TrackToSchema3(JsonObject json) {
        if (json[PackageKeys.Track] is JsonObject track) {
            track["collapsed"] ??= false;
            track["notes"] ??= string.Empty;
        }

        foreach (var region in (json["regions"] as JsonArray ?? []).OfType<JsonObject>()) {
            region["fadeInCurve"] ??= 0;
            region["fadeOutCurve"] ??= 0;
            region["muted"] ??= false;
        }

        foreach (var region in (json[PackageKeys.MidiRegions] as JsonArray ?? []).OfType<JsonObject>()) {
            region["muted"] ??= false;
        }

        json["takeFolders"] ??= new JsonArray();
    }

    /// <summary>
    ///     Schema 3 (Phase 3) to 4 (Phase 4, mixing and automation): every strip has no inserts or sends and goes to the
    ///     master, outside any group or VCA; delay compensation is on for everything, low-latency mode off at 10 ms;
    ///     there are no groups and no automation; tracks read automation, take no bus and are not frozen.
    /// </summary>
    static JsonObject ToSchema4(string kind, JsonObject json) {
        if (kind == PackageKeys.ProjectKind) {
            if (json[PackageKeys.Settings] is JsonObject settings) {
                settings["mixer"] ??= new JsonObject {
                    ["pdc"] = "all", ["lowLatency"] = false, ["lowLatencyLimitMs"] = 10
                };
            }

            Schema4.Strip(json["master"] as JsonObject);
            json["groups"] ??= new JsonArray();
            json["automation"] ??= new JsonArray();
        } else if (kind == PackageKeys.TrackKind) {
            if (json[PackageKeys.Track] is JsonObject track) {
                Schema4.Strip(track["strip"] as JsonObject);
                track["inputBus"] ??= 0;
                track["automationMode"] ??= "read";
            }

            json["automation"] ??= new JsonArray();
        }

        return json;
    }

    /// <summary>
    ///     Schema 4 (Phase 4) to 5 (Phase 5, advanced audio; docs/06-phase-5.md): Smart Tempo keeps the project tempo,
    ///     new regions do not follow it and imports are analysed; Varispeed is off at 100 %, speed only; tempo ramps
    ///     are linear; files have no tempo; tracks' flex is off (Polyphonic), without alternatives; regions do not
    ///     follow the tempo and have no markers, transpose, fine tune or pitch notes.
    /// </summary>
    static JsonObject ToSchema5(string kind, JsonObject json) {
        if (kind == PackageKeys.ProjectKind) {
            if (json[PackageKeys.Settings] is JsonObject settings) {
                settings["smartTempo"] ??= new JsonObject {
                    ["mode"] = "keep", ["defaultFollow"] = "off", ["analyzeImports"] = true
                };
                settings["varispeed"] ??= new JsonObject { ["enabled"] = false, ["mode"] = "speedOnly", ["speed"] = 1 };
                foreach (var tempo in (settings["tempoEvents"] as JsonArray ?? []).OfType<JsonObject>()) {
                    tempo["shape"] ??= 0;
                }
            }
        } else if (kind == PackageKeys.TrackKind) {
            if (json[PackageKeys.Track] is JsonObject track) {
                track["flexOn"] ??= false;
                track["flexMode"] ??= "polyphonic";
                track["alternatives"] ??= new JsonArray();
            }

            foreach (var region in (json["regions"] as JsonArray ?? []).OfType<JsonObject>()) {
                region["follow"] ??= "off";
                region["flexMarkers"] ??= new JsonArray();
                region["transpose"] ??= 0;
                region["fineTune"] ??= 0;
                region["pitchNotes"] ??= new JsonArray();
            }
        }

        return json;
    }

    /// <summary>
    ///     Schema 5 (Phase 5) to 6 (Phase 6, loops, patterns and performance; docs/07-phase-6.md): strips have no MIDI
    ///     FX; tracks have no drum machine, no Smart Controls and play their Tracks-area regions, and no pattern regions;
    ///     the Live Loops grid is empty, launching on the bar, hidden.
    /// </summary>
    static JsonObject ToSchema6(string kind, JsonObject json) {
        if (kind == PackageKeys.ProjectKind) {
            Schema6.Strip(json["master"] as JsonObject);
            json["liveLoops"] ??= new JsonObject {
                ["scenes"] = new JsonArray(), ["cells"] = new JsonArray(), ["quantize"] = "bar", ["shown"] = false
            };
        } else if (kind == PackageKeys.TrackKind) {
            if (json[PackageKeys.Track] is JsonObject track) {
                Schema6.Strip(track["strip"] as JsonObject);
                track["liveLoopsActive"] ??= false;
            }

            json["patternRegions"] ??= new JsonArray();
        }

        return json;
    }

    /// <summary>
    ///     Schema 6 (Phase 6) to 7 (Phase 7, advanced MIDI editing; docs/08-phase-7.md): no groove templates,
    ///     articulation sets or lane sets, and no groove track; files are as imported (version 0); tracks do not match
    ///     the groove track and have no articulation set; notes have no articulation (in the Tracks area, in track
    ///     alternatives and in Live Loops cells); regions quantise to the grid.
    /// </summary>
    static JsonObject ToSchema7(string kind, JsonObject json) {
        if (kind == PackageKeys.ProjectKind) {
            Schema7.Project(json);
        } else if (kind == PackageKeys.TrackKind) {
            Schema7.Track(json);
        }

        return json;
    }

    /// <summary>Schema 7 to 8 (Phase 8, docs/09-phase-8.md): a project's sync settings are the defaults.</summary>
    static JsonObject ToSchema8(string kind, JsonObject json) {
        if (kind == PackageKeys.ProjectKind) {
            Schema8.Project(json);
        }

        return json;
    }

    /// <summary>
    ///     Schema 8 to 9 (Phase 9, notation and interchange; docs/10-phase-9.md): every MIDI region (on tracks, in track
    ///     alternatives and in Live Loops cells) has the default notation settings, tracks have no staff style (left
    ///     out), and the project's score content is empty with the default layout.
    /// </summary>
    static JsonObject ToSchema9(string kind, JsonObject json) {
        Schema9.Fill(kind, json);
        return json;
    }

    /// <summary>
    ///     Schema 9 to 10 (Phase 10, surround and video; docs/11-phase-10.md): the project is stereo, every strip is
    ///     stereo in and out with the default panner, every marker is a user's marker, and there is no movie (left out).
    /// </summary>
    static JsonObject ToSchema10(string kind, JsonObject json) {
        Schema10.Fill(kind, json);
        return json;
    }

    /// <summary>
    ///     Schema 10 to 11 (Phase 11, generative and analysis; docs/12-phase-11.md): the chord track and the Environment
    ///     are empty (the Environment's default path), every region follows nothing and has no key of its own, and no
    ///     track has Session Player regions.
    /// </summary>
    static JsonObject ToSchema11(string kind, JsonObject json) {
        Schema11.Fill(kind, json);
        return json;
    }

    /// <summary>
    ///     Schema 11 to 12: alternatives hold pattern regions. An older alternative has none: while it was put away, the
    ///     track's pattern regions stayed live.
    /// </summary>
    static JsonObject ToSchema12(string kind, JsonObject json) {
        if (kind == PackageKeys.TrackKind) {
            foreach (var alternative in (json[PackageKeys.Track]?["alternatives"] as JsonArray ?? [])
                     .OfType<JsonObject>()) {
                alternative[PackageKeys.PatternRegions] ??= new JsonArray();
            }
        }

        return json;
    }
}

/// <summary>Source-generated serialisation of the package files (camelCase, indented, nulls omitted).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true
)]
[JsonSerializable(typeof(ProjectFile))]
[JsonSerializable(typeof(TrackFile))]
[JsonSerializable(typeof(List<ProjectSummary>))]
[JsonSerializable(typeof(AlternativesFile))]
[JsonSerializable(typeof(AutosaveManifest))]
[JsonSerializable(typeof(TemplateFile))]
public sealed partial class PackageJsonContext : JsonSerializerContext;

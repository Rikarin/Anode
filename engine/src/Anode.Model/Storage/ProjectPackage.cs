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
///     <c>settings</c>), markers, arrangement sections, the project's notes and the editor's screensets.
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
    IReadOnlyList<Screenset>? Screensets = null
);

/// <summary>
///     <c>tracks/&lt;id&gt;.json</c>: one track and its regions, audio and MIDI (schema 2), and its take folders
///     (schema 3).
/// </summary>
public sealed record TrackFile(
    int SchemaVersion,
    Track Track,
    IReadOnlyList<AudioRegion> Regions,
    IReadOnlyList<MidiRegion>? MidiRegions = null,
    IReadOnlyList<TakeFolder>? TakeFolders = null
);

/// <summary>A package that cannot be read: missing, damaged, or from a newer Anode.</summary>
public sealed class PackageException : Exception {
    public PackageException() { }

    public PackageException(string message) : base(message) { }

    public PackageException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
///     Reads and writes a project package: a folder with <c>project.json</c>, <c>tracks/&lt;id&gt;.json</c>,
///     <c>Audio Files/</c>, <c>Peaks/</c> (docs/02-phase-1.md) and <c>Plugin States/&lt;track id&gt;.bin</c>, each
///     instrument's opaque state (docs/03-phase-2.md, "State"). Every file is written atomically
///     (<see cref="AtomicRename" />): the track files first, then <c>project.json</c>, which names the tracks, so a save
///     cut short leaves the previous project readable; track files no longer named are removed last.
/// </summary>
public static class ProjectPackage {
    public const string ProjectFileName = "project.json";
    public const string TracksFolder = "tracks";
    public const string AudioFolder = "Audio Files";
    public const string PeaksFolder = "Peaks";
    public const string BackupsFolder = "Backups";
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
    public static void Save(string folder, ProjectState state, bool media = true) {
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
                [..state.TakeFoldersOf(track.Id)]
            );
            AtomicRename.Write(
                Path.Combine(tracksFolder, TrackFileName(track.Id)),
                JsonSerializer.Serialize(file, PackageJsonContext.Default.TrackFile)
            );
        }

        var root = new ProjectFile(
            PackageMigrations.CurrentSchemaVersion,
            state.Id,
            state.Name,
            state.SampleRate,
            state.Settings,
            state.Master,
            [..state.Tracks.Select(static t => t.Id)],
            [..state.Files.Values],
            [..state.Markers.Values.OrderBy(static m => m.Tick).ThenBy(static m => m.Id, StringComparer.Ordinal)],
            [..state.Sections.Values.OrderBy(static s => s.StartTick)],
            state.Notes,
            [..state.Screensets.Values]
        );
        AtomicRename.Write(
            Path.Combine(folder, ProjectFileName),
            JsonSerializer.Serialize(root, PackageJsonContext.Default.ProjectFile)
        );

        var kept = state.Tracks.Select(static t => TrackFileName(t.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in Directory.EnumerateFiles(tracksFolder, "*.json")) {
            if (!kept.Contains(Path.GetFileName(stale))) {
                File.Delete(stale);
            }
        }
    }

    /// <summary>
    ///     Reads a package. A track file that is missing or unreadable, and a region whose file or track is unknown,
    ///     are left out rather than failing the whole project; a damaged or newer <c>project.json</c> throws
    ///     <see cref="PackageException" />.
    /// </summary>
    public static ProjectState Load(string folder) {
        var root = Read(Path.Combine(folder, ProjectFileName), PackageJsonContext.Default.ProjectFile, "project");
        // Deserialisation leaves a property the JSON lacks null, whatever its declared type.
        if (root?.Id is null
            || root.Name is null
            || root.Settings is null
            || root.Master is null
            || !ProjectDefaults.SampleRates.Contains(root.SampleRate)) {
            throw new PackageException($"'{folder}' has no readable {ProjectFileName}.");
        }

        var files = ImmutableSortedDictionary.CreateRange(
            StringComparer.Ordinal,
            (root.AudioFiles ?? []).Select(static f => KeyValuePair.Create(f.Id, f))
        );
        var tracks = ImmutableList.CreateBuilder<Track>();
        var regions = ImmutableSortedDictionary.CreateBuilder<string, AudioRegion>(StringComparer.Ordinal);
        var midiRegions = ImmutableSortedDictionary.CreateBuilder<string, MidiRegion>(StringComparer.Ordinal);
        var folders = ImmutableSortedDictionary.CreateBuilder<string, TakeFolder>(StringComparer.Ordinal);
        var trackFiles = new List<TrackFile>();
        foreach (var id in (root.Tracks ?? []).Distinct(StringComparer.Ordinal)) {
            if (ReadTrack(folder, id) is { } file) {
                var track = Normalise(file.Track);
                tracks.Add(track);
                trackFiles.Add(file with { Track = track });
            }
        }

        Unnest(tracks);
        foreach (var file in trackFiles) {
            AddRegions(file, file.Track, files, regions, midiRegions, folders);
        }

        return new(
            root.Id,
            root.Name,
            root.SampleRate,
            Normalise(root.Settings),
            root.Master,
            tracks.ToImmutable(),
            regions.ToImmutable(),
            files,
            midiRegions.ToImmutable()
        ) {
            LastEventId = midiRegions.Values.Select(ProjectState.MaxEventId).DefaultIfEmpty().Max(),
            TakeFolders = folders.ToImmutable(),
            Markers = Keyed(
                root.Markers?.Select(static m => m is { Text: null } ? m with { Text = string.Empty } : m).ToArray(),
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
            )
        };
    }

    /// <summary>Stacks do not nest, and a member's stack is one of the project's: other parents are dropped.</summary>
    static void Unnest(ImmutableList<Track>.Builder tracks) {
        var stacks = tracks.Where(static t => t.Kind == TrackKind.Stack).Select(static t => t.Id).ToHashSet();
        for (var i = 0; i < tracks.Count; i++) {
            var t = tracks[i];
            if (t.ParentId is not null && (t.Kind == TrackKind.Stack || !stacks.Contains(t.ParentId))) {
                tracks[i] = t with { ParentId = null };
            }
        }
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
    static ProjectSettings Normalise(ProjectSettings settings) {
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
            Recording = settings.Recording ?? ProjectDefaults.Recording(signature)
        };
    }

    /// <summary>
    ///     A track file's regions that belong to it: audio ones of known files on an audio track, MIDI ones on a MIDI
    ///     track.
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
            if (region?.TrackId == track.Id && track.Kind == TrackKind.Audio && files.ContainsKey(region.FileId)) {
                regions[region.Id] = region;
            }
        }

        foreach (var region in file.MidiRegions ?? []) {
            if (region?.TrackId == track.Id && Edits.IsMidiKind(track.Kind) && region.Id is not null) {
                midiRegions[region.Id] = Normalise(region);
            }
        }

        foreach (var folder in file.TakeFolders ?? []) {
            if (folder?.TrackId == track.Id
                && track.Kind == TrackKind.Audio
                && folder.Id is not null
                && folder.Takes is { Count: > 0 } takes
                && takes.All(t => t is not null && files.ContainsKey(t.FileId ?? string.Empty))) {
                var ids = takes.Select(static t => t.Id).ToHashSet();
                folders[folder.Id] = folder with {
                    Name = folder.Name ?? track.Name,
                    Comp = [..(folder.Comp ?? []).Where(s => s is not null && ids.Contains(s.TakeId))]
                };
            }
        }
    }

    /// <summary>
    ///     Writes the instruments' opaque states, <c>Plugin States/&lt;track id&gt;.bin</c> each, and removes the state
    ///     files of tracks not in <paramref name="states" />.
    /// </summary>
    public static void SaveStates(string folder, IReadOnlyDictionary<string, byte[]> states) {
        var statesFolder = Path.Combine(folder, PluginStatesFolder);
        if (states.Count > 0) {
            Directory.CreateDirectory(statesFolder);
        }

        foreach (var (trackId, state) in states) {
            AtomicRename.Write(Path.Combine(statesFolder, StateFileName(trackId)), state);
        }

        if (Directory.Exists(statesFolder)) {
            foreach (var stale in Directory.EnumerateFiles(statesFolder, "*" + StateExtension)) {
                if (!states.ContainsKey(Path.GetFileNameWithoutExtension(stale))) {
                    File.Delete(stale);
                }
            }
        }
    }

    /// <summary>The saved states of <paramref name="state" />'s instrument tracks, by track id (unreadable ones left out).</summary>
    public static Dictionary<string, byte[]> LoadStates(string folder, ProjectState state) {
        var states = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var track in state.Tracks) {
            if (track.Instrument is null || !IsSafeId(track.Id)) {
                continue;
            }

            var path = Path.Combine(folder, PluginStatesFolder, StateFileName(track.Id));
            try {
                if (File.Exists(path)) {
                    states[track.Id] = File.ReadAllBytes(path);
                }
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                // An unreadable state: the instrument starts from its parameters.
            }
        }

        return states;
    }

    public static string StateFileName(string trackId) =>
        IsSafeId(trackId) ? trackId + StateExtension : throw new ArgumentException($"Unsafe track id '{trackId}'.");

    /// <summary>A track as read, in the model's canonical form (parameters in ordinal order).</summary>
    static Track Normalise(Track track) =>
        track with {
            Instrument = track.Instrument is { } slot
                ? slot with { Parameters = PluginCatalog.Sorted(slot.Parameters) }
                : null,
            MidiInput = track.MidiInput ?? ProjectDefaults.MidiInput,
            MidiOutput = track.Kind == TrackKind.Midi ? track.MidiOutput : null,
            Notes = track.Notes ?? string.Empty
        };

    static MidiRegion Normalise(MidiRegion region) =>
        region with {
            Playback = region.Playback ?? ProjectDefaults.Playback,
            Notes = region.Notes ?? [],
            Controls = region.Controls ?? []
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
                "track"
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

/// <summary>
///     Upgrades a package file's JSON to <see cref="CurrentSchemaVersion" /> before it is read, as Cathode's
///     <c>PresetMigrations</c>. A file without <c>schemaVersion</c> is schema 1.
/// </summary>
/// <remarks>
///     To change the format: raise <see cref="CurrentSchemaVersion" />, append to <see cref="Steps" /> the step that
///     takes the previous schema's JSON (of either kind of file, <c>project</c> or <c>track</c>) to the new one, and
///     add a test with a file of the previous schema. Released steps are never edited. A file from a newer Anode is
///     refused rather than read wrongly.
/// </remarks>
public static class PackageMigrations {
    public const int CurrentSchemaVersion = 3;
    const string VersionField = "schemaVersion";

    /// <summary><c>Steps[n]</c> takes schema <c>n + 1</c> to <c>n + 2</c>; it gets the kind of file and its JSON.</summary>
    static readonly Func<string, JsonObject, JsonObject>[] Steps = [ToSchema2, ToSchema3];

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
        if (kind == "project") {
            if (json["settings"] is JsonObject settings) {
                settings["mergeMidiRecording"] ??= false;
            }
        } else if (kind == "track") {
            if (json["track"] is JsonObject track) {
                track["kind"] ??= "audio";
                track["midiInput"] ??= new JsonObject { ["channel"] = 0 };
            }

            json["midiRegions"] ??= new JsonArray();
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
        if (kind == "project") {
            ProjectToSchema3(json);
        } else if (kind == "track") {
            TrackToSchema3(json);
        }

        return json;
    }

    static void ProjectToSchema3(JsonObject json) {
        if (json["settings"] is JsonObject settings) {
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
        if (json["track"] is JsonObject track) {
            track["collapsed"] ??= false;
            track["notes"] ??= string.Empty;
        }

        foreach (var region in (json["regions"] as JsonArray ?? []).OfType<JsonObject>()) {
            region["fadeInCurve"] ??= 0;
            region["fadeOutCurve"] ??= 0;
            region["muted"] ??= false;
        }

        foreach (var region in (json["midiRegions"] as JsonArray ?? []).OfType<JsonObject>()) {
            region["muted"] ??= false;
        }

        json["takeFolders"] ??= new JsonArray();
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
public sealed partial class PackageJsonContext : JsonSerializerContext;

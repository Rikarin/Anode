using Anode.Contracts;
using Anode.Model.Time;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Anode.Model;

/// <summary>
///     An immutable snapshot of a project's content (docs/adr/0004-state-ownership.md): what an edit changes, what undo
///     restores and what the package stores. The revision, the dirty flag and the folder belong to the open session,
///     not to the snapshot. Regions and files are keyed (and listed) by id; tracks keep their order.
///     <see cref="LastEventId" /> is the highest note or controller id the project has used (docs/03-phase-2.md,
///     "Ids"): it only grows, so an id freed by an undo is never handed out again while the project is open.
/// </summary>
public sealed record ProjectState(
    string Id,
    string Name,
    int SampleRate,
    ProjectSettings Settings,
    ChannelStrip Master,
    ImmutableList<Track> Tracks,
    ImmutableSortedDictionary<string, AudioRegion> Regions,
    ImmutableSortedDictionary<string, AudioFile> Files,
    ImmutableSortedDictionary<string, MidiRegion> MidiRegions
) {
    static readonly ConditionalWeakTable<IReadOnlyList<TempoEvent>, TempoMap> TempoMaps = new();
    static readonly ConditionalWeakTable<IReadOnlyList<SignatureEvent>, SignatureMap> SignatureMaps = new();

    public long LastEventId { get; init; }

    /// <summary>The take folders, by id (docs/04-phase-3.md, "Take folders").</summary>
    public ImmutableSortedDictionary<string, TakeFolder> TakeFolders { get; init; } =
        ImmutableSortedDictionary.Create<string, TakeFolder>(StringComparer.Ordinal);

    public ImmutableSortedDictionary<string, Marker> Markers { get; init; } =
        ImmutableSortedDictionary.Create<string, Marker>(StringComparer.Ordinal);

    /// <summary>The arrangement track's sections, by id; they never overlap.</summary>
    public ImmutableSortedDictionary<string, ArrangementMarker> Sections { get; init; } =
        ImmutableSortedDictionary.Create<string, ArrangementMarker>(StringComparer.Ordinal);

    /// <summary>The project's Note Pad.</summary>
    public string Notes { get; init; } = string.Empty;

    /// <summary>
    ///     The editor's screensets by number (1…9): saved with the project, but not part of its content as edits and
    ///     undo see it, nor of the contract's <see cref="Project" />.
    /// </summary>
    public ImmutableSortedDictionary<int, Screenset> Screensets { get; init; } =
        ImmutableSortedDictionary<int, Screenset>.Empty;

    /// <summary>The project's tempo map: its tempo track (built once per track, as the lists are immutable).</summary>
    public TempoMap TempoMap =>
        Settings.TempoEvents is { Count: > 0 } events
            ? TempoMaps.GetValue(events, static e => TempoMap.Of(e))
            : TempoMap.Constant(Settings.Tempo);

    /// <summary>The project's signature track as bars and beats.</summary>
    public SignatureMap SignatureMap =>
        Settings.Signatures is { Count: > 0 } events
            ? SignatureMaps.GetValue(events, static e => new(e))
            : SignatureMap.Constant(Settings.Signature.Numerator, Settings.Signature.Denominator);

    /// <summary>The position of the track <paramref name="id" /> in the track list, or −1.</summary>
    public int IndexOfTrack(string id) {
        for (var i = 0; i < Tracks.Count; i++) {
            if (Tracks[i].Id == id) {
                return i;
            }
        }

        return -1;
    }

    public Track? FindTrack(string id) => IndexOfTrack(id) is >= 0 and var i ? Tracks[i] : null;

    /// <summary>The MIDI regions of a track, by start.</summary>
    public IEnumerable<MidiRegion> MidiRegionsOf(string trackId) =>
        MidiRegions.Values.Where(r => r.TrackId == trackId)
            .OrderBy(static r => r.StartTick)
            .ThenBy(
                static r => r.Id,
                StringComparer.Ordinal
            );

    /// <summary>The highest note or controller id of <paramref name="region" /> (0 when it has none).</summary>
    public static long MaxEventId(MidiRegion region) {
        long max = 0;
        foreach (var note in region.Notes) {
            max = Math.Max(max, note.Id);
        }

        foreach (var control in region.Controls) {
            max = Math.Max(max, control.Id);
        }

        return max;
    }

    /// <summary>The take folders of a track, by start.</summary>
    public IEnumerable<TakeFolder> TakeFoldersOf(string trackId) =>
        TakeFolders.Values.Where(f => f.TrackId == trackId)
            .OrderBy(static f => f.StartTick)
            .ThenBy(static f => f.Id, StringComparer.Ordinal);

    /// <summary>The members of a folder stack, in track order.</summary>
    public IEnumerable<Track> MembersOf(string stackId) => Tracks.Where(t => t.ParentId == stackId);

    /// <summary>The regions of a track, by start.</summary>
    public IEnumerable<AudioRegion> RegionsOf(string trackId) =>
        Regions.Values.Where(r => r.TrackId == trackId)
            .OrderBy(static r => r.StartTick)
            .ThenBy(
                static r => r.Id,
                StringComparer.Ordinal
            );

    /// <summary>The contract form, as <c>GET /api/v1/project</c> answers it.</summary>
    public Project ToProject(string path, long revision, bool dirty) =>
        new(
            Id,
            Name,
            path,
            revision,
            dirty,
            SampleRate,
            Settings,
            Master,
            [..Tracks],
            [..Regions.Values],
            [..Files.Values],
            [..MidiRegions.Values],
            [..TakeFolders.Values],
            [..Markers.Values],
            [..Sections.Values],
            Notes,
            [], // Mixer groups and automation: Phase 4, workstream B (docs/05-phase-4.md).
            []
        );

    /// <summary>Applies one delta operation (an edit's primitive step, also what clients apply).</summary>
    public ProjectState Apply(DeltaOp op) =>
        op switch {
            TrackUpserted(var track, var index) => WithTrack(track, index),
            TrackRemoved(var id) => this with {
                Tracks = Tracks.RemoveAll(t => t.Id == id),
                Regions = Regions.RemoveRange(Regions.Values.Where(r => r.TrackId == id).Select(static r => r.Id)),
                MidiRegions = MidiRegions.RemoveRange(
                    MidiRegions.Values.Where(r => r.TrackId == id).Select(static r => r.Id)
                ),
                TakeFolders = TakeFolders.RemoveRange(
                    TakeFolders.Values.Where(f => f.TrackId == id).Select(static f => f.Id)
                )
            },
            RegionUpserted(var region) => this with { Regions = Regions.SetItem(region.Id, region) },
            RegionRemoved(var id) => this with { Regions = Regions.Remove(id) },
            AudioFileUpserted(var file) => this with { Files = Files.SetItem(file.Id, file) },
            SettingsChanged(var settings) => this with { Settings = settings },
            MasterChanged(var strip) => this with { Master = strip },
            ProjectRenamed(var name) => this with { Name = name },
            MidiRegionUpserted(var region) => this with {
                MidiRegions = MidiRegions.SetItem(region.Id, region),
                LastEventId = Math.Max(LastEventId, MaxEventId(region))
            },
            MidiRegionRemoved(var id) => this with { MidiRegions = MidiRegions.Remove(id) },
            TakeFolderUpserted(var folder) => this with { TakeFolders = TakeFolders.SetItem(folder.Id, folder) },
            TakeFolderRemoved(var id) => this with { TakeFolders = TakeFolders.Remove(id) },
            MarkerUpserted(var marker) => this with { Markers = Markers.SetItem(marker.Id, marker) },
            MarkerRemoved(var id) => this with { Markers = Markers.Remove(id) },
            SectionUpserted(var section) => this with { Sections = Sections.SetItem(section.Id, section) },
            SectionRemoved(var id) => this with { Sections = Sections.Remove(id) },
            NotesChanged(var text) => this with { Notes = text },
            _ => throw new ArgumentException($"Unknown delta operation {op.GetType().Name}.", nameof(op))
        };

    public ProjectState Apply(IEnumerable<DeltaOp> ops) {
        var state = this;
        foreach (var op in ops) {
            state = state.Apply(op);
        }

        return state;
    }

    /// <summary>
    ///     The operation that undoes <paramref name="op" /> applied to this state, or null when it has nothing to undo
    ///     (a file added to the pool stays in the pool).
    /// </summary>
    public DeltaOp? Inverse(DeltaOp op) =>
        op switch {
            TrackUpserted(var track, _) => IndexOfTrack(track.Id) is >= 0 and var i
                ? new TrackUpserted(Tracks[i], i)
                : new TrackRemoved(track.Id),
            TrackRemoved(var id) => IndexOfTrack(id) is >= 0 and var i ? new TrackUpserted(Tracks[i], i) : null,
            RegionUpserted(var region) => Regions.TryGetValue(region.Id, out var old)
                ? new RegionUpserted(old)
                : new RegionRemoved(region.Id),
            RegionRemoved(var id) => Regions.TryGetValue(id, out var old) ? new RegionUpserted(old) : null,
            AudioFileUpserted(var file) => Files.TryGetValue(file.Id, out var old) ? new AudioFileUpserted(old) : null,
            SettingsChanged => new SettingsChanged(Settings),
            MasterChanged => new MasterChanged(Master),
            ProjectRenamed => new ProjectRenamed(Name),
            MidiRegionUpserted or MidiRegionRemoved or TakeFolderUpserted or TakeFolderRemoved => InverseMidi(op),
            MarkerUpserted or MarkerRemoved or SectionUpserted or SectionRemoved => InverseArrangement(op),
            NotesChanged => new NotesChanged(Notes),
            _ => throw new ArgumentException($"Unknown delta operation {op.GetType().Name}.", nameof(op))
        };

    DeltaOp? InverseMidi(DeltaOp op) =>
        op switch {
            MidiRegionUpserted(var region) => MidiRegions.TryGetValue(region.Id, out var old)
                ? new MidiRegionUpserted(old)
                : new MidiRegionRemoved(region.Id),
            MidiRegionRemoved(var id) => MidiRegions.TryGetValue(id, out var old) ? new MidiRegionUpserted(old) : null,
            TakeFolderUpserted(var folder) => TakeFolders.TryGetValue(folder.Id, out var old)
                ? new TakeFolderUpserted(old)
                : new TakeFolderRemoved(folder.Id),
            TakeFolderRemoved(var id) => TakeFolders.TryGetValue(id, out var old) ? new TakeFolderUpserted(old) : null,
            _ => null
        };

    DeltaOp? InverseArrangement(DeltaOp op) =>
        op switch {
            MarkerUpserted(var marker) => Markers.TryGetValue(marker.Id, out var old)
                ? new MarkerUpserted(old)
                : new MarkerRemoved(marker.Id),
            MarkerRemoved(var id) => Markers.TryGetValue(id, out var old) ? new MarkerUpserted(old) : null,
            SectionUpserted(var section) => Sections.TryGetValue(section.Id, out var old)
                ? new SectionUpserted(old)
                : new SectionRemoved(section.Id),
            SectionRemoved(var id) => Sections.TryGetValue(id, out var old) ? new SectionUpserted(old) : null,
            _ => null
        };

    ProjectState WithTrack(Track track, int index) {
        var tracks = Tracks.RemoveAll(t => t.Id == track.Id);
        return this with { Tracks = tracks.Insert(Math.Clamp(index, 0, tracks.Count), track) };
    }
}

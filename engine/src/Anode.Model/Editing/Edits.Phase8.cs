using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     Phase 8 (docs/09-phase-8.md): the project's synchronisation settings (<c>setSyncSettings</c>) and the Stem
///     Splitter's result (<c>addStems</c>, applied by the <c>stemSplit</c> job).
/// </summary>
public static partial class Edits {
    /// <summary>The most MIDI sync destinations a project sends to.</summary>
    public const int MaxSyncDestinations = 32;

    /// <summary>A sync destination's delay is −50…50 ms.</summary>
    public const double MaxSyncDelayMs = 50;

    /// <summary>The timecode of the project start is 0…86399 s (under 24 hours).</summary>
    public const double MaxStartOffsetSeconds = 86_399;

    /// <summary>The longest MIDI port id a setting keeps.</summary>
    public const int MaxPortIdLength = 256;

    /// <summary>The Phase 8 commands; null for every other command.</summary>
    static IReadOnlyList<DeltaOp>? Phase8Ops(
        ProjectState state,
        EditCommand command,
        IIdSource ids,
        PluginCatalog plugins
    ) =>
        command switch {
            SetSyncSettingsCommand c => SetSyncSettings(state, c),
            AddStemsCommand c => AddStems(state, c, ids, plugins),
            _ => null
        };

    static DeltaOp[] SetSyncSettings(ProjectState state, SetSyncSettingsCommand c) {
        var old = state.Settings.Sync;
        CheckSync(c);
        var sync = new SyncSettings(
            c.Source ?? old.Source,
            c.Destinations is { } destinations ? SyncDestinations(destinations) : old.Destinations,
            c.FrameRate ?? old.FrameRate,
            c.StartOffsetSeconds ?? old.StartOffsetSeconds,
            c.LinkStartStop ?? old.LinkStartStop,
            c.ClockInPort is { } clock ? InPort(clock, "clock") : old.ClockInPort,
            c.MtcInPort is { } mtc ? InPort(mtc, "MTC") : old.MtcInPort
        );
        return [new SettingsChanged(state.Settings with { Sync = sync })];
    }

    /// <summary>A sync change's source, frame rate and offset, checked.</summary>
    static void CheckSync(SetSyncSettingsCommand c) {
        if (c.Source is { } source && !Enum.IsDefined(source)) {
            throw new EditException($"Unknown sync source {source}.");
        }

        if (c.FrameRate is { } rate && !Enum.IsDefined(rate)) {
            throw new EditException($"Unknown MTC frame rate {rate}.");
        }

        if (c.StartOffsetSeconds is { } offset && !(offset is >= 0 and <= MaxStartOffsetSeconds)) {
            throw new EditException($"The start offset is 0…{MaxStartOffsetSeconds} s, not {offset}.");
        }
    }

    /// <summary>Sync destinations checked: at most 32, each port once, delays −50…50 ms.</summary>
    static MidiSyncDestination[] SyncDestinations(IReadOnlyList<MidiSyncDestination> destinations) {
        if (destinations.Count > MaxSyncDestinations) {
            throw new EditException($"Send sync to at most {MaxSyncDestinations} ports.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var destination in destinations) {
            if (destination is null || !IsPortId(destination.Port)) {
                throw new EditException($"A sync destination names a MIDI port (1…{MaxPortIdLength} characters).");
            }

            if (!seen.Add(destination.Port)) {
                throw new EditException($"Port '{destination.Port}' is a sync destination twice.");
            }

            if (!(destination.DelayMs is >= -MaxSyncDelayMs and <= MaxSyncDelayMs)) {
                throw new EditException(
                    $"A sync destination's delay is −{MaxSyncDelayMs}…{MaxSyncDelayMs} ms, not {destination.DelayMs}."
                );
            }
        }

        return [..destinations];
    }

    /// <summary>An input port setting: empty is any input (null).</summary>
    static string? InPort(string port, string what) {
        if (port.Length == 0) {
            return null;
        }

        return IsPortId(port)
            ? port
            : throw new EditException($"The {what} input is a MIDI port id of 1…{MaxPortIdLength} characters.");
    }

    static bool IsPortId(string? port) =>
        port is { Length: > 0 and <= MaxPortIdLength } && !string.IsNullOrWhiteSpace(port);

    /// <summary>
    ///     The Stem Splitter's result: a track per stem below the region's track (in the order given, with the source
    ///     track's output; stereo for a stereo file), each with a region of its whole file at the source region's
    ///     position, and the source region muted unless kept.
    /// </summary>
    static List<DeltaOp> AddStems(ProjectState state, AddStemsCommand c, IIdSource ids, PluginCatalog plugins) {
        var source = RegionOf(state, c.RegionId ?? string.Empty);
        var track = AudioTrackOf(state, source.TrackId);
        if (c.Stems is not { Count: > 0 } stems || stems.Count > Enum.GetValues<StemKind>().Length) {
            throw new EditException($"Add 1…{Enum.GetValues<StemKind>().Length} stems.");
        }

        if (state.Tracks.Count + stems.Count > MaxTracks) {
            throw new EditException($"A project holds at most {MaxTracks} tracks.");
        }

        var kinds = new HashSet<StemKind>();
        foreach (var stem in stems) {
            if (stem is null || !Enum.IsDefined(stem.Kind) || !kinds.Add(stem.Kind)) {
                throw new EditException("Each stem is a known kind, once.");
            }

            _ = FileOf(state, stem.FileId ?? string.Empty);
        }

        var ops = new List<DeltaOp>();
        var index = state.Tracks.FindIndex(t => t.Id == track.Id) + 1;
        foreach (var stem in stems) {
            var file = state.Files[stem.FileId];
            var added = AddTrack(
                state,
                new(StemTrackName(source.Name, stem.Kind), index, file.Channels == 2, TrackKind.Audio),
                ids,
                plugins
            );
            var upserted = (TrackUpserted)added.Single(static o => o is TrackUpserted);
            var stemTrack = upserted.Track with { Strip = upserted.Track.Strip with { Output = track.Strip.Output } };
            DeltaOp[] step = [
                ..added.Select(o => o == upserted ? upserted with { Track = stemTrack } : o)
            ];
            state = state.Apply(step);
            ops.AddRange(step);
            var region = AddRegion(state, new(stemTrack.Id, file.Id, source.StartTick), ids);
            state = state.Apply(region);
            ops.AddRange(region);
            index = state.Tracks.FindIndex(t => t.Id == stemTrack.Id) + 1;
        }

        if (!c.KeepSource && !source.Muted) {
            ops.Add(new RegionUpserted(state.Regions[source.Id] with { Muted = true }));
        }

        return ops;
    }

    /// <summary>A stem track's name: the region's and the stem's ("Mix – Vocals"), within the name length.</summary>
    public static string StemTrackName(string regionName, StemKind kind) {
        var stem = kind switch {
            StemKind.Vocals => "Vocals",
            StemKind.Drums => "Drums",
            StemKind.Bass => "Bass",
            StemKind.Other => "Other",
            StemKind.Guitar => "Guitar",
            _ => "Piano"
        };
        var suffix = " – " + stem;
        var name = regionName.Trim();
        if (name.Length == 0) {
            return stem;
        }

        return name.Length + suffix.Length <= MaxNameLength
            ? name + suffix
            : name[..(MaxNameLength - suffix.Length)].TrimEnd() + suffix;
    }
}

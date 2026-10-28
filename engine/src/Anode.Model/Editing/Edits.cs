using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     What applying edits produced: the new snapshot, the delta operations that lead there (in order) and the ones that
///     lead back (in order). Both lists are applied with <see cref="ProjectState.Apply(IEnumerable{DeltaOp})" />.
/// </summary>
public sealed record EditResult(ProjectState State, IReadOnlyList<DeltaOp> Forward, IReadOnlyList<DeltaOp> Inverse);

/// <summary>
///     Applies <see cref="EditCommand" />s to a <see cref="ProjectState" /> (docs/adr/0004-state-ownership.md). Each
///     command is validated and turned into delta operations; its inverse is derived from the state each operation
///     replaces, so undo restores exactly what was there. A command that does not apply throws
///     <see cref="EditException" /> and changes nothing.
/// </summary>
public static partial class Edits {
    public const int MaxTracks = 1024;
    public const int MaxNameLength = 128;
    public const int MaxBatch = 10_000;
    public const double MaxVolumeDb = 6;
    public const double MinRegionGainDb = -96;
    public const double MaxRegionGainDb = 24;
    public const double MinClickDb = -96;
    public const double MaxClickDb = 6;
    public const int MaxInputChannel = 256;

    /// <summary>The last tick a position may have: about 9 hours at 990 BPM, far from overflowing samples.</summary>
    public const long MaxTick = 1L << 40;

    /// <summary>
    ///     Applies a batch atomically: all of it, or (on <see cref="EditException" />) none of it.
    ///     <paramref name="plugins" /> is what instrument edits may name (none when left out).
    /// </summary>
    public static EditResult Apply(
        ProjectState state,
        IReadOnlyList<EditCommand> commands,
        IIdSource ids,
        PluginCatalog? plugins = null
    ) {
        plugins ??= PluginCatalog.Empty;
        if (commands.Count == 0) {
            throw new EditException("A batch needs at least one command.");
        }

        if (commands.Count > MaxBatch) {
            throw new EditException($"A batch holds at most {MaxBatch} commands.");
        }

        var forward = new List<DeltaOp>();
        var inverse = new List<DeltaOp>();
        for (var i = 0; i < commands.Count; i++) {
            IReadOnlyList<DeltaOp> ops;
            try {
                ops = Ops(state, commands[i] ?? throw new EditException("A command is null."), ids, plugins);
            } catch (EditException e) when (commands.Count > 1) {
                throw new EditException($"Command {i + 1} ({commands[i]?.GetType().Name}): {e.Message}", e);
            }

            state = Step(state, ops, forward, inverse);
        }

        inverse.Reverse();
        return new(state, forward, inverse);
    }

    public static EditResult Apply(
        ProjectState state,
        EditCommand command,
        IIdSource ids,
        PluginCatalog? plugins = null
    ) =>
        Apply(state, [command], ids, plugins);

    /// <summary>
    ///     Applies delta operations, collecting them and their inverses (in application order; the caller reverses the
    ///     inverses once at the end).
    /// </summary>
    public static ProjectState Step(
        ProjectState state,
        IEnumerable<DeltaOp> ops,
        List<DeltaOp> forward,
        List<DeltaOp> inverse
    ) {
        foreach (var op in ops) {
            if (state.Inverse(op) is { } undo) {
                inverse.Add(undo);
            }

            forward.Add(op);
            state = state.Apply(op);
        }

        return state;
    }

    /// <summary>The delta operations of one command against <paramref name="state" />.</summary>
    public static IReadOnlyList<DeltaOp> Ops(
        ProjectState state,
        EditCommand command,
        IIdSource ids,
        PluginCatalog plugins
    ) =>
        command switch {
            AddTrackCommand c => AddTrack(state, c, ids, plugins),
            RemoveTrackCommand c => RemoveTrack(state, c),
            MoveTrackCommand c => MoveTrack(state, c),
            SetTrackCommand c => SetTrack(state, c),
            SetStripCommand c => SetStrip(state, c),
            AddRegionCommand c => AddRegion(state, c, ids),
            MoveRegionCommand c => MoveRegion(state, c),
            ResizeRegionCommand c => ResizeRegion(state, c),
            SplitRegionCommand c => SplitRegion(state, c, ids),
            SetRegionCommand c => SetRegion(state, c),
            DeleteRegionsCommand c => DeleteRegions(state, c),
            SetTempoCommand c => SetTempo(state, c),
            SetSignatureCommand c => SetSignature(state, c),
            SetCycleCommand c => SetCycle(state, c),
            SetMetronomeCommand c => SetMetronome(state, c),
            RenameProjectCommand c => [new ProjectRenamed(Name(c.Name, "project name"))],
            SetInstrumentCommand c => SetInstrument(state, c, plugins),
            SetParameterCommand c => SetParameter(state, c, plugins),
            LoadInstrumentFileCommand c => LoadInstrumentFile(state, c, plugins),
            SetMidiIoCommand c => SetMidiIo(state, c),
            AddMidiRegionCommand c => AddMidiRegion(state, c, ids),
            ResizeMidiRegionCommand c => ResizeMidiRegion(state, c),
            SetMidiRegionCommand c => SetMidiRegion(state, c),
            AddNotesCommand c => AddNotes(state, c),
            EditNotesCommand c => EditNotes(state, c),
            DeleteNotesCommand c => DeleteNotes(state, c),
            QuantizeNotesCommand c => QuantizeNotes(state, c),
            AddControlsCommand c => AddControls(state, c),
            DeleteControlsCommand c => DeleteControls(state, c),
            SetMidiRecordingCommand c => [
                new SettingsChanged(state.Settings with { MergeMidiRecording = c.Merge })
            ],
            SetTempoEventsCommand c => SetTempoEvents(state, c),
            SetSignaturesCommand c => SetSignatures(state, c),
            SetKeysCommand c => SetKeys(state, c),
            AddMarkerCommand c => AddMarker(state, c, ids),
            SetMarkerCommand c => SetMarker(state, c),
            DeleteMarkersCommand c => DeleteMarkers(state, c),
            AddSectionCommand c => AddSection(state, c, ids),
            SetSectionCommand c => SetSection(state, c),
            DeleteSectionsCommand c => DeleteSections(state, c),
            MoveSectionCommand c => MoveSection(state, c, ids),
            SetCompCommand c => SetComp(state, c),
            SelectTakeCommand c => SelectTake(state, c),
            SetTakeFolderCommand c => SetTakeFolder(state, c),
            DeleteTakeCommand c => DeleteTake(state, c),
            FlattenTakeFolderCommand c => FlattenTakeFolder(state, c, ids),
            PackTakeFolderCommand c => PackTakeFolder(state, c, ids),
            SetRecordingCommand c => SetRecording(state, c),
            CreateStackCommand c => CreateStack(state, c, ids),
            RemoveStackCommand c => RemoveStack(state, c),
            SetTrackParentCommand c => SetTrackParent(state, c),
            SetProjectNotesCommand c => SetProjectNotes(c),
            JoinRegionsCommand c => JoinRegions(state, c),
            CopyRegionsCommand c => CopyRegions(state, c, ids),
            _ => throw new EditException($"Unknown command {command.GetType().Name}.")
        };

    /// <summary>The project sample at which a region ends (exclusive).</summary>
    public static long EndSample(ProjectState state, AudioRegion region) =>
        state.TempoMap.SampleAt(region.StartTick, state.SampleRate) + region.Length;

    static DeltaOp[] AddTrack(ProjectState state, AddTrackCommand c, IIdSource ids, PluginCatalog plugins) {
        var count = state.Tracks.Count;
        if (count >= MaxTracks) {
            throw new EditException($"A project holds at most {MaxTracks} tracks.");
        }

        var index = c.Index ?? count;
        if (index < 0 || index > count) {
            throw new EditException($"Track index {index} is outside 0…{count}.");
        }

        var kind = c.Kind ?? TrackKind.Audio;
        if (!Enum.IsDefined(kind)) {
            throw new EditException($"Unknown track kind {kind}.");
        }

        if (c.PluginId is not null && kind != TrackKind.Instrument) {
            throw new EditException("Only an instrument track takes a plugin.");
        }

        var instrument = kind == TrackKind.Instrument
            ? NewInstrument(plugins, c.PluginId ?? PluginCatalog.DefaultInstrumentId, c.PluginId is not null)
            : null;
        var name = c.Name is null ? NextTrackName(state, kind) : Name(c.Name, "track name");
        var track = new Track(
            ids.Next(IdPrefix.Track),
            name,
            kind,
            ProjectDefaults.Colors[count % ProjectDefaults.Colors.Count],
            kind == TrackKind.Audio ? new(1, c.Stereo) : new(0, false),
            false,
            false,
            ProjectDefaults.Strip,
            instrument,
            ProjectDefaults.MidiInput,
            null,
            null,
            false,
            string.Empty
        );
        return [new TrackUpserted(track, index)];
    }

    static string NextTrackName(ProjectState state, TrackKind kind) {
        var prefix = kind switch {
            TrackKind.Instrument => "Inst",
            TrackKind.Midi => "MIDI",
            TrackKind.Stack => "Stack",
            _ => "Audio"
        };
        // Numbered by kind, as Logic does: the first instrument track is "Inst 1" whatever else the project holds.
        var sameKind = 0;
        foreach (var track in state.Tracks) {
            if (track.Kind == kind) {
                sameKind++;
            }
        }

        for (var n = sameKind + 1;; n++) {
            var name = $"{prefix} {n}";
            if (!state.Tracks.Exists(t => t.Name == name)) {
                return name;
            }
        }
    }

    static List<DeltaOp> RemoveTrack(ProjectState state, RemoveTrackCommand c) {
        var track = TrackOf(state, c.TrackId);
        var ops = new List<DeltaOp>();
        foreach (var region in state.RegionsOf(track.Id)) {
            ops.Add(new RegionRemoved(region.Id));
        }

        foreach (var region in state.MidiRegionsOf(track.Id)) {
            ops.Add(new MidiRegionRemoved(region.Id));
        }

        foreach (var folder in state.TakeFoldersOf(track.Id)) {
            ops.Add(new TakeFolderRemoved(folder.Id));
        }

        // A stack goes with its members, as in Logic (removeStack keeps them).
        foreach (var member in state.MembersOf(track.Id).ToArray()) {
            ops.AddRange(RemoveTrack(state, new(member.Id)));
        }

        ops.Add(new TrackRemoved(track.Id));
        return ops;
    }

    static DeltaOp[] MoveTrack(ProjectState state, MoveTrackCommand c) {
        var track = TrackOf(state, c.TrackId);
        if (c.Index < 0 || c.Index >= state.Tracks.Count) {
            throw new EditException($"Track index {c.Index} is outside 0…{state.Tracks.Count - 1}.");
        }

        return [new TrackUpserted(track, c.Index)];
    }

    static DeltaOp[] SetTrack(ProjectState state, SetTrackCommand c) {
        var track = TrackOf(state, c.TrackId);
        if (c.Color is { } color && (string.IsNullOrWhiteSpace(color) || color.Length > 64)) {
            throw new EditException("A colour is a CSS colour of at most 64 characters.");
        }

        if (c.Input is { } input && (input.Channel < 0 || input.Channel > MaxInputChannel)) {
            throw new EditException($"Input channel {input.Channel} is outside 0…{MaxInputChannel}.");
        }

        if (track.Kind == TrackKind.Stack && (c.RecordArmed == true || c.InputMonitoring == true)) {
            throw new EditException("A stack records and monitors nothing itself: arm its members.");
        }

        var changed = track with {
            Name = c.Name is null ? track.Name : Name(c.Name, "track name"),
            Color = c.Color ?? track.Color,
            Input = c.Input ?? track.Input,
            RecordArmed = c.RecordArmed ?? track.RecordArmed,
            InputMonitoring = c.InputMonitoring ?? track.InputMonitoring,
            Collapsed = c.Collapsed ?? track.Collapsed,
            Notes = c.Notes is null ? track.Notes : Text(c.Notes, "track notes")
        };
        return [new TrackUpserted(changed, state.IndexOfTrack(track.Id))];
    }

    static DeltaOp[] SetStrip(ProjectState state, SetStripCommand c) {
        if (c.VolumeDb is { } volume && !(volume >= Units.SilenceDb && volume <= MaxVolumeDb)) {
            throw new EditException($"Volume {volume} dB is outside {Units.SilenceDb}…{MaxVolumeDb} dB.");
        }

        if (c.Pan is { } pan && !(pan is >= -1 and <= 1)) {
            throw new EditException($"Pan {pan} is outside −1…1.");
        }

        if (c.TrackId is null) {
            return [new MasterChanged(Changed(state.Master, c))];
        }

        var track = TrackOf(state, c.TrackId);
        return [new TrackUpserted(track with { Strip = Changed(track.Strip, c) }, state.IndexOfTrack(track.Id))];
    }

    static ChannelStrip Changed(ChannelStrip strip, SetStripCommand c) =>
        new(c.VolumeDb ?? strip.VolumeDb, c.Pan ?? strip.Pan, c.Mute ?? strip.Mute, c.Solo ?? strip.Solo);

    static DeltaOp[] AddRegion(ProjectState state, AddRegionCommand c, IIdSource ids) {
        var track = AudioTrackOf(state, c.TrackId);
        var file = FileOf(state, c.FileId);
        var offset = c.FileOffset ?? 0;
        var length = c.Length ?? file.Frames - offset;
        CheckTick(c.StartTick, "start");
        CheckSpan(file, offset, length);
        var region = new AudioRegion(
            ids.Next(IdPrefix.Region),
            track.Id,
            file.Id,
            file.Name,
            c.StartTick,
            offset,
            length,
            0,
            0,
            0,
            0,
            0,
            false
        );
        return [new RegionUpserted(region)];
    }

    static DeltaOp[] MoveRegion(ProjectState state, MoveRegionCommand c) {
        CheckTick(c.StartTick, "start");
        if (state.MidiRegions.TryGetValue(c.RegionId, out var midi)) {
            var midiTrack = c.TrackId is null ? midi.TrackId : MidiTrackOf(state, c.TrackId).Id;
            return [new MidiRegionUpserted(midi with { StartTick = c.StartTick, TrackId = midiTrack })];
        }

        if (state.TakeFolders.TryGetValue(c.RegionId ?? string.Empty, out var folder)) {
            var folderTrack = c.TrackId is null ? folder.TrackId : AudioTrackOf(state, c.TrackId).Id;
            return [
                new TakeFolderUpserted(
                    Timeline.Shifted(folder, c.StartTick - folder.StartTick) with { TrackId = folderTrack }
                )
            ];
        }

        var region = RegionOf(state, c.RegionId ?? string.Empty);
        var trackId = c.TrackId is null ? region.TrackId : AudioTrackOf(state, c.TrackId).Id;
        return [new RegionUpserted(region with { StartTick = c.StartTick, TrackId = trackId })];
    }

    static DeltaOp[] ResizeRegion(ProjectState state, ResizeRegionCommand c) {
        if (state.MidiRegions.ContainsKey(c.RegionId)) {
            throw new EditException($"Region '{c.RegionId}' is a MIDI region: resize it with resizeMidiRegion.");
        }

        var region = RegionOf(state, c.RegionId);
        CheckTick(c.StartTick, "start");
        CheckSpan(FileOf(state, region.FileId), c.FileOffset, c.Length);
        var fadeIn = Math.Min(region.FadeIn, c.Length);
        var fadeOut = Math.Min(region.FadeOut, c.Length - fadeIn);
        return [
            new RegionUpserted(
                region with {
                    StartTick = c.StartTick,
                    FileOffset = c.FileOffset,
                    Length = c.Length,
                    FadeIn = fadeIn,
                    FadeOut = fadeOut
                }
            )
        ];
    }

    static DeltaOp[] SplitRegion(ProjectState state, SplitRegionCommand c, IIdSource ids) {
        if (state.MidiRegions.TryGetValue(c.RegionId, out var midi)) {
            return SplitMidiRegion(midi, c.AtTick, ids);
        }

        if (state.TakeFolders.ContainsKey(c.RegionId ?? string.Empty)) {
            throw new EditException($"'{c.RegionId}' is a take folder: flatten it to split its regions.");
        }

        var region = RegionOf(state, c.RegionId!);
        var map = state.TempoMap;
        var start = map.SampleAt(region.StartTick, state.SampleRate);
        var at = c.AtTick > region.StartTick ? map.SampleAt(c.AtTick, state.SampleRate) - start : 0;
        if (at <= 0 || at >= region.Length) {
            throw new EditException($"Tick {c.AtTick} is not inside region '{region.Id}'.");
        }

        var left = region with { Length = at, FadeIn = Math.Min(region.FadeIn, at), FadeOut = 0 };
        var rightLength = region.Length - at;
        var right = region with {
            Id = ids.Next(IdPrefix.Region),
            StartTick = c.AtTick,
            FileOffset = region.FileOffset + at,
            Length = rightLength,
            FadeIn = 0,
            FadeOut = Math.Min(region.FadeOut, rightLength)
        };
        return [new RegionUpserted(left), new RegionUpserted(right)];
    }

    static DeltaOp[] SetRegion(ProjectState state, SetRegionCommand c) {
        if (state.MidiRegions.ContainsKey(c.RegionId)) {
            throw new EditException($"Region '{c.RegionId}' is a MIDI region: change it with setMidiRegion.");
        }

        var region = RegionOf(state, c.RegionId);
        if (c.GainDb is { } gain && !(gain is >= MinRegionGainDb and <= MaxRegionGainDb)) {
            throw new EditException($"Region gain {gain} dB is outside {MinRegionGainDb}…{MaxRegionGainDb} dB.");
        }

        var fadeIn = c.FadeIn ?? region.FadeIn;
        var fadeOut = c.FadeOut ?? region.FadeOut;
        if (fadeIn < 0
            || fadeOut < 0
            || fadeIn + fadeOut > region.Length
            || fadeIn > int.MaxValue
            || fadeOut > int.MaxValue) {
            throw new EditException("Fades are at least 0 frames and together no longer than the region.");
        }

        CheckCurve(c.FadeInCurve);
        CheckCurve(c.FadeOutCurve);
        return [
            new RegionUpserted(
                region with {
                    Name = c.Name is null ? region.Name : Name(c.Name, "region name"),
                    GainDb = c.GainDb ?? region.GainDb,
                    FadeIn = fadeIn,
                    FadeOut = fadeOut,
                    FadeInCurve = c.FadeInCurve ?? region.FadeInCurve,
                    FadeOutCurve = c.FadeOutCurve ?? region.FadeOutCurve,
                    Muted = c.Muted ?? region.Muted
                }
            )
        ];
    }

    static DeltaOp[] DeleteRegions(ProjectState state, DeleteRegionsCommand c) {
        if (c.RegionIds is not { Count: > 0 }) {
            throw new EditException("Name at least one region to delete.");
        }

        return [
            ..c.RegionIds.Distinct(StringComparer.Ordinal)
                .Select(id => Timeline.Region(state, id) is { } item
                        ? Timeline.Remove(item)
                        : throw new EditException($"No region '{id}'.")
                )
        ];
    }

    /// <summary>A fade curve is −1…1 (0 linear).</summary>
    static void CheckCurve(double? curve) {
        if (curve is { } c && !(c is >= -1 and <= 1)) {
            throw new EditException($"A fade curve is −1…1, not {c}.");
        }
    }

    static DeltaOp[] SetCycle(ProjectState state, SetCycleCommand c) {
        var old = state.Settings.Cycle;
        var cycle = new CycleRange(c.Enabled ?? old.Enabled, c.StartTick ?? old.StartTick, c.EndTick ?? old.EndTick);
        CheckTick(cycle.StartTick, "cycle start");
        CheckTick(cycle.EndTick, "cycle end");
        if (cycle.EndTick <= cycle.StartTick) {
            throw new EditException("The cycle ends after it starts.");
        }

        return [new SettingsChanged(state.Settings with { Cycle = cycle })];
    }

    static DeltaOp[] SetMetronome(ProjectState state, SetMetronomeCommand c) {
        if (c.LevelDb is { } level && !(level is >= MinClickDb and <= MaxClickDb)) {
            throw new EditException($"Click level {level} dB is outside {MinClickDb}…{MaxClickDb} dB.");
        }

        var old = state.Settings.Metronome;
        return [
            new SettingsChanged(
                state.Settings with {
                    Metronome = new(c.Enabled ?? old.Enabled, c.CountIn ?? old.CountIn, c.LevelDb ?? old.LevelDb)
                }
            )
        ];
    }

    static string Name(string name, string what) {
        var trimmed = name.Trim();
        return trimmed.Length is 0 or > MaxNameLength
            ? throw new EditException($"A {what} has 1…{MaxNameLength} characters.")
            : trimmed;
    }

    static void CheckTick(long tick, string what) {
        if (tick is < 0 or > MaxTick) {
            throw new EditException($"The {what} tick {tick} is outside 0…{MaxTick}.");
        }
    }

    static void CheckSpan(AudioFile file, long offset, long length) {
        if (offset < 0 || length <= 0 || offset > file.Frames || length > file.Frames - offset) {
            throw new EditException(
                $"Frames {offset}…{offset + length} are not inside file '{file.Id}' ({file.Frames} frames)."
            );
        }
    }

    static Track TrackOf(ProjectState state, string id) =>
        state.FindTrack(id) ?? throw new EditException($"No track '{id}'.");

    static Track AudioTrackOf(ProjectState state, string id) {
        var track = TrackOf(state, id);
        return track.Kind == TrackKind.Audio
            ? track
            : throw new EditException($"Track '{id}' is not an audio track: it holds MIDI regions.");
    }

    static Track MidiTrackOf(ProjectState state, string id) {
        var track = TrackOf(state, id);
        return IsMidiKind(track.Kind)
            ? track
            : throw new EditException($"Track '{id}' is an audio track: it holds no MIDI regions.");
    }

    /// <summary>Whether a track of <paramref name="kind" /> holds MIDI regions and takes live MIDI.</summary>
    public static bool IsMidiKind(TrackKind kind) => kind is TrackKind.Instrument or TrackKind.Midi;

    static AudioRegion RegionOf(ProjectState state, string id) =>
        state.Regions.TryGetValue(id, out var region) ? region : throw new EditException($"No region '{id}'.");

    static AudioFile FileOf(ProjectState state, string id) =>
        state.Files.TryGetValue(id, out var file) ? file : throw new EditException($"No audio file '{id}'.");
}

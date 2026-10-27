using Anode.Contracts;
using Anode.Model.Time;

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
public static class Edits {
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

    /// <summary>Applies a batch atomically: all of it, or (on <see cref="EditException" />) none of it.</summary>
    public static EditResult Apply(ProjectState state, IReadOnlyList<EditCommand> commands, IIdSource ids) {
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
                ops = Ops(state, commands[i] ?? throw new EditException("A command is null."), ids);
            } catch (EditException e) when (commands.Count > 1) {
                throw new EditException($"Command {i + 1} ({commands[i]?.GetType().Name}): {e.Message}", e);
            }

            state = Step(state, ops, forward, inverse);
        }

        inverse.Reverse();
        return new(state, forward, inverse);
    }

    public static EditResult Apply(ProjectState state, EditCommand command, IIdSource ids) =>
        Apply(state, [command], ids);

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
    public static IReadOnlyList<DeltaOp> Ops(ProjectState state, EditCommand command, IIdSource ids) =>
        command switch {
            AddTrackCommand c => AddTrack(state, c, ids),
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
            _ => throw new EditException($"Unknown command {command.GetType().Name}.")
        };

    /// <summary>The project sample at which a region ends (exclusive).</summary>
    public static long EndSample(ProjectState state, AudioRegion region) =>
        state.TempoMap.SampleAt(region.StartTick, state.SampleRate) + region.Length;

    static DeltaOp[] AddTrack(ProjectState state, AddTrackCommand c, IIdSource ids) {
        var count = state.Tracks.Count;
        if (count >= MaxTracks) {
            throw new EditException($"A project holds at most {MaxTracks} tracks.");
        }

        var index = c.Index ?? count;
        if (index < 0 || index > count) {
            throw new EditException($"Track index {index} is outside 0…{count}.");
        }

        var name = c.Name is null ? NextTrackName(state) : Name(c.Name, "track name");
        var track = new Track(
            ids.Next(IdPrefix.Track),
            name,
            TrackKind.Audio,
            ProjectDefaults.Colors[count % ProjectDefaults.Colors.Count],
            new(1, c.Stereo),
            false,
            false,
            ProjectDefaults.Strip
        );
        return [new TrackUpserted(track, index)];
    }

    static string NextTrackName(ProjectState state) {
        for (var n = state.Tracks.Count + 1;; n++) {
            var name = $"Audio {n}";
            if (!state.Tracks.Any(t => t.Name == name)) {
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

        var changed = track with {
            Name = c.Name is null ? track.Name : Name(c.Name, "track name"),
            Color = c.Color ?? track.Color,
            Input = c.Input ?? track.Input,
            RecordArmed = c.RecordArmed ?? track.RecordArmed,
            InputMonitoring = c.InputMonitoring ?? track.InputMonitoring
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
        var track = TrackOf(state, c.TrackId);
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
            0
        );
        return [new RegionUpserted(region)];
    }

    static DeltaOp[] MoveRegion(ProjectState state, MoveRegionCommand c) {
        var region = RegionOf(state, c.RegionId);
        CheckTick(c.StartTick, "start");
        var trackId = c.TrackId is null ? region.TrackId : TrackOf(state, c.TrackId).Id;
        return [new RegionUpserted(region with { StartTick = c.StartTick, TrackId = trackId })];
    }

    static DeltaOp[] ResizeRegion(ProjectState state, ResizeRegionCommand c) {
        var region = RegionOf(state, c.RegionId);
        CheckTick(c.StartTick, "start");
        CheckSpan(FileOf(state, region.FileId), c.FileOffset, c.Length);
        var fadeIn = Math.Min(region.FadeIn, c.Length);
        var fadeOut = Math.Min(region.FadeOut, c.Length - fadeIn);
        return [
            new RegionUpserted(region with {
                    StartTick = c.StartTick, FileOffset = c.FileOffset, Length = c.Length, FadeIn = fadeIn,
                    FadeOut = fadeOut
                }
            )
        ];
    }

    static DeltaOp[] SplitRegion(ProjectState state, SplitRegionCommand c, IIdSource ids) {
        var region = RegionOf(state, c.RegionId);
        var map = state.TempoMap;
        var start = map.SampleAt(region.StartTick, state.SampleRate);
        var at = c.AtTick > region.StartTick ? map.SampleAt(c.AtTick, state.SampleRate) - start : 0;
        if (at <= 0 || at >= region.Length) {
            throw new EditException($"Tick {c.AtTick} is not inside region '{region.Id}'.");
        }

        var left = region with {
            Length = at, FadeIn = Math.Min(region.FadeIn, at), FadeOut = 0
        };
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
        var region = RegionOf(state, c.RegionId);
        if (c.GainDb is { } gain && !(gain is >= MinRegionGainDb and <= MaxRegionGainDb)) {
            throw new EditException($"Region gain {gain} dB is outside {MinRegionGainDb}…{MaxRegionGainDb} dB.");
        }

        var fadeIn = c.FadeIn ?? region.FadeIn;
        var fadeOut = c.FadeOut ?? region.FadeOut;
        if (fadeIn < 0 || fadeOut < 0 || fadeIn + fadeOut > region.Length || fadeIn > int.MaxValue
            || fadeOut > int.MaxValue) {
            throw new EditException("Fades are at least 0 frames and together no longer than the region.");
        }

        return [
            new RegionUpserted(region with {
                    Name = c.Name is null ? region.Name : Name(c.Name, "region name"),
                    GainDb = c.GainDb ?? region.GainDb,
                    FadeIn = fadeIn,
                    FadeOut = fadeOut
                }
            )
        ];
    }

    static DeltaOp[] DeleteRegions(ProjectState state, DeleteRegionsCommand c) {
        if (c.RegionIds is not { Count: > 0 }) {
            throw new EditException("Name at least one region to delete.");
        }

        return [..c.RegionIds.Distinct(StringComparer.Ordinal).Select(id => new RegionRemoved(RegionOf(state, id).Id))];
    }

    static DeltaOp[] SetTempo(ProjectState state, SetTempoCommand c) {
        if (!(c.Bpm is >= TempoMap.MinBpm and <= TempoMap.MaxBpm)) {
            throw new EditException($"Tempo {c.Bpm} is outside {TempoMap.MinBpm}…{TempoMap.MaxBpm} BPM.");
        }

        return [new SettingsChanged(state.Settings with { Tempo = c.Bpm })];
    }

    static DeltaOp[] SetSignature(ProjectState state, SetSignatureCommand c) {
        if (c.Numerator is < 1 or > 32) {
            throw new EditException($"A signature's numerator is 1…32, not {c.Numerator}.");
        }

        if (c.Denominator is not (1 or 2 or 4 or 8 or 16 or 32)) {
            throw new EditException($"A signature's denominator is 1, 2, 4, 8, 16 or 32, not {c.Denominator}.");
        }

        return [new SettingsChanged(state.Settings with { Signature = new(c.Numerator, c.Denominator) })];
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
            new SettingsChanged(state.Settings with {
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

    static AudioRegion RegionOf(ProjectState state, string id) =>
        state.Regions.TryGetValue(id, out var region) ? region : throw new EditException($"No region '{id}'.");

    static AudioFile FileOf(ProjectState state, string id) =>
        state.Files.TryGetValue(id, out var file) ? file : throw new EditException($"No audio file '{id}'.");
}

using Anode.Contracts;
using Anode.Model.Compile;
using Anode.Model.Time;

namespace Anode.Model.Editing;

/// <summary>Step Sequencer pattern regions (docs/07-phase-6.md, "Pattern regions").</summary>
public static partial class Edits {
    public const int MaxPatternRows = 128;
    public const int MaxRowLength = 64;
    public const int MaxStepTicks = 3840;
    public const int DefaultPatternVelocity = 100;

    static List<DeltaOp>? Phase6Ops(
        ProjectState state,
        EditCommand command,
        IIdSource ids,
        PluginCatalog plugins,
        IPatchSource patches
    ) =>
        command switch {
            AddPatternRegionCommand c => AddPatternRegion(state, c, ids),
            SetPatternRegionCommand c => SetPatternRegion(state, c),
            SetPatternCommand c => SetPattern(state, c),
            ConvertPatternToMidiCommand c => ConvertPatternToMidi(state, c, ids),
            ReseedPatternCommand c => ReseedPattern(state, c),
            CreateDrumMachineCommand c => CreateDrumMachine(state, c, ids, plugins, patches),
            AddDrumPadCommand c => AddDrumPad(state, c, ids, plugins),
            SetDrumPadCommand c => SetDrumPad(state, c, plugins),
            RemoveDrumPadCommand c => RemoveDrumPad(state, c),
            SetSmartControlsCommand c => SetSmartControls(state, c, ids, plugins),
            SetSmartControlCommand c => SetSmartControl(state, c, plugins),
            SetSmartMappingsCommand c => SetSmartMappings(state, c, plugins),
            AutoMapSmartControlsCommand c => AutoMapSmartControls(state, c, plugins),
            CompareSmartControlsCommand c => CompareSmartControls(state, c, plugins),
            AddSceneCommand c => AddScene(state, c, ids),
            RemoveSceneCommand c => RemoveScene(state, c),
            SetSceneCommand c => SetScene(state, c),
            SetCellCommand c => SetCell(state, c, ids),
            DeleteCellsCommand c => DeleteCells(state, c),
            MoveCellCommand c => MoveCell(state, c, ids),
            SetLiveLoopsCommand c => SetLiveLoops(state, c, ids, plugins),
            SetTrackActivationCommand c => SetTrackActivation(state, c),
            LoadInsertFileCommand c => LoadInsertFile(state, c, plugins),
            _ => null
        };

    static List<DeltaOp> AddPatternRegion(ProjectState state, AddPatternRegionCommand c, IIdSource ids) {
        var track = MidiTrackOf(state, c.TrackId);
        CheckTick(c.StartTick, "start");
        var length = c.LengthTicks ?? SignatureMap.BarTicks(state.SignatureMap.At(c.StartTick));
        CheckLength(length);
        var pattern = c.Pattern is null ? DefaultPattern(track, ids) : CheckPattern(c.Pattern);
        var region = new PatternRegion(
            ids.Next(IdPrefix.PatternRegion),
            track.Id,
            c.Name is null ? "Pattern" : Name(c.Name, "region name"),
            c.StartTick,
            length,
            false,
            pattern
        );
        return [new PatternRegionUpserted(region)];
    }

    /// <summary>
    ///     A new region's pattern: on a Drum Machine Designer one pad row per pad, else one note row on C3; each 16 steps
    ///     of a 16th, all off.
    /// </summary>
    public static Pattern DefaultPattern(Track track, IIdSource ids) {
        PatternRow Row(PatternRowKind kind, int note, string name) =>
            new(
                ids.Next(IdPrefix.PatternRow),
                kind,
                note,
                name,
                16,
                null,
                PatternPlayMode.Forward,
                false,
                [..Enumerable.Repeat(new PatternStep(false, DefaultPatternVelocity, 1, 1, 1, 0), 16)]
            );

        IReadOnlyList<PatternRow> rows = track.DrumMachine is { Pads.Count: > 0 } machine
            ? [..machine.Pads.Select(p => Row(PatternRowKind.Pad, p.Note, p.Name))]
            : [Row(PatternRowKind.Note, 60, NoteName(60))];
        return new(rows, 0, 0);
    }

    static void CheckLength(long length) {
        if (length is < 1 or > MaxTick) {
            throw new EditException($"A region's length is 1…{MaxTick} ticks, not {length}.");
        }
    }

    /// <summary>A pattern as kept, or <see cref="EditException" /> when a value is outside its range.</summary>
    public static Pattern CheckPattern(Pattern? pattern) {
        if (pattern?.Rows is not { } rows) {
            throw new EditException("A pattern has rows.");
        }

        if (rows.Count > MaxPatternRows) {
            throw new EditException($"A pattern has at most {MaxPatternRows} rows.");
        }

        if (!(pattern.Swing is >= 0 and <= 1)) {
            throw new EditException($"Swing is 0…1, not {pattern.Swing}.");
        }

        CheckStepTicks(pattern.StepTicks);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows) {
            CheckRow(row, seen);
        }

        return pattern with { Rows = [..rows] };
    }

    /// <summary>A row of a pattern: an id of its own, a known kind and play mode, a note, steps as its length.</summary>
    static void CheckRow(PatternRow? row, HashSet<string> seen) {
        if (row is null || string.IsNullOrEmpty(row.Id) || row.Id.Length > 64 || !seen.Add(row.Id)) {
            throw new EditException("Every row has an id of its own (1…64 characters).");
        }

        if (!Enum.IsDefined(row.Kind) || !Enum.IsDefined(row.PlayMode)) {
            throw new EditException($"Row '{row.Id}' has an unknown kind or play mode.");
        }

        var (low, high) = row.Kind == PatternRowKind.ChordDegree ? (1, 7) : (0, 127);
        if (row.Note < low || row.Note > high) {
            throw new EditException($"Row '{row.Id}' plays {low}…{high}, not {row.Note}.");
        }

        if (row.Name is null || row.Name.Length > MaxNameLength) {
            throw new EditException($"A row's name has at most {MaxNameLength} characters.");
        }

        if (row.Length is < 1 or > MaxRowLength || row.Steps is null || row.Steps.Count != row.Length) {
            throw new EditException($"Row '{row.Id}' has 1…{MaxRowLength} steps, as many as its length.");
        }

        if (row.StepTicks is { } stepTicks) {
            CheckStepTicks(stepTicks);
        }

        foreach (var step in row.Steps) {
            CheckStep(step);
        }
    }

    static void CheckStepTicks(int stepTicks) {
        if (stepTicks is < 1 or > MaxStepTicks) {
            throw new EditException($"A step is 1…{MaxStepTicks} ticks, not {stepTicks}.");
        }
    }

    static void CheckStep(PatternStep? step) {
        if (step is null) {
            throw new EditException("A step is null.");
        }

        if (step.Velocity is < 1 or > 127
            || !(step.Gate is > 0 and <= 16)
            || step.Repeats is < 1 or > 8
            || !(step.Chance is >= 0 and <= 1)
            || !(step.Offset is >= -0.5 and <= 0.5)) {
            throw new EditException(
                "A step's velocity is 1…127, gate over 0 up to 16, repeats 1…8, chance 0…1 and offset −0.5…0.5."
            );
        }
    }

    static List<DeltaOp> SetPatternRegion(ProjectState state, SetPatternRegionCommand c) {
        var region = PatternRegionOf(state, c.RegionId);
        if (c.StartTick is { } start) {
            CheckTick(start, "start");
        }

        if (c.LengthTicks is { } length) {
            CheckLength(length);
        }

        return [
            new PatternRegionUpserted(
                region with {
                    StartTick = c.StartTick ?? region.StartTick,
                    LengthTicks = c.LengthTicks ?? region.LengthTicks,
                    Name = c.Name is null ? region.Name : Name(c.Name, "region name"),
                    Muted = c.Muted ?? region.Muted
                }
            )
        ];
    }

    static List<DeltaOp> SetPattern(ProjectState state, SetPatternCommand c) {
        var region = PatternRegionOf(state, c.RegionId);
        return [new PatternRegionUpserted(region with { Pattern = CheckPattern(c.Pattern) })];
    }

    static List<DeltaOp> ReseedPattern(ProjectState state, ReseedPatternCommand c) {
        var region = PatternRegionOf(state, c.RegionId);
        var seed = c.Seed ?? Random.Shared.NextInt64(1, long.MaxValue);
        return [new PatternRegionUpserted(region with { Pattern = region.Pattern with { Seed = seed } })];
    }

    static List<DeltaOp> ConvertPatternToMidi(ProjectState state, ConvertPatternToMidiCommand c, IIdSource ids) {
        var region = PatternRegionOf(state, c.RegionId);
        var next = state.LastEventId;
        var notes = PatternUnroller.Unroll(region, t => KeyAt(state, t))
            .Select(n => new Note(++next, n.Pitch, n.Velocity, 1, n.Tick, n.Length, 0))
            .ToArray();
        var midi = new MidiRegion(
            ids.Next(IdPrefix.MidiRegion),
            region.TrackId,
            region.Name,
            region.StartTick,
            region.LengthTicks,
            ProjectDefaults.Playback,
            Sorted(notes),
            [],
            region.Muted
        );
        return [new PatternRegionRemoved(region.Id), new MidiRegionUpserted(midi)];
    }

    /// <summary>The key at <paramref name="tick" /> of the project's key track.</summary>
    public static KeyEvent KeyAt(ProjectState state, long tick) {
        var keys = state.Settings.Keys;
        var key = keys is { Count: > 0 } ? keys[0] : new KeyEvent(0, 0, KeyMode.Major);
        foreach (var k in keys ?? []) {
            if (k.Tick <= tick) {
                key = k;
            }
        }

        return key;
    }

    static PatternRegion PatternRegionOf(ProjectState state, string? id) =>
        state.PatternRegions.TryGetValue(id ?? string.Empty, out var region)
            ? region
            : throw new EditException($"No pattern region '{id}'.");
}

using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>Instrument, MIDI I/O and MIDI region edits (docs/03-phase-2.md).</summary>
public static partial class Edits {
    public const int MaxNotesPerCommand = 100_000;
    public const int MaxEventsPerRegion = 1_000_000;
    public const int MaxTranspose = 96;
    public const int MaxQuantizeTicks = 960 * 16;
    public const int MaxPathLength = 4096;
    public const int MaxDeviceLength = 256;
    public const int MaxParameterIdLength = 256;

    static InstrumentSlot NewInstrument(PluginCatalog plugins, string pluginId, bool required) {
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId.Length > MaxParameterIdLength) {
            throw new EditException("Name a plugin.");
        }

        if (plugins.Find(pluginId) is not { } plugin) {
            // The default instrument of a new track, before any catalogue is known (no engine yet, and no built-ins):
            // its parameters come from the catalogue's defaults once one names them, the engine's until then.
            return required
                ? throw new EditException($"No plugin '{pluginId}'.")
                : new(pluginId, pluginId, PluginCatalog.Sorted(null), null);
        }

        return plugin.IsInstrument
            ? new(plugin.Id, plugin.Name, PluginCatalog.Defaults(plugin), null)
            : throw new EditException($"Plugin '{pluginId}' is not an instrument.");
    }

    /// <summary>A new instrument: the old one's lanes go (its parameters are not the new one's).</summary>
    static List<DeltaOp> SetInstrument(ProjectState state, SetInstrumentCommand c, PluginCatalog plugins) {
        var track = InstrumentTrackOf(state, c.TrackId);
        CheckNotFrozen(track);
        var slot = NewInstrument(plugins, c.PluginId, true);
        var ops = new List<DeltaOp>();
        ops.AddRange(LanesFor(state, track.Id, "instrument:").Select(static l => new AutomationLaneRemoved(l.Id)));
        ops.Add(new TrackUpserted(track with { Instrument = slot }, state.IndexOfTrack(track.Id)));
        return ops;
    }

    static void CheckNotFrozen(Track track) {
        if (track.FrozenFileId is not null) {
            throw new EditException($"Track '{track.Name}' is frozen: unfreeze it to change its instrument.");
        }
    }

    static DeltaOp[] SetParameter(ProjectState state, SetParameterCommand c, PluginCatalog plugins) {
        var track = InstrumentTrackOf(state, c.TrackId);
        CheckNotFrozen(track);
        var slot = track.Instrument!;
        if (string.IsNullOrEmpty(c.ParameterId) || c.ParameterId.Length > MaxParameterIdLength) {
            throw new EditException("Name a parameter.");
        }

        if (!double.IsFinite(c.Value)) {
            throw new EditException("A parameter value is a finite number.");
        }

        var value = c.Value;
        var info = plugins.Find(slot.PluginId)?.Parameters.FirstOrDefault(p => p.Id == c.ParameterId);
        if (info is not null) {
            if (value < info.Min || value > info.Max) {
                throw new EditException($"Parameter '{info.Id}' takes {info.Min}…{info.Max}, not {value}.");
            }

            if (info.Steps > 0 && info.Max > info.Min) {
                var step = (info.Max - info.Min) / info.Steps;
                value = Math.Clamp(info.Min + Math.Round((value - info.Min) / step) * step, info.Min, info.Max);
            }
        } else if (!slot.Parameters.ContainsKey(c.ParameterId)) {
            throw new EditException($"Instrument '{slot.PluginId}' has no parameter '{c.ParameterId}'.");
        }

        var parameters = PluginCatalog.Sorted(slot.Parameters).SetItem(c.ParameterId, value);
        return [
            new TrackUpserted(
                track with { Instrument = slot with { Parameters = parameters } },
                state.IndexOfTrack(track.Id)
            )
        ];
    }

    static DeltaOp[] LoadInstrumentFile(ProjectState state, LoadInstrumentFileCommand c, PluginCatalog plugins) {
        var track = InstrumentTrackOf(state, c.TrackId);
        CheckNotFrozen(track);
        var slot = track.Instrument!;
        var path = c.Path?.Trim() ?? string.Empty;
        if (path.Length is 0 or > MaxPathLength) {
            throw new EditException($"A file path has 1…{MaxPathLength} characters.");
        }

        if (plugins.Find(slot.PluginId) is { } plugin) {
            if (plugin.FileTypes.Count == 0) {
                throw new EditException($"Instrument '{plugin.Id}' loads no files.");
            }

            var extension = Path.GetExtension(path);
            if (!plugin.FileTypes.Any(t => string.Equals(t, extension, StringComparison.OrdinalIgnoreCase))) {
                throw new EditException(
                    $"Instrument '{plugin.Id}' loads {string.Join(", ", plugin.FileTypes)} files, not '{extension}'."
                );
            }
        }

        return [
            new TrackUpserted(track with { Instrument = slot with { File = path } }, state.IndexOfTrack(track.Id))
        ];
    }

    static DeltaOp[] SetMidiIo(ProjectState state, SetMidiIoCommand c) {
        var track = MidiTrackOf(state, c.TrackId);
        if (c.Input is { } input) {
            if (input.Channel is < 0 or > 16) {
                throw new EditException($"A MIDI input channel is 0 (all) or 1…16, not {input.Channel}.");
            }

            CheckDevice(input.Device, true);
        }

        if (c.Output is { } output) {
            if (track.Kind != TrackKind.Midi) {
                throw new EditException("Only an external MIDI track has a MIDI output.");
            }

            if (output.Channel is < 1 or > 16) {
                throw new EditException($"A MIDI output channel is 1…16, not {output.Channel}.");
            }

            CheckDevice(output.Device, false);
        }

        return [
            new TrackUpserted(
                track with { MidiInput = c.Input ?? track.MidiInput, MidiOutput = c.Output ?? track.MidiOutput },
                state.IndexOfTrack(track.Id)
            )
        ];
    }

    static void CheckDevice(string? device, bool optional) {
        if (device is null && optional) {
            return;
        }

        if (string.IsNullOrWhiteSpace(device) || device.Length > MaxDeviceLength) {
            throw new EditException($"A MIDI device id has 1…{MaxDeviceLength} characters.");
        }
    }

    static DeltaOp[] AddMidiRegion(ProjectState state, AddMidiRegionCommand c, IIdSource ids) {
        var track = MidiTrackOf(state, c.TrackId);
        CheckTick(c.StartTick, "start");
        CheckLength(c.LengthTicks, "region");
        var region = new MidiRegion(
            ids.Next(IdPrefix.MidiRegion),
            track.Id,
            c.Name is null ? track.Name : Name(c.Name, "region name"),
            c.StartTick,
            c.LengthTicks,
            ProjectDefaults.Playback,
            [],
            [],
            false
        );
        return [new MidiRegionUpserted(region)];
    }

    static DeltaOp[] ResizeMidiRegion(ProjectState state, ResizeMidiRegionCommand c) {
        var region = MidiRegionOf(state, c.RegionId);
        CheckTick(c.StartTick, "start");
        CheckLength(c.LengthTicks, "region");
        // The notes stay where they are in time: their positions relative to the region move the other way.
        var shift = region.StartTick - c.StartTick;
        var resized = region with { StartTick = c.StartTick, LengthTicks = c.LengthTicks };
        if (shift != 0) {
            resized = resized with {
                Notes = [..region.Notes.Select(n => n with { StartTick = n.StartTick + shift })],
                Controls = [..region.Controls.Select(e => e with { Tick = e.Tick + shift })]
            };
        }

        return [new MidiRegionUpserted(resized)];
    }

    static DeltaOp[] SetMidiRegion(ProjectState state, SetMidiRegionCommand c) {
        var region = MidiRegionOf(state, c.RegionId);
        var p = region.Playback;
        var playback = new RegionPlayback(
            c.Transpose ?? p.Transpose,
            c.Velocity ?? p.Velocity,
            c.QuantizeTicks ?? p.QuantizeTicks,
            c.QuantizeStrength ?? p.QuantizeStrength,
            c.Swing ?? p.Swing,
            c.GrooveId is null ? p.GrooveId : NoneIfEmpty(c.GrooveId)
        );
        if (playback.GrooveId is { } groove && !state.Grooves.ContainsKey(groove)) {
            throw new EditException($"No groove template '{groove}'.");
        }

        if (playback.Transpose is < -MaxTranspose or > MaxTranspose) {
            throw new EditException($"Transpose {playback.Transpose} is outside ±{MaxTranspose} semitones.");
        }

        if (playback.Velocity is < -127 or > 127) {
            throw new EditException($"A velocity offset is −127…127, not {playback.Velocity}.");
        }

        if (playback.QuantizeTicks is < 0 or > MaxQuantizeTicks) {
            throw new EditException($"A quantise grid is 0 (off) … {MaxQuantizeTicks} ticks.");
        }

        CheckUnit(playback.QuantizeStrength, "quantise strength");
        CheckUnit(playback.Swing, "swing");
        return [
            new MidiRegionUpserted(
                region with {
                    Name = c.Name is null ? region.Name : Name(c.Name, "region name"),
                    Playback = playback,
                    Muted = c.Muted ?? region.Muted
                }
            )
        ];
    }

    static DeltaOp[] AddNotes(ProjectState state, AddNotesCommand c) {
        var region = MidiRegionOf(state, c.RegionId);
        if (c.Notes is not { Count: > 0 and <= MaxNotesPerCommand }) {
            throw new EditException($"Add 1…{MaxNotesPerCommand} notes.");
        }

        CheckCapacity(region, c.Notes.Count);
        var next = state.LastEventId;
        var notes = new List<Note>(region.Notes.Count + c.Notes.Count);
        notes.AddRange(region.Notes);
        foreach (var n in c.Notes) {
            if (n is null) {
                throw new EditException("A note is null.");
            }

            var note = new Note(
                ++next,
                n.Pitch,
                n.Velocity,
                n.Channel,
                n.StartTick,
                n.LengthTicks,
                n.ArticulationId ?? 0
            );
            CheckNote(region, note, true);
            notes.Add(note);
        }

        return [new MidiRegionUpserted(region with { Notes = Sorted(notes) })];
    }

    static DeltaOp[] EditNotes(ProjectState state, EditNotesCommand c) {
        var region = MidiRegionOf(state, c.RegionId);
        if (c.Changes is not { Count: > 0 and <= MaxNotesPerCommand }) {
            throw new EditException($"Change 1…{MaxNotesPerCommand} notes.");
        }

        var notes = region.Notes.ToArray();
        var index = IndexById(notes);
        foreach (var change in c.Changes) {
            if (change is null || !index.TryGetValue(change.Id, out var i)) {
                throw new EditException($"Region '{region.Id}' has no note {change?.Id}.");
            }

            var old = notes[i];
            var note = old with {
                Pitch = change.Pitch ?? old.Pitch,
                Velocity = change.Velocity ?? old.Velocity,
                Channel = change.Channel ?? old.Channel,
                StartTick = change.StartTick ?? old.StartTick,
                LengthTicks = change.LengthTicks ?? old.LengthTicks,
                ArticulationId = change.ArticulationId ?? old.ArticulationId
            };
            CheckNote(region, note, change.StartTick.HasValue);
            notes[i] = note;
        }

        return [new MidiRegionUpserted(region with { Notes = Sorted(notes) })];
    }

    static DeltaOp[] DeleteNotes(ProjectState state, DeleteNotesCommand c) {
        var region = MidiRegionOf(state, c.RegionId);
        var gone = Known(region.Notes.Select(static n => n.Id), c.NoteIds, "note", region.Id);
        return [new MidiRegionUpserted(region with { Notes = [..region.Notes.Where(n => !gone.Contains(n.Id))] })];
    }

    static DeltaOp[] QuantizeNotes(ProjectState state, QuantizeNotesCommand c, IAnalysisSource analysis) {
        var region = MidiRegionOf(state, c.RegionId);
        var groove = GrooveFor(state, region.TrackId, c.GrooveId, analysis);
        if (groove is null) {
            CheckGrid(c);
        }

        CheckUnit(c.Strength, "quantise strength");
        var chosen = c.NoteIds is null
            ? null
            : Known(region.Notes.Select(static n => n.Id), c.NoteIds, "note", region.Id);
        if (groove is not null) {
            return [new MidiRegionUpserted(Grooved(region, chosen, groove, c))];
        }

        var notes = region.Notes.Select(n => {
                if (chosen is not null && !chosen.Contains(n.Id)) {
                    return n;
                }

                // The grid is the project's: positions are quantised where they sound, then made relative again.
                var at = Quantize.Tick(region.StartTick + n.StartTick, c.GridTicks, c.Strength, c.Swing);
                return n with { StartTick = at - region.StartTick };
            }
        );
        return [new MidiRegionUpserted(region with { Notes = Sorted(notes) })];
    }

    /// <summary>A quantise to the grid: a grid, a swing, and no velocities to follow.</summary>
    static void CheckGrid(QuantizeNotesCommand c) {
        if (c.GridTicks is < 1 or > MaxQuantizeTicks) {
            throw new EditException($"A quantise grid is 1…{MaxQuantizeTicks} ticks.");
        }

        if (c.GrooveVelocity == true) {
            throw new EditException("Velocities follow a groove template only: name one.");
        }

        CheckUnit(c.Swing, "swing");
    }

    /// <summary>A region with its chosen notes (null: all) quantised to a groove (Phase 7).</summary>
    static MidiRegion Grooved(MidiRegion region, HashSet<long>? chosen, GrooveTemplate groove, QuantizeNotesCommand c) {
        var picked = region.Notes.Where(n => chosen is null || chosen.Contains(n.Id)).ToArray();
        var moved = MidiAlgorithms.Current.GrooveQuantize(
                picked,
                region.StartTick,
                groove,
                c.Strength,
                c.GrooveVelocity == true
            )
            .Where(static n => n is not null)
            .ToDictionary(static n => n.Id);
        var grooved = region.Notes.Select(n => moved.TryGetValue(n.Id, out var m)
                    ? n with { StartTick = m.StartTick, Velocity = Math.Clamp(m.Velocity, 1, 127) }
                    : n
            )
            .ToArray();
        foreach (var note in grooved) {
            CheckNote(region, note, true);
        }

        return region with { Notes = Sorted(grooved) };
    }

    /// <summary>A text, or null when it is empty (a command's way to clear a reference).</summary>
    static string? NoneIfEmpty(string text) => text.Length == 0 ? null : text;

    static DeltaOp[] AddControls(ProjectState state, AddControlsCommand c) {
        var region = MidiRegionOf(state, c.RegionId);
        if (c.Events is not { Count: > 0 and <= MaxNotesPerCommand }) {
            throw new EditException($"Add 1…{MaxNotesPerCommand} controller events.");
        }

        CheckCapacity(region, c.Events.Count);
        var next = state.LastEventId;
        var controls = new List<ControlEvent>(region.Controls.Count + c.Events.Count);
        controls.AddRange(region.Controls);
        foreach (var e in c.Events) {
            if (e is null) {
                throw new EditException("A controller event is null.");
            }

            var control = new ControlEvent(++next, e.Kind, e.Channel, e.Number, e.Value, e.Tick);
            CheckControl(region, control);
            controls.Add(control);
        }

        return [new MidiRegionUpserted(region with { Controls = Sorted(controls) })];
    }

    static DeltaOp[] DeleteControls(ProjectState state, DeleteControlsCommand c) {
        var region = MidiRegionOf(state, c.RegionId);
        var gone = Known(region.Controls.Select(static e => e.Id), c.EventIds, "controller event", region.Id);
        return [
            new MidiRegionUpserted(region with { Controls = [..region.Controls.Where(e => !gone.Contains(e.Id))] })
        ];
    }

    /// <summary>A MIDI region in two at <paramref name="at" />: notes and events go to the side they start on.</summary>
    static DeltaOp[] SplitMidiRegion(MidiRegion region, long at, IIdSource ids) {
        var cut = at - region.StartTick;
        if (cut <= 0 || cut >= region.LengthTicks) {
            throw new EditException($"Tick {at} is not inside region '{region.Id}'.");
        }

        var left = region with {
            LengthTicks = cut,
            Notes = [..region.Notes.Where(n => n.StartTick < cut)],
            Controls = [..region.Controls.Where(e => e.Tick < cut)]
        };
        var right = region with {
            Id = ids.Next(IdPrefix.MidiRegion),
            StartTick = at,
            LengthTicks = region.LengthTicks - cut,
            Notes = [
                ..region.Notes.Where(n => n.StartTick >= cut).Select(n => n with { StartTick = n.StartTick - cut })
            ],
            Controls = [..region.Controls.Where(e => e.Tick >= cut).Select(e => e with { Tick = e.Tick - cut })]
        };
        return [new MidiRegionUpserted(left), new MidiRegionUpserted(right)];
    }

    /// <summary>Notes in canonical order: by start, then pitch, then id.</summary>
    public static Note[] Sorted(IEnumerable<Note> notes) =>
        [..notes.OrderBy(static n => n.StartTick).ThenBy(static n => n.Pitch).ThenBy(static n => n.Id)];

    /// <summary>Controller events in canonical order: by tick, then id.</summary>
    public static ControlEvent[] Sorted(IEnumerable<ControlEvent> events) =>
        [..events.OrderBy(static e => e.Tick).ThenBy(static e => e.Id)];

    static void CheckNote(MidiRegion region, Note note, bool checkStart) {
        if (note.Pitch is < 0 or > 127) {
            throw new EditException($"A note's pitch is 0…127, not {note.Pitch}.");
        }

        if (note.Velocity is < 1 or > 127) {
            throw new EditException($"A note's velocity is 1…127, not {note.Velocity}.");
        }

        CheckChannel(note.Channel);
        CheckLength(note.LengthTicks, "note");
        if (note.ArticulationId is < 0 or > MaxArticulationId) {
            throw new EditException(
                $"A note's articulation is 1…{MaxArticulationId} (0: none), not {note.ArticulationId}."
            );
        }

        if (checkStart) {
            CheckPosition(region, note.StartTick);
        }
    }

    static void CheckControl(MidiRegion region, ControlEvent e) {
        CheckChannel(e.Channel);
        CheckPosition(region, e.Tick);
        var (min, max) = e.Kind switch {
            ControlKind.ControlChange => (0, 127),
            ControlKind.PitchBend => (-8192, 8191),
            ControlKind.ChannelPressure => (0, 127),
            ControlKind.ProgramChange => (0, 127),
            ControlKind.PolyPressure => (0, 127),
            _ => throw new EditException($"Unknown controller kind {e.Kind}.")
        };
        if (e.Value < min || e.Value > max) {
            throw new EditException($"A {e.Kind} value is {min}…{max}, not {e.Value}.");
        }

        if (e.Kind is ControlKind.ControlChange or ControlKind.PolyPressure
                ? e.Number is < 0 or > 127
                : e.Number != 0) {
            throw new EditException("A controller number (a poly pressure's key) is 0…127, 0 for the other kinds.");
        }
    }

    static void CheckChannel(int channel) {
        if (channel is < 1 or > 16) {
            throw new EditException($"A MIDI channel is 1…16, not {channel}.");
        }
    }

    /// <summary>A position relative to <paramref name="region" />: where it sounds is inside the project.</summary>
    static void CheckPosition(MidiRegion region, long tick) {
        var at = region.StartTick + tick;
        if (tick is < -MaxTick or > MaxTick || at is < 0 or > MaxTick) {
            throw new EditException($"Tick {tick} of region '{region.Id}' is outside the project.");
        }
    }

    static void CheckLength(long length, string what) {
        if (length is < 1 or > MaxTick) {
            throw new EditException($"A {what} lasts 1…{MaxTick} ticks, not {length}.");
        }
    }

    static void CheckUnit(double value, string what) {
        if (!(value is >= 0 and <= 1)) {
            throw new EditException($"The {what} is 0…1, not {value}.");
        }
    }

    static void CheckCapacity(MidiRegion region, int adding) {
        if ((long)region.Notes.Count + region.Controls.Count + adding > MaxEventsPerRegion) {
            throw new EditException($"A MIDI region holds at most {MaxEventsPerRegion} notes and events.");
        }
    }

    static Dictionary<long, int> IndexById(Note[] notes) {
        var index = new Dictionary<long, int>(notes.Length);
        for (var i = 0; i < notes.Length; i++) {
            index[notes[i].Id] = i;
        }

        return index;
    }

    /// <summary>The ids named, each one of <paramref name="existing" />.</summary>
    static HashSet<long> Known(IEnumerable<long> existing, IReadOnlyList<long>? named, string what, string regionId) {
        if (named is not { Count: > 0 }) {
            throw new EditException($"Name at least one {what}.");
        }

        var have = existing.ToHashSet();
        var set = new HashSet<long>();
        foreach (var id in named) {
            if (!have.Contains(id)) {
                throw new EditException($"Region '{regionId}' has no {what} {id}.");
            }

            set.Add(id);
        }

        return set;
    }

    static Track InstrumentTrackOf(ProjectState state, string id) {
        var track = TrackOf(state, id);
        return track is { Kind: TrackKind.Instrument, Instrument: not null }
            ? track
            : throw new EditException($"Track '{id}' is not an instrument track.");
    }

    static MidiRegion MidiRegionOf(ProjectState state, string id) =>
        state.MidiRegions.TryGetValue(id ?? string.Empty, out var region)
            ? region
            : throw new EditException($"No MIDI region '{id}'.");
}

/// <summary>
///     Moves positions towards a grid (docs/03-phase-2.md, "Compilation"): by <c>strength</c> (0…1) of the way to the
///     nearest grid point, where swing (0…1) delays every second grid point by up to half a step. Shared by the
///     destructive <c>quantizeNotes</c> and the compiler's region playback.
/// </summary>
public static class Quantize {
    public static long Tick(long tick, long grid, double strength, double swing) {
        if (grid <= 0 || strength <= 0) {
            return tick;
        }

        var target = Nearest(tick, grid, swing);
        return tick + (long)Math.Round((target - tick) * strength, MidpointRounding.AwayFromZero);
    }

    /// <summary>The grid point nearest <paramref name="tick" />: <c>k·grid</c>, plus the swing on odd <c>k</c>.</summary>
    public static long Nearest(long tick, long grid, double swing) {
        var delay = (long)Math.Round(swing * grid / 2, MidpointRounding.AwayFromZero);
        var k = tick <= 0 ? 0 : tick / grid;
        var best = long.MaxValue;
        var bestDistance = long.MaxValue;
        for (var j = Math.Max(0, k - 1); j <= k + 1; j++) {
            var point = j * grid + ((j & 1) == 1 ? delay : 0);
            var distance = Math.Abs(point - tick);
            if (distance < bestDistance) {
                best = point;
                bestDistance = distance;
            }
        }

        return best;
    }
}

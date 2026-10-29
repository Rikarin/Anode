using Anode.Contracts;

namespace Anode.Model.Editing;

/// <summary>
///     The Drum Machine Designer (docs/07-phase-6.md): a stack with a <c>drumMachine</c> whose members are its pads,
///     each an instrument track on one note. The DMD holds the MIDI and pattern regions; the compiler routes their notes
///     to the pads by note.
/// </summary>
public static partial class Edits {
    /// <summary>The instrument a new pad plays unless told otherwise (workstream E's Drum Synth).</summary>
    public const string DrumSynthId = "anode.drum-synth";

    static List<DeltaOp> CreateDrumMachine(
        ProjectState state,
        CreateDrumMachineCommand c,
        IIdSource ids,
        PluginCatalog plugins,
        IPatchSource patches
    ) {
        var kit = c.KitPatchId is null
            ? null
            : patches.Find(c.KitPatchId) is { Pads: not null } found
                ? found
                : throw new EditException($"No drum kit '{c.KitPatchId}'.");
        var ops = new List<DeltaOp>();
        Track dmd;
        if (c.TrackId is null) {
            CheckTrackCount(state);
            dmd = NewTrack(
                state,
                ids,
                TrackKind.Stack,
                c.Name is null ? NextDrumMachineName(state) : Name(c.Name, "track name")
            ) with {
                DrumMachine = new([])
            };
            ops.Add(new TrackUpserted(dmd, state.Tracks.Count));
        } else {
            dmd = Converted(state, c, ops);
        }

        state = state.Apply(ops);
        foreach (var pad in kit?.Pads ?? []) {
            if (pad.Note is < 0 or > 127 || state.FindTrack(dmd.Id)!.DrumMachine!.Pads.Any(p => p.Note == pad.Note)) {
                continue;
            }

            var step = NewPad(state, state.FindTrack(dmd.Id)!, pad.Note, pad.Name, ids, plugins, pad);
            ops.AddRange(step);
            state = state.Apply(step);
        }

        return ops;
    }

    /// <summary>An instrument track turned into a Drum Machine Designer in place (its operations appended).</summary>
    static Track Converted(ProjectState state, CreateDrumMachineCommand c, List<DeltaOp> ops) {
        {
            var track = TrackOf(state, c.TrackId!);
            if (track.Kind != TrackKind.Instrument) {
                throw new EditException("Only an instrument track turns into a Drum Machine Designer.");
            }

            if (track.ParentId is not null) {
                throw new EditException("Stacks do not nest: take the track out of its stack first.");
            }

            if (track.FrozenFileId is not null || track.Alternatives.Count > 0) {
                throw new EditException(
                    $"Track '{track.Name}' is frozen or has alternatives: a Drum Machine Designer has neither."
                );
            }

            var strip = track.Strip;
            var dmd = track with {
                Kind = TrackKind.Stack,
                Name = c.Name is null ? track.Name : Name(c.Name, "track name"),
                Instrument = null,
                DrumMachine = new([]),
                SmartControls = null,
                Strip = ProjectDefaults.Strip with {
                    VolumeDb = strip.VolumeDb,
                    Pan = strip.Pan,
                    Mute = strip.Mute,
                    Solo = strip.Solo,
                    Output = strip.Output
                }
            };
            // Its lanes were of an instrument's strip; a stack's are not automated.
            ops.AddRange(state.LanesOf(track.Id).ToArray().Select(static l => new AutomationLaneRemoved(l.Id)));
            foreach (var other in state.Tracks.Where(o => o.Id != track.Id)) {
                if (Unlinked(other.Strip, track.Id) is var unlinked && !ReferenceEquals(unlinked, other.Strip)) {
                    ops.Add(new TrackUpserted(other with { Strip = unlinked }, state.IndexOfTrack(other.Id)));
                }
            }

            if (Unlinked(state.Master, track.Id) is var master && !ReferenceEquals(master, state.Master)) {
                ops.Add(new MasterChanged(master));
            }

            ops.Add(new TrackUpserted(dmd, state.IndexOfTrack(track.Id)));
            return dmd;
        }
    }

    static string NextDrumMachineName(ProjectState state) {
        for (var n = 1;; n++) {
            var name = n == 1 ? "Drum Machine" : $"Drum Machine {n}";
            if (!state.Tracks.Exists(t => t.Name == name)) {
                return name;
            }
        }
    }

    static List<DeltaOp> AddDrumPad(ProjectState state, AddDrumPadCommand c, IIdSource ids, PluginCatalog plugins) {
        var dmd = DrumMachineOf(state, c.TrackId);
        CheckPadNote(c.Note);
        if (dmd.DrumMachine!.Pads.Any(p => p.Note == c.Note)) {
            throw new EditException($"Note {c.Note} already has a pad.");
        }

        var instrument = NewInstrument(plugins, c.PluginId ?? DrumSynthId, c.PluginId is not null);
        return NewPad(state, dmd, c.Note, c.Name, ids, plugins, null, instrument);
    }

    /// <summary>A new pad track (after the DMD's last member) and the DMD with its pad.</summary>
    static List<DeltaOp> NewPad(
        ProjectState state,
        Track dmd,
        int note,
        string? name,
        IIdSource ids,
        PluginCatalog plugins,
        KitPad? kit,
        InstrumentSlot? instrument = null
    ) {
        CheckTrackCount(state);
        var padName = name is null ? NoteName(note) : Name(name, "pad name");
        instrument ??= kit is null
            ? NewInstrument(plugins, DrumSynthId, false)
            : kit.Instrument with { Parameters = PluginCatalog.Sorted(kit.Instrument.Parameters) };
        var track = NewTrack(state, ids, TrackKind.Instrument, padName) with {
            ParentId = dmd.Id,
            Instrument = instrument,
            Strip = kit is null
                ? ProjectDefaults.Strip
                : ProjectDefaults.Strip with {
                    Inserts = [
                        ..kit.Inserts.Take(MaxInserts)
                            .Select(i => i with {
                                    Id = ids.Next(IdPrefix.Insert),
                                    Parameters = PluginCatalog.Sorted(i.Parameters).Remove(HostTempoParameter),
                                    Sidechain = null
                                }
                            )
                    ]
                }
        };
        var pads = dmd.DrumMachine!.Pads.Append(new(note, padName, track.Id)).OrderBy(static p => p.Note);
        return [
            new TrackUpserted(dmd with { DrumMachine = new([..pads]) }, state.IndexOfTrack(dmd.Id)),
            new TrackUpserted(track, AfterStack(state, dmd.Id))
        ];
    }

    static List<DeltaOp> SetDrumPad(ProjectState state, SetDrumPadCommand c, PluginCatalog plugins) {
        var dmd = DrumMachineOf(state, c.TrackId);
        var pad = PadOf(dmd, c.Note);
        var padTrack = TrackOf(state, pad.TrackId);
        var changed = pad;
        var ops = new List<DeltaOp>();
        var track = padTrack;
        if (c.Name is not null) {
            var name = Name(c.Name, "pad name");
            changed = changed with { Name = name };
            track = track with { Name = name };
        }

        if (c.ToNote is { } to && to != pad.Note) {
            CheckPadNote(to);
            if (dmd.DrumMachine!.Pads.Any(p => p.Note == to)) {
                throw new EditException($"Note {to} already has a pad.");
            }

            changed = changed with { Note = to };
        }

        if (c.PluginId is not null) {
            CheckNotFrozen(padTrack);
            ops.AddRange(
                LanesFor(state, padTrack.Id, "instrument:").Select(static l => new AutomationLaneRemoved(l.Id))
            );
            track = track with { Instrument = NewInstrument(plugins, c.PluginId, true) };
        }

        var pads = dmd.DrumMachine!.Pads.Select(p => p.Note == pad.Note ? changed : p).OrderBy(static p => p.Note);
        ops.Add(new TrackUpserted(dmd with { DrumMachine = new([..pads]) }, state.IndexOfTrack(dmd.Id)));
        if (track != padTrack) {
            ops.Add(new TrackUpserted(track, state.IndexOfTrack(track.Id)));
        }

        return ops;
    }

    static List<DeltaOp> RemoveDrumPad(ProjectState state, RemoveDrumPadCommand c) {
        var dmd = DrumMachineOf(state, c.TrackId);
        var pad = PadOf(dmd, c.Note);
        var ops = new List<DeltaOp>();
        if (state.FindTrack(pad.TrackId) is not null) {
            RemoveOne(state, pad.TrackId, ops);
        } else {
            ops.Add(WithoutPad(state, dmd, pad.TrackId));
        }

        return ops;
    }

    /// <summary>The Drum Machine Designer whose pad <paramref name="trackId" /> is, or null.</summary>
    static Track? PadOwner(ProjectState state, string trackId) =>
        state.FindTrack(trackId) is { ParentId: { } parent }
        && state.FindTrack(parent) is { DrumMachine: { } machine } owner
        && machine.Pads.Any(p => p.TrackId == trackId)
            ? owner
            : null;

    /// <summary>The DMD without the pad of track <paramref name="trackId" />.</summary>
    static TrackUpserted WithoutPad(ProjectState state, Track dmd, string trackId) =>
        new(
            dmd with { DrumMachine = new([..dmd.DrumMachine!.Pads.Where(p => p.TrackId != trackId)]) },
            state.IndexOfTrack(dmd.Id)
        );

    static Track DrumMachineOf(ProjectState state, string? id) {
        var track = TrackOf(state, id ?? string.Empty);
        return track.DrumMachine is not null
            ? track
            : throw new EditException($"Track '{track.Name}' is not a Drum Machine Designer.");
    }

    static DrumPad PadOf(Track dmd, int note) =>
        dmd.DrumMachine!.Pads.FirstOrDefault(p => p.Note == note)
        ?? throw new EditException($"Drum machine '{dmd.Name}' has no pad on note {note}.");

    static void CheckPadNote(int note) {
        if (note is < 0 or > 127) {
            throw new EditException($"A pad's note is 0…127, not {note}.");
        }
    }

    /// <summary>A note's name as Logic writes it: C3 is 60.</summary>
    public static string NoteName(int note) {
        ReadOnlySpan<string> names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return $"{names[note % 12]}{note / 12 - 2}";
    }
}

/// <summary>A pad of a kit patch (a Drum Machine Designer's saved kit): its note, name, instrument and inserts.</summary>
public sealed record KitPad(int Note, string Name, InstrumentSlot Instrument, IReadOnlyList<Insert> Inserts);

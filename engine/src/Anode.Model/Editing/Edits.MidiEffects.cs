using Anode.Contracts;
using System.Collections.Immutable;

namespace Anode.Model.Editing;

/// <summary>
///     MIDI FX slots (docs/07-phase-6.md): the insert commands with <c>chain: midiEffects</c> edit an instrument or
///     external MIDI track's <c>midiEffects</c>, slots of the insert shape (never a sidechain) holding plugins of
///     category <c>MIDI Effect</c>; their parameters automate as <c>midiEffect:{slotId}:{parameterId}</c>.
/// </summary>
public static partial class Edits {
    /// <summary>The catalogue category of a MIDI effect (Anode.Dsp's <c>EffectCategories.MidiEffect</c>).</summary>
    public const string MidiEffectCategory = "MIDI Effect";

    const string MidiEffectPrefix = "midiEffect:";

    static bool IsMidiEffect(PluginInfo plugin) => plugin.Category == MidiEffectCategory;

    static Track MidiFxTrackOf(ProjectState state, string? trackId) {
        if (trackId is null) {
            throw new EditException("The master has no MIDI FX.");
        }

        var track = TrackOf(state, trackId);
        if (!IsMidiKind(track.Kind)) {
            throw new EditException($"A {Kind(track.Kind)} track has no MIDI FX.");
        }

        return track.FrozenFileId is not null
            ? throw new EditException($"Track '{track.Name}' is frozen: unfreeze it to change its plugins.")
            : track;
    }

    static TrackUpserted WithMidiEffects(ProjectState state, Track track, IReadOnlyList<Insert> slots) =>
        new TrackUpserted(
            SyncArpeggiator(track with { Strip = track.Strip with { MidiEffects = slots } }),
            state.IndexOfTrack(track.Id)
        );

    static List<DeltaOp> AddMidiEffect(ProjectState state, AddInsertCommand c, IIdSource ids, PluginCatalog plugins) {
        var track = MidiFxTrackOf(state, c.TrackId);
        if (string.IsNullOrWhiteSpace(c.PluginId) || plugins.Find(c.PluginId) is not { } plugin) {
            throw new EditException($"No plugin '{c.PluginId}'.");
        }

        if (!IsMidiEffect(plugin)) {
            throw new EditException($"Plugin '{plugin.Id}' is not a MIDI effect.");
        }

        var slots = track.Strip.MidiEffects;
        if (slots.Count >= MaxInserts) {
            throw new EditException($"A channel strip holds at most {MaxInserts} MIDI FX.");
        }

        var index = c.Index ?? slots.Count;
        if (index < 0 || index > slots.Count) {
            throw new EditException($"MIDI FX slot {index} is outside 0…{slots.Count}.");
        }

        var slot = new Insert(ids.Next(IdPrefix.Insert), plugin.Id, plugin.Name, false, PluginCatalog.Defaults(plugin), null);
        return [WithMidiEffects(state, track, slots.ToImmutableList().Insert(index, slot))];
    }

    static List<DeltaOp> RemoveMidiEffect(ProjectState state, RemoveInsertCommand c) {
        var track = MidiFxTrackOf(state, c.TrackId);
        var slot = MidiEffectOf(track, c.InsertId);
        var ops = new List<DeltaOp>();
        ops.AddRange(
            LanesFor(state, track.Id, $"{MidiEffectPrefix}{slot.Id}:").Select(static l => new AutomationLaneRemoved(l.Id))
        );
        ops.Add(WithMidiEffects(state, track, [..track.Strip.MidiEffects.Where(i => i.Id != slot.Id)]));
        return ops;
    }

    static List<DeltaOp> MoveMidiEffect(ProjectState state, MoveInsertCommand c) {
        var track = MidiFxTrackOf(state, c.TrackId);
        var slot = MidiEffectOf(track, c.InsertId);
        var slots = track.Strip.MidiEffects;
        if (c.Index < 0 || c.Index >= slots.Count) {
            throw new EditException($"MIDI FX slot {c.Index} is outside 0…{slots.Count - 1}.");
        }

        return [WithMidiEffects(state, track, slots.Where(i => i.Id != slot.Id).ToImmutableList().Insert(c.Index, slot))];
    }

    static List<DeltaOp> SetMidiEffect(ProjectState state, SetInsertCommand c) {
        var track = MidiFxTrackOf(state, c.TrackId);
        var slot = MidiEffectOf(track, c.InsertId);
        if (c.Sidechain is { Length: > 0 }) {
            throw new EditException("A MIDI FX slot takes no sidechain.");
        }

        var changed = slot with { Bypassed = c.Bypassed ?? slot.Bypassed };
        return [WithMidiEffects(state, track, [..track.Strip.MidiEffects.Select(i => i.Id == slot.Id ? changed : i)])];
    }

    static List<DeltaOp> SetMidiEffectParameter(
        ProjectState state,
        SetInsertParameterCommand c,
        PluginCatalog plugins
    ) {
        var track = MidiFxTrackOf(state, c.TrackId);
        var slot = MidiEffectOf(track, c.InsertId);
        var value = ParameterValue(plugins, slot.PluginId, slot.Parameters, c.ParameterId, c.Value, "MIDI effect");
        var changed = slot with { Parameters = PluginCatalog.Sorted(slot.Parameters).SetItem(c.ParameterId, value) };
        return [WithMidiEffects(state, track, [..track.Strip.MidiEffects.Select(i => i.Id == slot.Id ? changed : i)])];
    }

    static Insert MidiEffectOf(Track track, string? id) =>
        track.Strip.MidiEffects.FirstOrDefault(i => i.Id == id)
        ?? throw new EditException($"No MIDI FX slot '{id}' on track '{track.Name}'.");
}

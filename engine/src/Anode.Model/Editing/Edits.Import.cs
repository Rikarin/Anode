using Anode.Contracts;
using Anode.Model.Midi;
using Anode.Model.Time;

namespace Anode.Model.Editing;

/// <summary>Standard MIDI File import (docs/04-phase-3.md, "Files and MIDI import").</summary>
public static partial class Edits {
    /// <summary>
    ///     A MIDI file as one edit: the file's tempo and signature tracks replace the project's, and each of its tracks
    ///     with notes becomes an instrument track with the default synth (named after the MIDI track, else
    ///     <paramref name="name" />), holding one MIDI region from the bar of its first event to the bar line after its
    ///     last note.
    /// </summary>
    public static EditResult ImportMidi(
        ProjectState state,
        SmfFile file,
        string name,
        IIdSource ids,
        PluginCatalog? plugins = null
    ) {
        plugins ??= PluginCatalog.Empty;
        var tracks = file.Tracks.Where(static t => t.Notes.Count > 0).ToArray();
        if (tracks.Length == 0) {
            throw new EditException("The MIDI file has no notes.");
        }

        if (state.Tracks.Count + tracks.Length > MaxTracks) {
            throw new EditException($"A project holds at most {MaxTracks} tracks.");
        }

        var forward = new List<DeltaOp>();
        var inverse = new List<DeltaOp>();
        state = Step(state, SetTempoEvents(state, new(file.Tempo)), forward, inverse);
        state = Step(state, SetSignatures(state, new(file.Signatures)), forward, inverse);
        var bars = state.SignatureMap;
        var next = state.LastEventId;
        for (var i = 0; i < tracks.Length; i++) {
            var source = tracks[i];
            var trackName = source.Name is { Length: > 0 and <= MaxNameLength } own
                ? own
                : tracks.Length == 1 ? Name(name, "track name") : $"{Name(name, "track name")} {i + 1}";
            state = Step(
                state,
                AddTrack(state, new(trackName, Kind: TrackKind.Instrument), ids, plugins),
                forward,
                inverse
            );
            var track = state.Tracks[^1];
            var first = Math.Min(
                source.Notes.Min(static n => n.StartTick),
                source.Controls.Count == 0 ? long.MaxValue : source.Controls.Min(static e => e.Tick)
            );
            var start = bars.BarLineAtOrBefore(Math.Max(0, first));
            var end = bars.BarLineAtOrAfter(source.Notes.Max(static n => n.StartTick + n.LengthTicks));
            CheckTick(end, "region end");
            if ((long)source.Notes.Count + source.Controls.Count > MaxEventsPerRegion) {
                throw new EditException($"A MIDI region holds at most {MaxEventsPerRegion} notes and events.");
            }

            var notes = source.Notes.Select(n => n with { Id = ++next, StartTick = n.StartTick - start }).ToArray();
            var controls = source.Controls.Select(e => e with { Id = ++next, Tick = e.Tick - start }).ToArray();
            var region = new MidiRegion(
                ids.Next(IdPrefix.MidiRegion),
                track.Id,
                track.Name,
                start,
                Math.Max(end - start, SignatureMap.BarTicks(bars.At(start))),
                ProjectDefaults.Playback,
                Sorted(notes),
                Sorted(controls),
                false
            );
            state = Step(state, [new MidiRegionUpserted(region)], forward, inverse);
        }

        inverse.Reverse();
        return new(state, forward, inverse);
    }
}

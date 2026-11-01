using Anode.Contracts;
using Anode.Model;
using Anode.Model.Editing;
using Anode.Model.Groove;
using Anode.Model.Midi;

namespace Anode.Interchange.Smf;

/// <summary>
///     The Standard MIDI File project export (docs/10-phase-9.md, owned by workstream B): the project's mapping to a file,
///     here beside the other formats, over Anode.Model's byte-level writer
///     (<see cref="StandardMidiFileWriter.Write(SmfDocument)" />: tempo map, signatures, keys, markers, track names,
///     notes, controllers, pitch bend, channel pressure and programs).
/// </summary>
public static class SmfExport {
    /// <summary>
    ///     The file of <paramref name="request" /> for <paramref name="state" /> at 960 PPQ: format 0 or 1, the tracks or
    ///     region asked for, with regions' playback transpose, velocity and quantise applied as they play (unmuted regions,
    ///     cut at their ends). Throws <see cref="ArgumentException" /> for a format, track or region that is not one.
    /// </summary>
    public static byte[] Write(ProjectState state, MidiExportRequest request) =>
        StandardMidiFileWriter.Write(Document(state, request));

    /// <summary>What <see cref="Write" /> writes, before it is bytes.</summary>
    public static SmfDocument Document(ProjectState state, MidiExportRequest request) {
        if (request.Format is not (0 or 1)) {
            throw new ArgumentException($"A MIDI file is format 0 or 1, not {request.Format}.", nameof(request));
        }

        var tracks = new List<SmfTrack>();
        if (request.RegionId is { } regionId) {
            var region = state.MidiRegions.TryGetValue(regionId, out var r)
                ? r
                : throw new ArgumentException($"No MIDI region '{regionId}'.", nameof(request));
            tracks.Add(TrackOf(state, region.Name, [region]));
        } else {
            var chosen = request.TrackIds is { } ids
                ? ids.Distinct(StringComparer.Ordinal)
                    .Select(id => state.FindTrack(id ?? string.Empty) is { } t && Edits.HoldsMidi(t)
                            ? t
                            : throw new ArgumentException($"No MIDI track '{id}'.", nameof(request))
                    )
                : state.Tracks.Where(t => Edits.HoldsMidi(t) && state.MidiRegionsOf(t.Id).Any());
            tracks.AddRange(chosen.Select(t => TrackOf(state, t.Name, [..state.MidiRegionsOf(t.Id)])));
        }

        var settings = state.Settings;
        var map = state.SignatureMap;
        var end = tracks.SelectMany(static t => t.Notes.Select(static n => n.StartTick + n.LengthTicks))
            .Concat(tracks.SelectMany(static t => t.Controls.Select(static c => c.Tick)))
            .DefaultIfEmpty(0)
            .Max();
        return new(
            request.Format,
            settings.TempoEvents is { Count: > 0 } tempo ? tempo : [new(0, settings.Tempo, TempoCurve.Step)],
            [..Enumerable.Range(0, map.Count).Select(i => map[i])],
            settings.Keys ?? [],
            [
                ..state.Markers.Values.OrderBy(static m => m.Tick)
                    .ThenBy(static m => m.Id, StringComparer.Ordinal)
                    .Select(static m => new SmfMarker(m.Tick, m.Name))
            ],
            tracks,
            state.Name,
            end
        );
    }

    /// <summary>A file track of <paramref name="regions" /> as they play, at project ticks.</summary>
    static SmfTrack TrackOf(ProjectState state, string name, IReadOnlyList<MidiRegion> regions) {
        var notes = new List<Note>();
        var controls = new List<ControlEvent>();
        foreach (var region in regions.Where(static r => !r.Muted)) {
            var groove = region.Playback.GrooveId is { } grooveId ? state.Grooves.GetValueOrDefault(grooveId) : null;
            notes.AddRange(
                region.Notes.Where(n => n.StartTick >= 0 && n.StartTick < region.LengthTicks)
                    .Select(n => Played(region, groove, n))
            );

            controls.AddRange(
                region.Controls.Where(c => c.Tick >= 0 && c.Tick < region.LengthTicks)
                    .Select(c => c with { Tick = region.StartTick + c.Tick })
            );
        }

        return new(name, Edits.Sorted(notes), Edits.Sorted(controls));
    }

    /// <summary>
    ///     A note as it plays, at its project tick: transposed, its velocity offset, quantised to the region's grid or
    ///     groove, cut at the region's end.
    /// </summary>
    static Note Played(MidiRegion region, GrooveTemplate? groove, Note note) {
        var playback = region.Playback;
        var tick = region.StartTick + note.StartTick;
        var at = groove is null
            ? Quantize.Tick(tick, playback.QuantizeTicks, playback.QuantizeStrength, playback.Swing)
            : playback.QuantizeStrength <= 0
                ? tick
                : Grooves.Target(groove, tick, playback.QuantizeStrength).Tick;
        return note with {
            Pitch = Math.Clamp(note.Pitch + playback.Transpose, 0, 127),
            Velocity = Math.Clamp(note.Velocity + playback.Velocity, 1, 127),
            StartTick = Math.Max(0, at),
            LengthTicks = Math.Max(1, Math.Min(note.LengthTicks, region.StartTick + region.LengthTicks - tick))
        };
    }
}

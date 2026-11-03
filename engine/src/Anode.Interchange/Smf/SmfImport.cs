using Anode.Contracts;
using Anode.Model;
using Anode.Model.Editing;
using Anode.Model.Midi;
using Anode.Model.Time;
using System.Globalization;

namespace Anode.Interchange.Smf;

/// <summary>
///     The Standard MIDI File project import (docs/10-phase-9.md, owned by workstream B), over Anode.Model's reader
///     (<see cref="StandardMidiFile" />: notes, controllers, pitch bend, pressure, programs, track names, tempo map,
///     signatures, keys and markers).
/// </summary>
public static class SmfImport {
    /// <summary>
    ///     The <c>importTracks</c> content of <paramref name="file" /> for <paramref name="state" />: an instrument track
    ///     per file track with events (one region from its first bar to the bar after its last note or event), or one track
    ///     with everything when <c>merge</c>; the tempo map, signatures and keys when <c>importTempo</c>; the markers when
    ///     <c>importMarkers</c>. Ids are provisional (<see cref="ImportedContent" />). A file without notes or controller
    ///     events throws <see cref="FormatException" />.
    /// </summary>
    public static ImportedContent ToContent(SmfFile file, ProjectState state, MidiImportRequest request) {
        var name = Path.GetFileNameWithoutExtension(request.Path ?? string.Empty) is { Length: > 0 } stem
            ? stem
            : "MIDI";
        var sources = Sources(file, request.Merge, name);

        var bars = request.ImportTempo ? new SignatureMap(file.Signatures) : state.SignatureMap;
        long next = 0;
        var tracks = new List<ImportedTrack>();
        for (var i = 0; i < sources.Count; i++) {
            var source = sources[i];
            var trackName = source.Name is { Length: > 0 and <= Edits.MaxNameLength } own ? own
                : sources.Count == 1 ? Clip(name)
                : $"{Clip(name)} {(i + 1).ToString(CultureInfo.InvariantCulture)}";
            var id = $"smf-{(i + 1).ToString(CultureInfo.InvariantCulture)}";
            tracks.Add(
                new(id, trackName, TrackKind.Instrument, null, [Region(source, id, trackName, bars, ref next)], [])
            );
        }

        return new(
            tracks,
            request.ImportTempo ? file.Tempo : null,
            request.ImportTempo ? file.Signatures : null,
            request.ImportTempo && file.Keys.Count > 0 ? file.Keys : null,
            request.ImportMarkers && file.Markers.Count > 0 ? Markers(file) : null
        );
    }

    /// <summary>
    ///     A file track's region: from the bar line at or before its first event to the one after its last note or
    ///     event (a bar at least), notes and events numbered on from <paramref name="next" />.
    /// </summary>
    static MidiRegion Region(SmfTrack source, string trackId, string name, SignatureMap bars, ref long next) {
        var first = source.Notes.Select(static n => n.StartTick)
            .Concat(source.Controls.Select(static c => c.Tick))
            .Min();
        var last = source.Notes.Select(static n => n.StartTick + n.LengthTicks)
            .Concat(source.Controls.Select(static c => c.Tick + 1))
            .Max();
        var start = bars.BarLineAtOrBefore(Math.Max(0, first));
        var end = bars.BarLineAtOrAfter(last);
        var notes = new List<Note>(source.Notes.Count);
        foreach (var note in source.Notes) {
            notes.Add(note with { Id = ++next, StartTick = note.StartTick - start });
        }

        var controls = new List<ControlEvent>(source.Controls.Count);
        foreach (var control in source.Controls) {
            controls.Add(control with { Id = ++next, Tick = control.Tick - start });
        }

        return new(
            $"{trackId}-region",
            trackId,
            name,
            start,
            Math.Max(end - start, SignatureMap.BarTicks(bars.At(start))),
            ProjectDefaults.Playback,
            Edits.Sorted(notes),
            Edits.Sorted(controls),
            false,
            ProjectDefaults.Notation
        );
    }

    /// <summary>The file tracks with notes or events, or all of them merged into one (named after the first named).</summary>
    static List<SmfTrack> Sources(SmfFile file, bool merge, string name) {
        var sources = file.Tracks.Where(static t => t.Notes.Count > 0 || t.Controls.Count > 0).ToList();
        if (sources.Count == 0) {
            throw new FormatException("The MIDI file has no notes or controller events.");
        }

        return merge
            ? [
                new(
                    sources.Select(static t => t.Name).FirstOrDefault(static n => n is { Length: > 0 }) ?? name,
                    [..sources.SelectMany(static t => t.Notes)],
                    [..sources.SelectMany(static t => t.Controls)]
                )
            ]
            : sources;
    }

    static Marker[] Markers(SmfFile file) => [
        ..file.Markers.Select(static (m, i) => new Marker(
                $"smf-marker-{(i + 1).ToString(CultureInfo.InvariantCulture)}",
                Clip(m.Name),
                m.Tick,
                0,
                null,
                string.Empty,
                MarkerKind.Marker
            )
        )
    ];

    static string Clip(string name) => name.Length <= Edits.MaxNameLength ? name : name[..Edits.MaxNameLength];
}

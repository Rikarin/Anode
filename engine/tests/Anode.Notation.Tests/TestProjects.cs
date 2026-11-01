using Anode.Contracts;
using Anode.Model;
using Anode.Model.Editing;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Anode.Notation.Tests;

/// <summary>Hand-made projects for the notation tests: MIDI tracks, regions of notes, signatures, keys and markers.</summary>
sealed class TestProject {
    readonly SequentialIds ids = new();
    long nextRegion;

    public TestProject() => State = ProjectDefaults.NewProject("prj_test", "Test", 48_000);

    public ProjectState State { get; private set; }

    /// <summary>A MIDI track with a staff style (null: the default, piano).</summary>
    public string Track(string name, string? styleId = null) {
        State = Edits.Apply(State, new AddTrackCommand(name, Kind: TrackKind.Midi), ids).State;
        var track = State.Tracks[^1] with { StaffStyleId = styleId };
        State = State with { Tracks = State.Tracks.SetItem(State.Tracks.Count - 1, track) };
        return track.Id;
    }

    /// <summary>A region on <paramref name="trackId" /> from <paramref name="start" /> holding notes (ticks relative to it).</summary>
    public string Region(string trackId, long start, long length, RegionNotation? notation, params Note[] notes) {
        var id = $"mid_{++nextRegion:D3}";
        var region = new MidiRegion(
            id,
            trackId,
            id,
            start,
            length,
            ProjectDefaults.Playback,
            Edits.Sorted(notes),
            [],
            false,
            notation ?? RegionNotation.Default
        );
        State = State with { MidiRegions = State.MidiRegions.SetItem(id, region) };
        return id;
    }

    public TestProject Signatures(params SignatureEvent[] signatures) {
        State = State with { Settings = State.Settings with { Signatures = signatures, Signature = new(signatures[0].Numerator, signatures[0].Denominator) } };
        return this;
    }

    public TestProject Keys(params KeyEvent[] keys) {
        State = State with { Settings = State.Settings with { Keys = keys } };
        return this;
    }

    public TestProject Tempos(params TempoEvent[] tempos) {
        State = State with { Settings = State.Settings with { TempoEvents = tempos, Tempo = tempos[0].Bpm } };
        return this;
    }

    public TestProject Marker(string id, string name, long tick) {
        State = State with { Markers = State.Markers.SetItem(id, new(id, name, tick, 0, null, string.Empty)) };
        return this;
    }

    public static Note N(long id, int pitch, long start, long length) => new(id, pitch, 80, 1, start, length, 0);

    public static RegionNotation Grid(int straight, int triplet = 0) => RegionNotation.Default with { DisplayQuantizeTicks = straight, TripletQuantizeTicks = triplet };
}

/// <summary>A compact text of a score view for assertions: one line per measure, staff and voice.</summary>
static class Describe {
    /// <summary>
    ///     <c>m{n} s{staff} v{voice}: events</c>, an event <c>{pitches}/{value}{dots}</c>: pitches written (<c>C4</c>,
    ///     <c>F#4</c>, a chord <c>C4+E4</c>, a rest <c>r</c>), the value (<c>w h q 8 16 32 64</c>), <c>.</c> per dot, then
    ///     <c>t{group}</c> in a tuplet, <c>~</c> tied to the next, <c>b{group}</c> beamed, <c>!</c> before a pitch with a
    ///     drawn accidental.
    /// </summary>
    public static string View(ScoreView view, int part = 0) {
        var text = new StringBuilder();
        var p = view.Parts[part];
        for (var m = 0; m < view.Measures.Count; m++) {
            for (var s = 0; s < p.Staves.Count; s++) {
                foreach (var voice in p.Measures[m].Staves[s].Voices) {
                    text.Append(CultureInfo.InvariantCulture, $"m{view.Measures[m].Number} s{s} v{voice.Number}:");
                    foreach (var e in voice.Events) {
                        text.Append(' ').Append(Event(e));
                    }

                    text.Append('\n');
                }
            }
        }

        return text.ToString();
    }

    public static string Event(ScoreEvent e) {
        var pitches = e.Kind == ScoreEventKind.Rest ? "r" : string.Join('+', e.Notes.Select(Pitch));
        var value = e.Duration.Value switch {
            NoteValue.Breve => "b",
            NoteValue.Whole => "w",
            NoteValue.Half => "h",
            NoteValue.Quarter => "q",
            NoteValue.Eighth => "8",
            NoteValue.Sixteenth => "16",
            NoteValue.ThirtySecond => "32",
            NoteValue.SixtyFourth => "64",
            _ => "128"
        };
        var text = $"{pitches}/{value}{new string('.', e.Duration.Dots)}";
        if (e.Duration.Tuplet is { } tuplet) {
            text += $"t{tuplet.Group}";
        }

        if (e.Notes.Any(n => n.TieStart)) {
            text += "~";
        }

        if (e.Beam is { } beam) {
            text += $"b{beam}";
        }

        return text;
    }

    public static string Pitch(ScoreNote note) {
        var alter = note.Written.Alter switch {
            1 => "#",
            2 => "x",
            -1 => "b",
            -2 => "bb",
            _ => string.Empty
        };
        return $"{(note.Accidental is null ? string.Empty : "!")}{note.Written.Step.ToString().ToUpperInvariant()}{alter}{note.Written.Octave}";
    }
}

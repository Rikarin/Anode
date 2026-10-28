using Anode.Contracts;
using Anode.Model.Editing;
using Anode.Model.Midi;

namespace Anode.Model.Tests;

/// <summary>Standard MIDI File reading and import (docs/04-phase-3.md, "Files and MIDI import").</summary>
public sealed class MidiFileTests {
    [Fact]
    public void A_type_1_file_reads_its_tracks_tempo_and_signatures_at_960_ppq() {
        var file = StandardMidiFile.Read(TestMidi.Type1());
        Assert.Equal(1, file.Format);
        Assert.Equal(3, file.Tracks.Count);
        Assert.Empty(file.Tracks[0].Notes); // the conductor track
        Assert.Equal("Piano", file.Tracks[1].Name);
        // 480 PPQ in the file: a quarter at tick 480 is 960 here.
        Assert.Equal(
            [(60, 100, 1, 0L, 960L), (64, 90, 1, 960L, 960L)],
            file.Tracks[1].Notes.Select(static n => (n.Pitch, n.Velocity, n.Channel, n.StartTick, n.LengthTicks))
        );
        Assert.Equal(
            [(ControlKind.ControlChange, 64, 127, 0L), (ControlKind.PitchBend, 0, 0, 480L)],
            file.Tracks[1].Controls.Select(static c => (c.Kind, c.Number, c.Value, c.Tick))
        );
        Assert.Equal(10, file.Tracks[2].Notes[0].Channel);
        Assert.Equal([new TempoEvent(0, 100, TempoCurve.Step), new TempoEvent(3840, 150, TempoCurve.Step)], file.Tempo);
        Assert.Equal([new SignatureEvent(0, 4, 4), new SignatureEvent(3840, 3, 4)], file.Signatures);
    }

    [Fact]
    public void A_type_0_file_without_tempo_or_signature_is_120_in_4_4() {
        var file = StandardMidiFile.Read(TestMidi.Type0());
        Assert.Equal(0, file.Format);
        Assert.Single(file.Tracks);
        Assert.Equal([new TempoEvent(0, 120, TempoCurve.Step)], file.Tempo);
        Assert.Equal([new SignatureEvent(0, 4, 4)], file.Signatures);
        // Running status and a note-on at velocity 0 as the note-off.
        Assert.Equal(
            [(60, 0L, 960L), (62, 960L, 960L)],
            file.Tracks[0].Notes.Select(static n => (n.Pitch, n.StartTick, n.LengthTicks))
        );
    }

    [Fact]
    public void Damaged_and_unsupported_files_are_refused() {
        Assert.Throws<FormatException>(static () => StandardMidiFile.Read("RIFF0000WAVE"u8));
        var type0 = TestMidi.Type0();
        Assert.Throws<FormatException>(() => StandardMidiFile.Read(type0.AsSpan(0, type0.Length - 6)));
        var smpte = (byte[])type0.Clone();
        smpte[12] = 0xE7; // −25 fps
        Assert.Throws<FormatException>(() => StandardMidiFile.Read(smpte));
        var type2 = (byte[])type0.Clone();
        type2[9] = 2;
        Assert.Throws<FormatException>(() => StandardMidiFile.Read(type2));
    }

    [Fact]
    public void Importing_makes_an_instrument_track_per_track_with_notes_and_the_files_tempo_in_one_step() {
        var ids = new SequentialIds();
        var state = ProjectDefaults.NewProject("prj_1", "Song", 48_000);
        var before = RandomProjects.Json(state);
        var result = Edits.ImportMidi(
            state,
            StandardMidiFile.Read(TestMidi.Type1()),
            "Song",
            ids,
            RandomProjects.Plugins
        );
        var imported = result.State;
        Assert.Equal(["Piano", "Song 2"], imported.Tracks.Select(static t => t.Name));
        Assert.All(
            imported.Tracks,
            static t => Assert.Equal(("anode.synth", TrackKind.Instrument), (t.Instrument!.PluginId, t.Kind))
        );
        Assert.Equal(100, imported.Settings.Tempo);
        Assert.Equal(2, imported.Settings.TempoEvents.Count);
        Assert.Equal(2, imported.Settings.Signatures.Count);
        var piano = imported.MidiRegionsOf(imported.Tracks[0].Id).Single();
        Assert.Equal((0L, 3840L), (piano.StartTick, piano.LengthTicks));
        Assert.Equal([1L, 2], piano.Notes.Select(static n => n.Id));
        // The drums start in bar 2 (after the 4/4 bar): their region starts there, in 3/4.
        var drums = imported.MidiRegionsOf(imported.Tracks[1].Id).Single();
        Assert.Equal((3840L, 2880L), (drums.StartTick, drums.LengthTicks));
        Assert.Equal(0, drums.Notes[0].StartTick);
        Assert.Equal(before, RandomProjects.Json(imported.Apply(result.Inverse)));
    }
}

/// <summary>Standard MIDI Files built byte by byte.</summary>
static class TestMidi {
    /// <summary>
    ///     Format 1, 480 PPQ: a conductor track (100 BPM, 4/4; at bar 2, 150 BPM and 3/4), "Piano" (two notes, a sustain
    ///     pedal and a pitch bend) and an unnamed drum track on channel 10 starting at bar 2.
    /// </summary>
    public static byte[] Type1() => [
        ..Header(1, 3, 480),
        ..Track(
            [
                0x00, 0xFF, 0x51, 0x03, 0x09, 0x27, 0xC0, // 600 000 µs: 100 BPM
                0x00, 0xFF, 0x58, 0x04, 0x04, 0x02, 0x18, 0x08,
                0x8F, 0x00, 0xFF, 0x51, 0x03, 0x06, 0x1A, 0x80, // +1920: 400 000 µs, 150 BPM
                0x00, 0xFF, 0x58, 0x04, 0x03, 0x02, 0x18, 0x08,
                0x00, 0xFF, 0x2F, 0x00
            ]
        ),
        ..Track(
            [
                0x00, 0xFF, 0x03, 0x05, (byte)'P', (byte)'i', (byte)'a', (byte)'n', (byte)'o',
                0x00, 0xB0, 0x40, 0x7F,
                0x00, 0x90, 0x3C, 0x64,
                0x81, 0x70, 0xE0, 0x00, 0x40, // +240: centred pitch bend
                0x81, 0x70, 0x80, 0x3C, 0x00, // +240
                0x00, 0x90, 0x40, 0x5A,
                0x83, 0x60, 0x80, 0x40, 0x00, // +480
                0x00, 0xFF, 0x2F, 0x00
            ]
        ),
        ..Track(
            [
                0x8F, 0x00, 0x99, 0x24, 0x70, // +1920, channel 10
                0x83, 0x60, 0x89, 0x24, 0x00,
                0x00, 0xFF, 0x2F, 0x00
            ]
        )
    ];

    /// <summary>Format 0, 96 PPQ: two notes in running status, the first ended by a note-on at velocity 0.</summary>
    public static byte[] Type0() => [
        ..Header(0, 1, 96),
        ..Track([0x00, 0x90, 0x3C, 0x64, 0x60, 0x3C, 0x00, 0x00, 0x3E, 0x50, 0x60, 0x3E, 0x00, 0x00, 0xFF, 0x2F, 0x00])
    ];

    static byte[] Header(int format, int tracks, int division) => [
        (byte)'M', (byte)'T', (byte)'h', (byte)'d', 0, 0, 0, 6, 0, (byte)format, 0, (byte)tracks, (byte)(division >> 8),
        (byte)division
    ];

    static byte[] Track(byte[] events) => [
        (byte)'M', (byte)'T', (byte)'r', (byte)'k',
        (byte)(events.Length >> 24), (byte)(events.Length >> 16), (byte)(events.Length >> 8), (byte)events.Length,
        ..events
    ];
}

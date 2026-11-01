using Anode.Contracts;
using Anode.Interchange.MusicXml;
using static Anode.Interchange.Tests.MusicXml.Views;

namespace Anode.Interchange.Tests.MusicXml;

/// <summary>
///     Random two-staff views (quarters, eighths, halves, dotted values, triplets, rests, ties across bar lines, chords and
///     a second voice): every export validates and reads back as the same notes.
/// </summary>
public sealed class MusicXmlPropertyTests {
    [Fact]
    public void Random_views_validate_and_read_back_as_the_same_notes() {
        for (var seed = 1; seed <= 40; seed++) {
            var (view, expected) = RandomView(new(seed));
            var file = MusicXmlWriter.Write(view, seed % 2 == 0);
            MusicXmlSchema.AssertValid(file);
            var content = MusicXmlReader.Read(file);
            Assert.Equal(expected.Order(), MusicXmlRoundTripTests.Notes(content.Tracks[0].MidiRegions[0]));
            Assert.Equal(file, MusicXmlWriter.Write(view, seed % 2 == 0));
        }
    }

    static (ScoreView View, List<(int Pitch, long Tick, long Length)> Notes) RandomView(Random random) {
        const int bars = 4;
        var nextId = 1L;
        var expected = new List<(int Pitch, long Tick, long Length)>();
        var measures = new List<ScoreMeasure>();
        var partMeasures = new List<ScorePartMeasure>();
        (int Pitch, long NoteId, int ExpectedIndex)? tie = null;
        for (var b = 0; b < bars; b++) {
            var start = b * 3840L;
            measures.Add(Measure(b + 1, start, barline: b == bars - 1 ? Barline.Final : Barline.Single));
            var events = new List<ScoreEvent>();
            var tick = start;
            var tuplet = 0;
            var beam = 0;
            while (tick < start + 3840) {
                var left = start + 3840 - tick;
                var choice = random.Next(left >= 1920 ? 6 : 4);
                if (tie is { } open && events.Count == 0) {
                    // The tied note continues as a quarter.
                    events.Add(Event(tick, 960, NoteValue.Quarter, [Note(open.NoteId, open.Pitch, 2, tieStop: true)]));
                    var e = expected[open.ExpectedIndex];
                    expected[open.ExpectedIndex] = e with { Length = e.Length + 960 };
                    tie = null;
                    tick += 960;
                    continue;
                }

                switch (choice) {
                    case 0:
                        events.Add(Rest(tick, 960, NoteValue.Quarter));
                        tick += 960;
                        break;
                    case 1:
                        beam++;
                        for (var i = 0; i < 2; i++) {
                            var pitch = random.Next(60, 84);
                            events.Add(Event(tick, 480, NoteValue.Eighth, [Note(nextId++, pitch)], beam: beam));
                            expected.Add((pitch, tick, 480));
                            tick += 480;
                        }

                        break;
                    case 2: {
                        tuplet++;
                        beam++;
                        for (var i = 0; i < 3; i++) {
                            var pitch = random.Next(60, 84);
                            events.Add(Event(tick, 320, NoteValue.Eighth, [Note(nextId++, pitch)], tuplet: new(tuplet, 3, 2), beam: beam));
                            expected.Add((pitch, tick, 320));
                            tick += 320;
                        }

                        break;
                    }
                    case 3: {
                        // A quarter chord, tied into the next bar when it ends this one.
                        var low = random.Next(55, 70);
                        var ties = tick + 960 == start + 3840 && b < bars - 1;
                        var lowId = nextId++;
                        events.Add(Event(tick, 960, NoteValue.Quarter, [Note(lowId, low, tieStart: ties), Note(nextId++, low + 4)]));
                        expected.Add((low, tick, 960));
                        if (ties) {
                            tie = (low, lowId, expected.Count - 1);
                        }

                        expected.Add((low + 4, tick, 960));
                        tick += 960;
                        break;
                    }
                    case 4: {
                        var pitch = random.Next(60, 84);
                        events.Add(Event(tick, 1440, NoteValue.Quarter, [Note(nextId++, pitch)], 1));
                        expected.Add((pitch, tick, 1440));
                        var second = random.Next(60, 84);
                        events.Add(Event(tick + 1440, 480, NoteValue.Eighth, [Note(nextId++, second)]));
                        expected.Add((second, tick + 1440, 480));
                        tick += 1920;
                        break;
                    }
                    default: {
                        var pitch = random.Next(60, 84);
                        events.Add(Event(tick, 1920, NoteValue.Half, [Note(nextId++, pitch)]));
                        expected.Add((pitch, tick, 1920));
                        tick += 1920;
                        break;
                    }
                }
            }

            var voices = new List<ScoreVoice> { Voice(1, [..events]) };
            if (random.Next(2) == 0) {
                var pitch = random.Next(52, 60);
                voices.Add(Voice(2, Event(start + 1920, 1920, NoteValue.Half, [Note(nextId++, pitch)], stem: StemDirection.Down)));
                expected.Add((pitch, start + 1920, 1920));
            }

            var bass = random.Next(36, 52);
            var bassVoice = Voice(1, Event(start, 3840, NoteValue.Whole, [Note(nextId++, bass)]));
            expected.Add((bass, start, 3840));
            partMeasures.Add(Bar(new ScoreStaffMeasure(b == 0 ? Clef.Treble : null, voices), new ScoreStaffMeasure(b == 0 ? Clef.Bass : null, [bassVoice])));
        }

        var part = Part("trk_p", "Piano", [new(Clef.Treble, 0, 60, 2), new(Clef.Bass, 0, null, 1)], partMeasures, StaffStyleIds.Piano);
        return (View(measures, [part]), expected);
    }
}

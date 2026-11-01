using Anode.Contracts;
using Anode.Interchange.Aaf;
using Anode.Model;
using Anode.Model.Editing;
using Anode.Notation;
using Anode.Server.Projects;
using System.Text;

namespace Anode.Server.Tests;

/// <summary>
///     A stand-in notation engine (docs/10-phase-9.md, workstream A) for the score API and the MusicXML export: one
///     measure, a part per track with an event per note (element ids <c>n{noteId}</c>), the stored content passed
///     through; its MEI lists the ids. Counts its renders.
/// </summary>
sealed class FakeNotation : INotation {
    int renders;

    public int Renders => Volatile.Read(ref renders);

    public IReadOnlyList<StaffStyle> FactoryStyles => Model.Tests.ScoreNotationDouble.Styles;

    public ScoreView Render(ProjectState state, ScoreRequest request) {
        Interlocked.Increment(ref renders);
        var tracks = request.PartTrackId is { } part
            ? [state.FindTrack(part) ?? throw new ArgumentException($"No track '{part}'.")]
            : request.SetId is { } set
                ? state.ScoreSets.TryGetValue(set, out var s)
                    ? s.TrackIds.Select(id => state.FindTrack(id)!)
                    : throw new ArgumentException($"No set '{set}'.")
                : state.Tracks.Where(Edits.HoldsMidi);
        var parts = tracks.Select(t => new ScorePart(
                    t.Id,
                    t.Name,
                    t.Name,
                    t.StaffStyleId ?? StaffStyleIds.Default,
                    0,
                    false,
                    [new(Clef.Treble, 0, null, 1)],
                    [
                        new(
                            [
                                new(
                                    Clef.Treble,
                                    [
                                        new(
                                            1,
                                            [
                                                ..state.MidiRegionsOf(t.Id)
                                                    .SelectMany(r => r.Notes.Select(n => Event(state, r, n)))
                                            ]
                                        )
                                    ]
                                )
                            ]
                        )
                    ]
                )
            )
            .ToArray();
        return new(
            request.SetId,
            request.PartTrackId,
            request.Mode,
            request.PartTrackId is not null || state.ScoreLayout.TransposingScore,
            state.ScoreLayout,
            [new(1, 0, 3840, new(4, 4), new(0, KeyMode.Major), true, true, [], [], Barline.Final)],
            parts,
            [..state.ScoreSymbols.Values],
            [..state.Lyrics.Values],
            [..state.ChordSymbols.Values]
        );
    }

    static ScoreEvent Event(ProjectState state, MidiRegion region, Note note) =>
        new(
            ScoreIds.Note(note.Id),
            ScoreEventKind.Note,
            region.StartTick + note.StartTick,
            note.LengthTicks,
            new(NoteValue.Quarter, 0, null),
            [
                new(
                    ScoreIds.Note(note.Id),
                    note.Id,
                    note.Pitch,
                    new(PitchStep.C, 0, 4),
                    0,
                    null,
                    false,
                    false,
                    Notehead.Normal
                )
            ],
            StemDirection.Up,
            null,
            [..state.ScoreSymbols.Values.Where(s => s.NoteId == note.Id).Select(static s => s.Id)],
            [..state.Lyrics.Values.Where(l => l.NoteId == note.Id).Select(static l => l.Id)],
            []
        );

    public string Mei(ScoreView view) =>
        "<mei>"
        + string.Join(
            " ",
            view.Parts.SelectMany(static p => p.Measures)
                .SelectMany(static m => m.Staves)
                .SelectMany(static s => s.Voices)
                .SelectMany(static v => v.Events)
                .Select(static e => e.Id)
        )
        + "</mei>";
}

/// <summary>
///     Stand-ins for the MusicXML (D) and AAF (E) readers and writers: small text files that say what they hold, a WAV
///     extracted for an AAF clip, content of one track each. <see cref="Throw" /> makes every member throw.
/// </summary>
sealed class FakeFormats : IInterchangeFormats {
    public Exception? Throw { get; set; }

    public ScoreView? Written { get; private set; }

    public byte[] WriteMusicXml(ScoreView view, bool compressed) {
        Fail();
        Written = view;
        return Encoding.UTF8.GetBytes(
            $"<score-partwise parts=\"{view.Parts.Count}\" compressed=\"{compressed}\" title=\"{view.Layout.Title}\"/>"
        );
    }

    public ImportedContent ReadMusicXml(byte[] data) {
        Fail();
        if (!Encoding.UTF8.GetString(data).StartsWith("<score-partwise", StringComparison.Ordinal)) {
            throw new FormatException("Not a MusicXML file.");
        }

        return new(
            [
                new(
                    "P1",
                    "Flute",
                    TrackKind.Instrument,
                    StaffStyleIds.Treble,
                    [
                        new(
                            "R1",
                            "P1",
                            "Flute",
                            0,
                            3840,
                            ProjectDefaults.Playback,
                            [new(1, 72, 80, 1, 0, 960, 0), new(2, 74, 80, 1, 960, 960, 0)],
                            [],
                            false,
                            ProjectDefaults.Notation
                        )
                    ],
                    []
                )
            ],
            [new(0, 80, TempoCurve.Step)],
            [new(0, 3, 4)],
            null,
            null,
            new(
                [],
                [new("S1", ScoreSymbolKind.Staccato, "P1", 1)],
                [new("L1", 2, 1, "la", Syllabic.Single, false)],
                [],
                "Imported",
                null
            )
        );
    }

    public IReadOnlyList<string> WriteAaf(
        ProjectState state,
        string projectFolder,
        AafExportRequest request,
        string path
    ) {
        Fail();
        File.WriteAllText(path, $"AAF {string.Join(",", request.TrackIds ?? [])}");
        if (request.EmbedMedia) {
            return [path];
        }

        var media = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + " Media");
        Directory.CreateDirectory(media);
        var file = state.Files.Values.First();
        var copy = Path.Combine(media, Path.GetFileName(file.Path));
        File.Copy(Path.Combine(projectFolder, file.Path), copy, true);
        return [path, copy];
    }

    public AafComposition ReadAaf(string path, string mediaFolder) {
        Fail();
        if (!File.ReadAllText(path).StartsWith("AAF", StringComparison.Ordinal)) {
            throw new FormatException("Not an AAF file.");
        }

        Directory.CreateDirectory(mediaFolder);
        var wav = TestWav.Write(mediaFolder, "Dialog Clip.wav", frames: 24_000);
        return new(
            "Composition",
            48_000,
            [new("Dialog", [new("Clip", wav, 48_000, 0, 24_000, -3, 100, 100)])],
            [new("Hit", 96_000, 0, "here")]
        );
    }

    public ImportedContent AafContent(
        AafComposition composition,
        ProjectState state,
        IReadOnlyDictionary<string, string> fileIds
    ) {
        Fail();
        var clip = composition.Tracks[0].Clips[0];
        return new(
            [
                new(
                    "T1",
                    composition.Tracks[0].Name,
                    TrackKind.Audio,
                    null,
                    [],
                    [
                        new(
                            "C1",
                            "T1",
                            fileIds[clip.MediaPath],
                            clip.Name,
                            1920,
                            clip.SourceOffset,
                            clip.Length,
                            clip.GainDb,
                            clip.FadeIn,
                            clip.FadeOut,
                            0,
                            0,
                            false,
                            FlexFollow.Off,
                            [],
                            0,
                            0,
                            []
                        )
                    ]
                )
            ],
            Markers: [new("M1", composition.Markers[0].Name, 3840, 0, null, composition.Markers[0].Comment)]
        );
    }

    void Fail() {
        if (Throw is { } e) {
            throw e;
        }
    }
}

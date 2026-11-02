# 10 — Phase 9: notation and interchange

From [the plan, §12](01-implementation-plan.md#phase-9-notation-and-interchange-10-weeks), §6 (`libs/notation`) and §11
(the Score Editor, notation and export rows marked 9). The renderer follows [ADR-0011](adr/0011-notation-rendering.md).

**Exit criterion.** MIDI reads and edits as a score that prints, and projects move to and from other software through
MusicXML, AAF and Standard MIDI Files. The Playwright golden path covers it on the null device:
1. **Score.** Open a piano region in the Score Editor: chords, a tie across a bar line, a triplet, a two-voice passage,
   and notes slightly off the grid. With the piano staff style (split at C3) and display quantise 1/16:
   - the score shows exactly the expected notes, rests, ties, the triplet, beams and the two voices (checked through
     the server's score model, and that every note id has an element in the rendered SVG);
   - display quantise 1/8 changes the score and leaves the MIDI unchanged.
2. **Editing in the score.**
   - Drag a note up one step: its pitch moves to the next note of the key.
   - Insert a quarter note from the Part box at beat 3 of bar 2.
   - Delete a note.
   Each is one undo step, and the Piano Roll shows the same notes.
3. **Symbols and text.**
   - Add a staccato, an accent, a dynamic (mf), a slur over three notes and a crescendo.
   - Enter lyrics, one syllable per note, with a hyphenated word.
   - Add chord symbols (C, Am7, Fmaj7, G7).
   All of them render at their notes or positions and survive save and reopen.
4. **Staff styles and score sets.**
   - Give a second track the B♭ clarinet staff style (transposing): its part shows notes a major second above concert
     pitch, and the full score shows concert pitch when "Transposing score" is off.
   - Make a score set of both tracks, and extract the clarinet part.
5. **Print.** Print the score set to PDF: the expected number of pages, with the title, the instrument names, the
   lyrics and the chord symbols as text in the PDF.
6. **MusicXML.**
   - Export the score set: the file validates against the MusicXML 4.0 schema.
   - Re-import it into a new project: the same notes, ties, tuplets, dynamics, articulations, slurs, lyrics and chord
     symbols.
   - Import a hand-written multi-part fixture (strings quartet, key and meter changes): four tracks with their notes.
7. **AAF.**
   - Export an arrangement of audio regions (with offsets, gain, fades and a crossfade) as AAF with embedded media, and
     as AAF with linked media: an independent reader (pyaaf2) finds every clip at its position, source offset and
     length to the sample, the fades as transitions, and the media.
   - Import an AAF fixture made by pyaaf2: the tracks, regions and media come back, positioned to the sample.
8. **Standard MIDI Files.** Export the project as a type 1 file (tempo map, signatures, markers, track names, CCs) and
   import it into a new project: the same events, tempo map and markers.
9. Save, close and reopen: everything is intact.

The contracts below are committed code. Each workstream builds against them and changes them only with a note in its
report. A REST change means regenerating `engine/src/Anode.Server/openapi.json`
(`ANODE_UPDATE_OPENAPI=1 dotnet test`) and the editor client (`nx run api-client:generate`).

## Scope

**In:**
- **Notation engine** (`Anode.Notation`): MIDI to notation, as Logic's score does it.
  - Display quantise per region (and per track's staff style as the default): note and rest grids, with triplets.
  - Interpretation (shorten notes to tidy rests), no overlap, syncopation, max dots.
  - Durations as tied notes across beats and bars; tuplets; rests; beaming by time signature; stem directions.
  - Voices: by the staff style's split point (piano), and polyphonic voices in one staff (by overlap).
  - Pitch spelling from the key signature (and a project key); clefs; transposition.
  - Key, time signature and tempo marks from the project; bar numbers; markers as rehearsal marks.
- **Score model** stored with the project:
  - **Staff styles:** one or more staves, each with clef, transposition, split point, voices, and display quantise
    defaults. Factory styles: treble, bass, piano, drums (percussion, a drum map), guitar (treble 8vb), B♭ clarinet,
    B♭ trumpet, F horn, alto sax, viola, and others.
  - **Symbols:** articulations, dynamics, slurs, ties shown or hidden, crescendo/diminuendo, text, tempo text, pedal,
    trills, fermatas, octave lines; attached to a note (by id) or to a position (tick) on a track.
  - **Lyrics** per note (syllables, hyphen and extender) in verses; **chord symbols** at positions, with an optional
    chord grid.
  - **Score sets:** named sets of tracks; **parts** from a set; **layout:** page size, margins, staff size, title,
    composer, header and footer, instrument names (full and short), and "transposing score".
- **Score Editor** (C): linear and page views; select, drag (pitch by key step, position by display grid), insert
  from the Part box, delete; symbols and text from the Part box; lyrics entry (Tab moves to the next note); chord
  symbol entry; staff style editor; score set and layout windows; print and PDF export.
- **MusicXML** 4.0, `score-partwise`: export from the score model (notes, rests, voices, ties, tuplets, beams, clefs,
  keys, times, transposition, articulations, dynamics, slurs, wedges, lyrics, harmony, text, tempo, rehearsal); import
  into tracks and regions with the score symbols. `.musicxml` and compressed `.mxl`.
- **AAF** (Avid's edit interchange; the Pro Tools and Logic subset): export the arrangement's audio tracks as a
  composition with clips, offsets, gain, fades and crossfades as transitions, markers, with media embedded or linked
  (WAV in a folder beside); import the same. MIDI is not in AAF here (SMF covers it).
- **Standard MIDI Files:** export the project (type 0 or 1: tempo map, signatures, markers, track names, notes, CCs,
  pitch bend, program changes), selected tracks or a region; import with options (tempo map and markers into the
  project, one track per file track, or merge). The existing reader and writer are extended.

**Out:**
- Tablature (later), guitar chord diagrams beyond chord grids, percussion beyond a drum map.
- Score playback articulations (the articulation sets of Phase 7 cover sound).
- OMF, AAF video, AAF volume and pan automation (clip gain only), AAF MIDI.
- Optical music recognition.
- Score editing of audio.

## Contracts

| What | Where |
|---|---|
| Score model: staff styles, symbols, lyrics, chord symbols, score sets, layout; a track's staff style; a region's display quantise and interpretation | `engine/src/Anode.Contracts/Score.cs`; `Project.Score`, `Track.StaffStyleId`, `MidiRegion.Notation` |
| The rendered score: an engraving model (systems of measures, staves, voices, events with note ids, symbols) as the notation engine's output; MEI text for Verovio with `xml:id`s that map back | `engine/src/Anode.Contracts/Score.cs` (`ScoreView`) |
| Interchange requests and jobs: MusicXML, AAF and SMF export and import, with options | `engine/src/Anode.Contracts/Interchange.cs`; `Jobs.cs` |
| New commands: staff styles, symbols, lyrics, chord symbols, score sets, layout, region notation settings, score edits (notes by staff position), interchange import results | `engine/src/Anode.Contracts/Commands.cs` |
| `/ws`: nothing new (score views are fetched; deltas say when to refetch) | — |
| REST (stubbed): `GET /project/score?set=&part=&view=linear|page` (the `ScoreView` and MEI); `/library/staff-styles`; `/project/export/musicxml|aaf|midi` and `/project/import/musicxml|aaf|midi` (jobs, with files as in the bounce and import endpoints) | `engine/src/Anode.Server/Api/ScoreApi.cs`, `InterchangeApi.cs` → `openapi.json` → `@anode/api-client` |
| Notation engine (stubs that throw): MIDI → `ScoreView`; `ScoreView` → MEI; staff position ↔ pitch | `engine/src/Anode.Notation/` (new project, references Contracts and Model) |
| Interchange (stubs that throw): MusicXML reader/writer, AAF reader/writer, SMF project export/import | `engine/src/Anode.Interchange/` (new project; references Contracts, Model and Notation) |
| Editor: `libs/notation` (Verovio in a worker: MEI → SVG pages; element id ↔ note id) | `editor/libs/notation` (new library, stubbed) |

### Rules the contracts imply

- **The score is a view:** notes live in MIDI regions only. The notation engine derives the score each time from
  notes, the staff style, the region's notation settings and the project's time, key and tempo. Symbols, lyrics and
  chord symbols are the only stored score content.
- **Attachment:** a note-attached symbol or lyric follows its note (by note id) and disappears with it; a positioned
  symbol (dynamic, wedge, text, chord symbol) has a track and a tick. Slurs and wedges span two notes or two ticks.
- **Determinism:** the same project gives the same `ScoreView` and the same MEI, byte for byte. Element ids in MEI are
  `n{noteId}` for notes and `s{symbolId}` for symbols, so the editor maps SVG back to the model without the server.
- **Display quantise** moves where notes are drawn, never the notes. Score edits are made in display positions and
  converted back: dragging a note horizontally moves its MIDI position by the display grid; vertically it moves by a
  step of the key (with accidentals by modifier keys).
- **Transposition:** a staff style's transposition changes written pitch in parts (and in the full score when
  "Transposing score" is on); MIDI stays at concert pitch.
- **Pitch names** follow Logic in the UI (C3 is 60); MusicXML and MEI use their own octave numbering (C4 is 60).
- **Interchange:**
  - Imports create tracks and regions in one undoable step; their media are copied into the project.
  - Exports are jobs that write files and report them, like the bounce.
  - AAF positions and lengths are in samples at the project rate (edit rate = sample rate); clip gain in dB; fades as
    `Transition`s on the timeline (crossfades) or as fade lengths (Avid's `FadeIn`/`FadeOut` properties).
  - MusicXML divisions are chosen per file to express every duration exactly.
  - SMF export uses 960 PPQ, the project's own resolution.

### Contract notes

Where the committed code differs from, or adds to, the shapes planned for this commit, and the shapes chosen where
the plan left latitude:
- **Staff styles** (`Score.cs`): `Clef` (`treble | bass | alto | tenor | treble8vb | treble8va | bass8vb | percussion
  | none`); `Staff(clef, transpose, splitPitch?, voices)`: `transpose` in semitones, written = concert + transpose (an
  octave clef's octave is the clef's, not the transposition); with several staves (top to bottom) a note goes to the
  first staff whose `splitPitch` (concert MIDI pitch) it reaches, the last staff's is null; `voices` 1…4 is the most
  voices made by overlap on the staff. `DrumMapEntry(pitch, staffPosition, notehead, name)` with `Notehead` (`normal |
  cross | circleCross | diamond | triangle | slash`). `StaffStyle(id, name, staves, drumMap?, displayQuantizeTicks,
  tripletQuantizeTicks, brace)`: the display quantise defaults are **two grids** (straight and triplet, ticks; 0 none),
  not a single "default quantise", and `brace` (piano) is added for the MEI `staffGrp`. Factory ids are
  **`StaffStyleIds`** (Contracts; the data is A's `FactoryStaffStyles`): `factory.treble`, `factory.bass`,
  `factory.piano` (treble and bass split at C3 = 60, braced), `factory.drums`, `factory.guitar` (treble 8vb),
  `factory.bassGuitar` (bass 8vb), `factory.clarinetBb` (+2), `factory.trumpetBb` (+2), `factory.hornF` (+7),
  `factory.altoSax` (+9), `factory.tenorSax` (+14), `factory.viola` (alto clef), `factory.cello`; `FactoryPrefix`
  `factory.` (a project's own style ids, chosen by the client, may not use it), `All` (library order), and **`Default`
  = `factory.piano`**: a track's `staffStyleId` null means the piano style (Logic's "Auto Grand"), not a range-based
  automatic choice.
- **Region notation:** `RegionNotation(displayQuantizeTicks, tripletQuantizeTicks, interpretation, noOverlap,
  syncopation, maxDots)`; the brief's "triplets/allowTuplets" is the **triplet grid** in ticks (160 a 1/16 triplet; 0
  none), as Logic's "16,24" settings are two grids. `displayQuantizeTicks` **0 means the staff style's grids** (both),
  which is how a staff style's display quantise is "the default". `RegionNotation.Default` (Logic's): 0, 0,
  interpretation on, no overlap on, syncopation off, 1 dot; `ProjectDefaults.Notation` in the model.
  `MidiRegion.notation` is required (every region has one, as in Logic's inspector); `Track.staffStyleId` is
  nullable, left out when null.
- **Symbols:** `ScoreSymbolKind` (`staccato | staccatissimo | accent | marcato | tenuto | fermata | trill | tieHidden |
  dynamic | text | tempoText | rehearsal | pedalDown | pedalUp | slur | crescendo | diminuendo | octaveUp |
  octaveDown`): dynamics are **one kind** with the marking in `text` (one of `ScoreSymbols.Dynamics`: pppp … ffff, sf,
  sfz, sffz, sfp, fp, fz, rf, rfz), and "ties shown or hidden" is `tieHidden` on the note whose ties are not drawn.
  `ScoreSymbol(id, kind, trackId, noteId?, tick?, endNoteId?, endTick?, text?, placement?, staff?)` with
  `ScorePlacement` (`above | below`; null the engine's choice); `staff` (added) puts a positioned symbol on one staff of
  a multi-staff style. Ticks are **project** ticks (symbols belong to tracks, not regions). The anchor rules are data
  in `ScoreSymbols`: `NoteOnly` (articulations, fermata, trill, tieHidden), `Spanning` (slur, wedges, octave lines; a
  slur spans notes only), `WithText` (dynamic, text, tempoText, rehearsal); other kinds take a tick or a note;
  `MaxTextLength` 200.
- **Lyrics and chords:** `LyricSyllable(id, noteId, verse, text, syllabic, extender)` with `Syllabic` (`single | begin
  | middle | end`), at most one per note and verse (no `trackId`: note ids are project-wide). `ChordSymbol(id,
  trackId, tick, root, quality, bass?, showGrid)` with **`PitchName(step, alter)`** and `PitchStep` (`c` … `b`) for
  the root and bass (spelled, as MusicXML's `root-step`/`root-alter` need); `quality` is the text after the root ("",
  "m", "7", "maj7", "m7", "dim", "m7b5", "aug", "sus4", "6", "9"…), from which D derives MusicXML's `kind`.
- **Sets and layout:** `ScoreSet(id, name, trackIds)` (ids chosen by the client). `ScoreLayout(pageWidthMm,
  pageHeightMm, margins: ScoreMargins(top, bottom, left, right), staffHeightMm, title, subtitle, composer, header,
  footer, transposingScore, instrumentNames: InstrumentName(trackId, full, short)[])`: the page is **millimetres**
  rather than a named page size (the dialog maps presets), texts are "" when unset, and instrument names list only
  the tracks whose names differ from the track's. `ScoreLayout.Default`: A4 portrait (210 × 297), 15 mm margins, 7 mm
  staves, concert pitch. One layout per project (not per set).
- **`ProjectScore(staffStyles, symbols, lyrics, chords, sets, layout)`** with `ProjectScore.Empty`;
  `Project.score` is required.
- **`ScoreView`** (the engine's output): `ScoreView(setId?, partTrackId?, mode, transposing, layout, measures, parts,
  symbols, lyrics, chords)` with `ScoreViewMode` (`linear | page`). Measures are common to every part:
  `ScoreMeasure(number, startTick, lengthTicks, time: TimeSignature, key: ScoreKey(fifths, mode), showTime, showKey,
  tempos: ScoreTempoMark(tick, bpm)[], rehearsals: ScoreRehearsal(tick, markerId, text)[], barline)` with `Barline`
  (`single | double | final`). `ScorePart(trackId, name, shortName, staffStyleId, transpose, brace, staves: Staff[],
  measures: ScorePartMeasure[])` → `ScorePartMeasure(staves: ScoreStaffMeasure[])` → `ScoreStaffMeasure(clef?,
  voices: ScoreVoice[])` → `ScoreVoice(number, events)` → `ScoreEvent(id, kind, tick, durationTicks, duration, notes,
  stem, beam?, symbolIds, lyricIds, chordIds)` with `ScoreEventKind` (`note | chord | rest`), `ScoreDuration(value:
  NoteValue, dots, tuplet: ScoreTuplet(group, actual, normal)?)`, `NoteValue` (`breve | whole | half | quarter |
  eighth | sixteenth | thirtySecond | sixtyFourth | hundredTwentyEighth`), `StemDirection` (`none | up | down`), and
  `ScoreNote(id, noteId, pitch, written: ScorePitch(step, alter, octave), staffPosition, accidental?, tieStart,
  tieStop, notehead)` with `Accidental` (`natural | sharp | flat | doubleSharp | doubleFlat`). Beams and tuplets are
  **group numbers** (1… within a measure's voice) rather than begin/continue/end flags. Ticks are display positions in
  project ticks. Every list is ordered, so the view serialises deterministically.
  - **Deviation:** there are **no systems** in the view: the linear view is one system and page breaks are Verovio's
    (ADR-0011: the engine fixes only what is musical), so the brief's "systems of measures" is measures only.
  - **Deviation:** the MEI is **not a property of `ScoreView`**: `GET /project/score` answers **`ScoreDocument(view,
    mei)`**, so `MeiWriter.Write(view)` stays a pure function of the view.
- **Element ids** (`ScoreIds`, implemented in Contracts and mirrored, implemented, in `@anode/notation`): `n{noteId}`
  for a note's first segment and **`n{noteId}-{k}`** for its k-th tied segment (k ≥ 2; ids must be unique in MEI),
  `c{lowest noteId}` (with the same suffix) for a chord, `r{trackId}-{measure}-{staff}-{voice}-{index}` for a rest,
  `s{symbolId}` for a symbol, **`l{lyricId}`** for a syllable, **`h{chordId}`** for a chord symbol, `m{number}` for a
  measure; `NoteIdOf` and `SymbolIdOf` read them back (the editor's `noteIdOf`, `symbolIdOf`, `lyricIdOf`,
  `chordSymbolIdOf`). Symbol ids are `sym_…`, so their element is `ssym_…`.
- **Model ids:** `IdPrefix.StaffStyle` `sts`, `ScoreSymbol` `sym`, `Lyric` `lyr`, `ChordSymbol` `chd`, `ScoreSet`
  `scs` (staff styles and sets are client-chosen, but the editor should use these prefixes).
- **Commands** (17): `setStaffStyle(style)`, `deleteStaffStyle(styleId)` (tracks using it return to null),
  `assignStaffStyle(trackIds, styleId?)`, `setRegionNotation(regionIds, displayQuantizeTicks?, tripletQuantizeTicks?,
  interpretation?, noOverlap?, syncopation?, maxDots?)` (several regions at once, as the inspector edits a selection),
  `addScoreSymbols(symbols: NewScoreSymbol[])` (the server gives ids; they come back in `scoreSymbolUpserted`),
  `editScoreSymbols(symbols: ScoreSymbol[])`, `deleteScoreSymbols(symbolIds)`, **`setLyrics(verse, syllables:
  LyricChange(noteId, text, syllabic, extender)[])`** (an empty text removes the syllable; one command for lyrics entry,
  a note's syllable keeps its id), `addChordSymbols(chords: NewChordSymbol[])`, `editChordSymbols(chords)`,
  `deleteChordSymbols(chordIds)`, `setScoreSet(set)`, `deleteScoreSet(setId)`, `setScoreLayout(layout)` (whole
  layout), and the score edits **`moveScoreNotes(regionId, noteIds, steps, ticks = 0, accidental?)`** (vertical by
  diatonic steps in the written key, horizontal by MIDI ticks the editor computes from the display grid) and
  **`addScoreNote(trackId, tick, lengthTicks, staffPosition, staff = 0, accidental?, velocity?, regionId?)`**
  (velocity 80 by default; a new region of whole bars when the track has none at the tick). The Part box's **rests
  are not commands**: rests are derived (no user rests in the model). Staff positions are diatonic steps above the
  bottom line (0 the bottom line, 8 the top), as MEI's `loc`. Since the model cannot reference `Anode.Notation`, B
  converts the score edits with A's `StaffPositions` in the session (as step input fills `stepNotes`) or gives the
  model a notation helper interface, as it prefers; the semantics are fixed.
- **Import result:** one command, **`importTracks(content: ImportedContent)`**, applied by all three import jobs (the
  Phase 3 `Edits.ImportMidi` is not a command and does not carry score content or audio). `ImportedContent(tracks:
  ImportedTrack[], tempoEvents?, signatures?, keys?, markers?, score: ImportedScore?)`, `ImportedTrack(id, name, kind,
  staffStyleId?, midiRegions: MidiRegion[], regions: AudioRegion[])` (instrument or audio), `ImportedScore(staffStyles,
  symbols, lyrics, chords, title?, composer?)`: every id in the content is provisional (unique within it), and the model
  gives real ids and rewrites the references (an imported track's `staffStyleId` is a factory id, kept, or one of the
  content's styles); audio regions name files the job added first; tempo, signatures and keys replace the project's
  when present; the title and composer fill the layout's when empty. A nullable object property
  (`ImportedContent.score`, `ChordSymbol.bass`) is required but nullable in the generated client: send `null`.
- **Deltas:** `staffStyleUpserted(style)`, `staffStyleRemoved(id)`, `scoreSymbolUpserted(symbol)`,
  `scoreSymbolRemoved(id)`, `lyricUpserted(lyric)`, `lyricRemoved(id)`, `chordSymbolUpserted(chord)`,
  `chordSymbolRemoved(id)`, `scoreSetUpserted(set)`, `scoreSetRemoved(id)`, `scoreLayoutChanged(layout)`; a track's
  style comes as `trackUpserted`, a region's notation as `midiRegionUpserted`. The editor's `ProjectStore` keeps
  `score` and applies them.
- **Interchange** (`Interchange.cs`, `Jobs.cs`): `JobKind` `musicXmlExport | musicXmlImport | aafExport | aafImport |
  midiExport | midiImport`. Exports take a file as the bounce does: `MusicXmlExportRequest(fileName, directory?,
  setId?, partTrackId?, compressed = false)` (`partTrackId` added, for an extracted part), `AafExportRequest(fileName,
  directory?, trackIds?, embedMedia = true)` (linked media go to `<fileName> Media/` beside the file),
  `MidiExportRequest(fileName, directory?, format = 1, trackIds?, regionId?)`; the extension is added when missing,
  the directory defaults to **`<project>/Exports`**, and the job's `files` are what it wrote. Imports take a path on
  the server's disk, `MusicXmlImportRequest(path)`, `AafImportRequest(path)`, `MidiImportRequest(path, importTempo =
  true, importMarkers = true, merge = false)`, or an upload (multipart `file`; the MIDI options as query parameters),
  as audio import offers both. `POST /project/files/import-midi` (Phase 3) stays.
- **REST** (`ScoreApi.cs`, `InterchangeApi.cs`, `Pending.Phase9`, a new `Pending.cs` since B had removed Phase 8's):
  `GetScore` (`GET /project/score?set=&part=&view=`, `ScoreDocument`; `view` is a string, `linear` or `page`, as query
  binding does not parse contract enum names), `ListStaffStyles` (`GET /library/staff-styles`), `ExportMusicXml`,
  `ExportAaf`, `ExportMidi` (`POST /project/export/musicxml|aaf|midi`), `ImportMusicXml`, `ImportAaf`, `ImportMidi`
  (`POST /project/import/musicxml|aaf|midi`) and `UploadMusicXml`, `UploadAaf`, `UploadMidi`
  (`POST /project/import/…/upload`), all answering `Accepted<Job>` except the two `GET`s. The editor's `AnodeApi`:
  `score(query: ScoreQuery)`, `staffStyles`, `exportMusicXml`, `exportAaf`, `exportMidi`, `importMusicXml`,
  `uploadMusicXml`, `importAaf`, `uploadAaf`, `importMidi`, `uploadMidi(file, options?: MidiImportOptions)` (`importMidi`
  is the job; `importMidiFile` the Phase 3 import), through the generated client in `HttpAnodeApi`; the fake Server
  answers them 422. `models.ts` mirrors every new schema, and `StaffStyleIds`, `DefaultRegionNotation`,
  `DefaultScoreLayout` and `EmptyProjectScore` as values.
- **Stubs that throw:**
  - `Anode.Notation` (`Pending.WorkstreamA()`; references Contracts and Model): `NotationEngine.Render(state, score,
    ScoreRequest(setId?, partTrackId?, mode))` → `ScoreView` (the score is passed in until the model stores it);
    `MeiWriter.Write(view)` → MEI 5 text; `StaffPositions.PitchAt(clef, transpose, key, staffPosition, accidental?)`,
    `PlacementOf(clef, transpose, key, pitch)` → `StaffPlacement(staffPosition, written)`, `Step(pitch, steps,
    transpose, key, accidental?)`; `DisplayGrid.GridsOf(notation, style)`, `DisplayTick(state, region, style, tick)`,
    `Move(state, region, style, tick, steps)` (grids counted from each bar line); `FactoryStaffStyles.All`,
    `Find(id)`, `Resolve(score, styleId)`.
  - `Anode.Interchange` (references Contracts, Model and Notation): `MusicXml.MusicXmlWriter.Write(view, compressed)`
    → bytes (from the view alone: it carries the layout, symbols, lyrics and chords; `Extension(compressed)` is real),
    `MusicXml.MusicXmlReader.Read(data)` → `ImportedContent` (`.mxl` told by content) (`Pending.WorkstreamD()`);
    `Aaf.AafWriter.Write(state, projectFolder, request, path)` → files written, `Aaf.AafReader.Read(path, mediaFolder)`
    → `AafComposition(name, sampleRate, tracks: AafTrack(name, clips)[], markers: AafMarker(name, position, length,
    comment)[])` with `AafClip(name, mediaPath, position, sourceOffset, length, gainDb, fadeIn, fadeOut)` in samples
    (embedded essence extracted to WAVs in the job's scratch folder; a transition becomes an overlap with fades), and
    `AafReader.ToContent(composition, state, fileIds)` → `ImportedContent` (`Pending.WorkstreamE()`): E converts
    samples to ticks, so the B↔E seam is the composition and the content.
  - **SMF:** the project mapping lives in **`Anode.Interchange/Smf`** (`SmfExport.Write(state, request)` → bytes,
    `SmfImport.ToContent(file: SmfFile, state, request)` → `ImportedContent`; `Pending.WorkstreamB()`), owned by B and
    calling `Anode.Model.Midi`'s `StandardMidiFileWriter` and `StandardMidiFile`, which B extends at the byte level
    (meta events, markers, keys, programs). Model's codec stays where loops and the Phase 3 import use it; the server's
    jobs call one place for every format.
- **Editor:** `libs/notation` (`@anode/notation`, tag `type:ui`): `NotationRenderer` (`render(mei, RenderOptions(view,
  layout, zoom?))` → `RenderedPage(page, svg, width, height)[]`, `dispose()`), `PendingNotationRenderer` (rejects), and
  the id map above, implemented with tests mirroring the engine's.
- **Projects:** `Anode.Notation` and `Anode.Interchange` are referenced by `Anode.Server` (not by `Anode.Host`);
  `Anode.Notation.Tests` and `Anode.Interchange.Tests` hold one placeholder test each.
- **Interim:** schema-8 packages read through **`Phase9Fields.Fill`** (every MIDI region's `notation` at the default,
  on tracks, in track alternatives and in Live Loops cells; the package's region normaliser and a cell's content
  default a missing one too), which B replaces with schema 9 and its migration; `Project.score` is
  `ProjectScore.Empty` (not stored yet). The model refuses the 17 commands, and a Live Loops cell whose region has
  notation other than the default (422, `Edits.Phase9.cs`); the identity property skips
  `RandomProjects.PendingPhase9`, and random projects are built without those commands (`RandomProjects.Buildable`),
  so they stay Phase 8's and the other properties keep their odds (each pending command picked during a build would be
  a wasted step, as `LoadInsertFile`'s flaky count in b9f7db4 showed). The editor's fake Server starts projects with
  the empty score, gives regions the default notation, and answers the new commands and endpoints 422
  (`fake-phase9.spec.ts`).

### Integration notes

How the seams were settled when the workstreams met (the contracts above stand; these refine them):
- **Real classes through the server:** the server's own registrations (`EngineNotation`, `EngineScoreNotation`,
  `InterchangeFormats`) are covered by `Phase9EngineTests` beside B's tests with doubles: the score API and library,
  score edits, a score set through MusicXML into a new project, AAF export and import to the sample.
- **Factory staff styles:** D's reader matches a part against A's `FactoryStaffStyles` (clefs, no clef reading as
  treble, and the transposition; of two styles with the same staves the one whose name's last word the part's name
  holds: bass or cello, clarinet or trumpet). Every factory style round-trips through MusicXML.
- **MusicXML from the engine:** every vector of `ScoreVectors.json` and A's golden views are written, validated against
  the 4.0 XSD and read back (`MusicXmlEngineTests`): notes come back where the score draws them, with their staff style,
  and render to the same events.
- **AAF at another rate:** the import job resamples media at another rate to the project's (the bounce's
  `SampleRateConverter`, as the Audio File Editor's rate conversion) before `AudioImport.Bring`; `ToContent` scales
  positions and keeps a region inside its file. Media at the project's rate are copied as before.
- **Housekeeping:** `AafMetaModel.g.cs` carries pyaaf2's copyright and MIT notice (and ADR-0008 says so); `*.aaf` is
  binary in `.gitattributes`; Interchange's `Pending` is gone.
- **MEI in Verovio:** checked with the real Verovio on A's golden MEI (ADR-0011, "Checked at integration"); nothing in
  the MEI changed. The renderer sets `condense: none`, so the k-th `g.staff` of a measure is always the k-th staff of
  the view.
- **Transposing scores:** `moveScoreNotes` reads steps of the written key and a written accidental, as A said. In a
  concert-pitch score of a transposing staff the editor turns the concert pitch a drag reaches into written steps and
  accidental (`writtenMove`); the editor's spelling mirrors A's `Spelling.Spell` (naturals before sharps, minor keys'
  raised degrees), which insert positions and drag targets use.
- **Instrument names:** a part's default names are the track's name and its first five characters
  (`NotationEngine.DefaultShortName`, the editor's rule); an empty `InstrumentName.full` or `short` is unset. MusicXML's
  `part-abbreviation` is that short name.
- **Golden path:** the piano passage is A's exit-criterion case (the "exit-criterion" vector), and step 1's "display
  quantise 1/8" is Logic's "8,12" (480/320, the "exit-criterion-eighths" vector), which draws bar 3's sixteenths as
  E4/8 F4+G4/8; with the 1/16-triplet grid kept (480/160) they would be drawn as triplets. A crossfade is an overlap
  with matching fades (no `setCrossfade` command). Helpers: `scripts/aaf-check.py read|make` (pyaaf2),
  `scripts/musicxml-validate.py` (xmllint), `pdftotext`; the e2e global setup makes the pyaaf2 venv.

## Workstreams

Five, in parallel, each on its own branch; they meet only at the contracts above.

### A — Notation engine (`Anode.Notation`)

- **MIDI to `ScoreView`:** display quantise with triplets, interpretation, no overlap, syncopation, max dots; tied
  durations across beats and bars; tuplets; rests; beaming by meter; stems; voices by split point and by overlap;
  spelling by key; clefs and transposition; measures, systems for the linear view and page breaking from the layout
  (with Verovio doing final engraving, A decides only what is musically fixed: which notes, durations, voices,
  measures).
- **MEI:** `ScoreView` to MEI 5 with the id scheme above, the layout as `scoreDef`, symbols as control events, lyrics
  as `verse`/`syl`, chord symbols as `harm`.
- **Score edits:** staff position ↔ pitch for a clef and key; display position ↔ tick.
- **Factory staff styles** as data.
- **Tests:** golden `ScoreView`s and MEI for hand-made cases (ties, triplets, dotted, syncopation, two voices,
  transposition, key changes); determinism; property tests (every note appears once; durations in a measure sum to the
  meter); MEI validates against the MEI 5 schema (RNG, if a validator is available; else structural checks).
- Write the shared vectors `engine/tests/Anode.Notation.Tests/ScoreVectors.json` (MIDI in, expected events out);
  workstreams C and D test against them.

### B — Server domain (`Anode.Model`, `Anode.Server`)

- **Model:** every new command with validation and inverses; package schema 9 with a migration from 8; remove the
  `PendingPhase9` placeholders. Symbols and lyrics attached to notes go with them when notes are deleted (and come back
  on undo).
- **Score API:** `GET /project/score` through A's engine, cached per project version and set; library staff styles.
- **Interchange jobs:** export and import for MusicXML, AAF and SMF through D's and E's readers and writers; files
  written and media copied as the bounce and audio import do.
- **SMF** (owned by B): extend the reader and writer to the project export (type 0 and 1, tempo map, signatures,
  markers, names, CCs, pitch bend, programs) and import options.
- **Tests:** properties for every command; migration; score API; jobs with D's and E's stubs replaced by test doubles
  until they merge; SMF round trips; API tests.
- **As built:**
  - The score content is stored in the snapshot by id (`ProjectState.StaffStyles`, `ScoreSymbols`, `Lyrics`,
    `ChordSymbols`, `ScoreSets`, `ScoreLayout`; `Project.score` lists each by id) and in `project.json`'s `score`
    (schema 9; the migration from 8 gives regions the default notation and the project the empty score).
  - **Attachment** is enforced after every edit, whatever made it: `Edits.Step` follows operations that remove or move
    tracks, MIDI regions or notes with `Edits.Detached` (a note-attached symbol or syllable whose note is gone is
    removed, one whose note moved to another track follows it, a slur or wedge whose notes are on two tracks goes, a
    removed track's positioned symbols and chord symbols go and it leaves score sets and instrument names). A note in
    one of its track's other alternatives still counts, so symbols survive switching alternatives. The removals are
    ordinary deltas in the same undo step, so undo brings them back.
  - **Score edits** are converted in the model through `IScoreNotation` (factory styles, `PitchAt`, `Step`), which
    `Edits.Apply` takes like the analysis source: the server passes `EngineScoreNotation` (A's `FactoryStaffStyles`
    and `StaffPositions`), tests a stand-in. So `moveScoreNotes` and `addScoreNote` are commands in history as
    themselves (one undo step each, labelled as such), and the identity property covers them. Without a notation
    source the model refuses them. The staff of a note comes from the style's split points; the key from the key track
    at the note; a percussion staff takes its drum map's pitch at the position.
  - **Staff styles have no user library** in Phase 9: `GET /library/staff-styles` lists the factory styles; a
    project's own styles live in its score (`setStaffStyle`), and the editor copies between projects if it wants to.
  - `GET /project/score` is cached per project opening and revision and request (set, part, view), at most 16
    documents per revision.
  - The interchange jobs reach D and E through `IInterchangeFormats` and A through `INotation` (server seams with
    doubles in the tests). A MusicXML import into a project with tracks drops the file's tempo, meter and key. An
    upload is saved in `<project>/.upload-<id>/` under its own name for the job, which removes it. A job that throws
    anything unexpected now fails itself instead of stopping the queue.

### C — Editor (`editor/`, `libs/notation`; built with `editor/DESIGN.md`)

- **`libs/notation`:** Verovio (WASM, the `verovio` npm package, LGPL-3.0, loaded as its own file so it stays
  replaceable) in a Web Worker: MEI → SVG per page and for the linear view; the id map; options from the layout.
  Measure the spike criteria of ADR-0011 and record them.
- **Score Editor:** linear and page views; selection, drag, insert from the Part box (notes, rests, symbols,
  dynamics, text), delete; lyrics entry; chord symbol entry; follow the playhead; open from a region or track.
- **Windows:** staff styles, score sets, layout (page, title, instruments), region notation settings in the inspector.
- **Print and PDF:** the page view printed through Electron's `printToPDF` on desktop, `window.print` elsewhere.
- **Import and export dialogs** for MusicXML, AAF and MIDI in the File menu.
- **Fake server:** every new endpoint, with a small notation stand-in so the editor works without the engine.
- **Tests:** the id map; drag and insert conversions; stores; stories with axe for the Part box and score toolbar; the
  golden-path spec, as `test.fixme`.

### D — MusicXML (`Anode.Interchange/MusicXml`)

- **Writer:** from `ScoreView` plus the score model: everything in Scope, partwise, with exact divisions;
  `.musicxml` and `.mxl` (the container with `META-INF/container.xml`).
- **Reader:** into tracks, regions and score symbols: notes with voices, ties, tuplets, grace notes as short notes,
  chords, transposing instruments to concert pitch, key and time changes into the project when importing into an empty
  project, tempo marks, dynamics and articulations, lyrics, harmony.
- **Validation:** the MusicXML 4.0 XSD (fetched by a script and checked into test fixtures if its licence allows;
  the W3C Community Final Specification Agreement does) in tests.
- **Fixtures:** a hand-written string quartet with key and meter changes; files exported by D round-trip.
- **Tests:** schema validity; round trips (export → import → same notes and symbols); import of the fixtures; against
  A's `ScoreVectors.json` once A merges (until then, small hand-made `ScoreView`s).

### E — AAF (`Anode.Interchange/Aaf`)

- **Container:** Microsoft Compound File Binary ([MS-CFB]) read and write, own implementation or a permissively
  licensed library compatible with GPL-3.0 (record the choice in ADR-0008).
- **Object model subset:** the header, content storage, dictionary, the composition mob with timeline slots per track,
  source clips, fillers, transitions, master mobs and file source mobs with WAVE (or PCM) descriptors, embedded essence
  data or network locators for linked media, markers (`DescriptiveMarker`), clip gain.
- **Writer and reader** for the arrangement's audio tracks as in Scope.
- **Verification:** pyaaf2 (MIT) as an independent reader in opt-in tests (a script in `scripts/` sets up a venv),
  and to generate import fixtures (committed, small).
- **Tests:** CFB round trips (sectors, mini-stream, large streams); AAF export read back by E's reader and by pyaaf2
  (positions to the sample, offsets, lengths, transitions, gain, media); fixture import.

## Integration

1. Merge in this order: A, D, E, B, then C.
2. Set up pyaaf2 and the MusicXML schema for the tests.
3. Run the server tests with the real notation engine and interchange.
4. Run the Playwright golden path for the exit criterion.
5. Fix the seams.

## Status (2026-11-02)

**Exit criterion met on macOS.** The Playwright golden path `phase9/notation-interchange-golden-path.spec.ts` passes on
the real desktop app with the null device, with all nine steps (pyaaf2 as the independent AAF reader, `xmllint` with
the MusicXML 4.0 schema, `pdftotext` for the PDF). All ten e2e specs pass, twice in a row.

| Step | Checked | Result |
|---|---|---|
| 1. Score | chord, one triplet group, a tie across the bar, beams, voice 2, the piano's bass staff; every note id in the SVG | 21 of 21 ids |
| 1. Display quantise | "8,12" (480/320) draws bar 3 as E4/8 F4+G4/8; the MIDI is unchanged | as expected |
| 2. Editing | drag up a step, insert a quarter at 2 3 1 1, delete; one undo step each; the Piano Roll agrees | as expected |
| 3. Symbols and text | staccato, accent, mf, a slur over 3 notes, a crescendo, "Hap-py day", C Am7 Fmaj7 G7 | all drawn with their ids |
| 4. Staff styles | B♭ clarinet part written C5, full score B♭4; a set of two tracks; the part extracted | as expected |
| 5. Print | title, instrument names, lyrics and chord symbols as text in the PDF | found |
| 6. MusicXML | valid against the 4.0 schema; 22 notes back as drawn, with 5 symbols, 3 syllables, 4 chord symbols, tie and triplet; the string quartet fixture's four parts | as expected |
| 7. AAF | embedded and linked: every clip's position, offset and length, the crossfade, −6 dB gain and fades as pyaaf2 reads them; a pyaaf2 fixture imported | exact to the sample |
| 8. Standard MIDI File | type 1 at 960 PPQ: tempo map, a meter change, markers, notes and CCs back | equal |
| 9. Save and reopen | notes, score content, staff styles and region notation | intact |

| Check | Result |
|---|---|
| Engine tests (`dotnet test --solution engine/Anode.slnx --max-parallel-test-modules 2`) | 2427 pass, 18 skipped (opt-in); no failures |
| Notation ([docs/notation](notation/README.md)) | golden views and MEI valid against the MEI 5.0 RELAX NG schema; every note once and full measures over random projects; 20 shared vectors |
| Rendering ([ADR-0011](adr/0011-notation-rendering.md)) | Verovio 6.4.0: 4 staves × 100 bars in 145 ms, a one-note edit re-rendered in 116–149 ms; outside every bundle |
| MusicXML ([docs/interchange/musicxml.md](interchange/musicxml.md)) | every export valid against the 4.0 XSD; the engine's vectors and goldens round-trip |
| AAF ([docs/interchange/aaf.md](interchange/aaf.md)) | own compound-file container; pyaaf2 reads exports to the sample; media at another rate resampled on import |
| Editor (`nx run-many -t typecheck lint test build`) | 6 projects pass (editor 705 tests); Storybook + axe pass |
| e2e (`nx e2e desktop`) | notation and interchange, control and sync, MIDI editing, loops and performance, advanced audio, mixing, arranging, MIDI and Phase 1 golden paths, smoke; twice in a row |
| Skala, Prettier, generated client | clean |

**Contract changes and decisions at integration** (detailed in "Integration notes" above):
- The MusicXML reader picks the engine's factory staff styles (clefs and transposition, then the part name).
- Vertical drags are in the written key in every view; the editor's spelling mirrors the engine's.
- AAF media at another sample rate are converted on import.
- The default short instrument name is the track name cut to five characters, in the engine, the editor and MusicXML.
- Verovio renders with `condense: 'none'`, so every staff is drawn and hit testing holds.
- pyaaf2's MIT notice stays with the AAF model generated from it.

**Open:**
- MusicXML import does not bring back instrument-name overrides (`ImportedScore` has no field for them).
- Imported lyrics, chord symbols and markers are listed by id, not by time.
- Tempo marks draw "♩" as a text character (font fallback), not in the music font.
- AAF: stereo is written as one interleaved slot, not Avid's split mono, untested in Pro Tools or Media Composer; region
  names, fade curves, uneven crossfades and automation are not carried.
- Notation: no grace notes, courtesy accidentals or tuplets other than triplets; the Score Editor's click and lyric
  fixes are covered by the golden path, not unit tests.
- The editor's initial bundle is 1.30 MB against its 1.00 MB budget (#18).
- Linux and Windows.

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

# MusicXML

How Anode writes and reads MusicXML: export from the score, import into tracks. The plan is
[Phase 9, workstream D](../10-phase-9.md#d--musicxml-anodeinterchangemusicxml).

The code is `engine/src/Anode.Interchange/MusicXml`, pure code with no I/O:

- `MusicXmlWriter.Write(view, compressed)` turns a `ScoreView` (the notation engine's output) into the bytes of a
  `.musicxml` file, or of a `.mxl` container. `MusicXmlWriter.Extension(compressed)` is the file's extension.
- `MusicXmlReader.Read(bytes)` turns a `.musicxml`, `.xml` or `.mxl` file into `ImportedContent` for the
  `importTracks` command. It tells a container from a plain file by its content, not its name.

The server's jobs read and write the files around them.

## Export

The writer produces MusicXML 4.0 `score-partwise`, valid against the 4.0 XSD. The same view always gives the same bytes:
UTF-8 without a byte order mark, LF line ends, and fixed ZIP timestamps.

**Header.**
- `work-title` is the layout's title.
- `creator type="composer"` is the layout's composer.
- `encoding` names the software (Anode) and supports `accidental`, `beam` and `stem`. There is no encoding date, so the
  output stays deterministic.
- `defaults`:
  - `scaling` maps the staff height to 40 tenths.
  - `page-layout` gives the page size and margins.
  - `concert-score` is written when the view is at concert pitch and a part transposes.
- `credit`s on page 1 carry the title, subtitle and composer.

**Part list.**
- One `score-part` per part (`P1`, `P2`…), with its name and abbreviation.
- When there are several parts, a bracketed `part-group` encloses them all.
- A percussion part lists a `score-instrument` and a `midi-instrument` for every key it uses. They have channel 10,
  `midi-unpitched` and the General MIDI name.

**Attributes.**
- **Divisions** are chosen per file, so that every duration is an exact integer. They are the least common multiple
  of the denominators of all durations, offsets and gaps, in quarters. Triplets give multiples of 3; a septuplet gives
  multiples of 7.
- **Key** (with its mode) and **time** are written in the first measure, and wherever they change or the view shows
  them.
- A multi-staff part gets `staves` and a `part-symbol`: a brace if the part is braced, otherwise a bracket.
- **Clefs** are written per staff, with octave clefs as `clef-octave-change`, and percussion and none as signs.
- **Transposition.** A transposing view writes `transpose`: diatonic, chromatic and octave-change, from written to
  sounding. Clarinet is −1/−2; tenor sax is −1/−2 with an octave-change of −1. A concert view of a transposing part
  writes MusicXML 4.0's `for-part`/`part-transpose` instead. The key is transposed with the part.

**Notes.**
- **Pitch.** The written spelling is kept. The octave is set so the pitch is the concert pitch plus the part's
  transposition. Pitch octaves are MusicXML's: C4 is 60.
- **Unpitched notes** on percussion staves have a display step and octave taken from the staff position (on a treble
  staff, the bottom line is E4), their `instrument`, and their notehead (`x`, `circle-x`, diamond, triangle, slash).
- Every note has its duration, `type`, dots and `voice`. A whole-bar rest whose value does not fill the bar becomes
  `rest measure="yes"` without a type. Voice numbers are 1–4 on the first staff and 5–8 on the second.
- **Accidentals** are written as the view draws them.
- **Tuplets.** Each tuplet note gets `time-modification`. A `tuplet` start and stop marks each bracket.
- **Beams** come from the view's beam groups: begin, continue and end. Secondary beams for 16ths and shorter are
  computed from the note values, with hooks where they are needed.
- `stem`, and `staff` on multi-staff parts.
- Voices and staves are joined by `backup`, and gaps in a voice are filled with `forward`.
- **Ties.** A tie is written twice: as `tie` (the sound) and as `tied` (the drawing). A note with `tieHidden` carries
  its `tied` in a separate `notations print-object="no"`.

**Notations** (on the note's first tied segment):
- Slurs are numbered 1–16, and overlapping slurs get different numbers.
- `articulations`: accent, strong-accent (marcato), staccato, tenuto, staccatissimo.
- `ornaments`: trill-mark.
- `fermata`: upright, or inverted when it is placed below.
- A dynamic attached to a note goes in the note's own `dynamics`.

**Directions** come before the event they belong to. When the tick falls after the event's start, an `offset` places
them, and `staff` and `placement` are written when known:
- The project's tempo marks become a `metronome` plus `sound tempo` (quarter notes per minute). Its markers become
  `rehearsal`. Both are written in the first part only.
- Dynamics: the named marks, and `other-dynamics` for any other text.
- Wedges are crescendo, diminuendo and stop, numbered like slurs.
- `words` for text. Tempo text is written in bold, so the reader can tell it from plain text.
- Rehearsal symbols become `rehearsal`.
- `pedal` start and stop, with a sign and no line.
- `octave-shift`: an 8va line is `down`, an 8vb line is `up`, with size 8.

**Lyrics.** Each lyric has its verse as `number`, its `syllabic`, its `text`, and `extend` when it has an extender.

**Harmony.**
- `root` (step and alter) and optional `bass`.
- `kind`: derived from the quality, with the quality itself in `text`. For example, "m7" is `minor-seventh` with
  text="m7". A quality with no matching kind is written as `other`.
- `print-frame` when there is a chord grid.

**Barlines.** The last bar is `light-heavy`, and double barlines are `light-light`.

**`.mxl`.** A ZIP containing:
- `mimetype`, stored uncompressed and first;
- `META-INF/container.xml`, naming `score.musicxml` with the MusicXML media type;
- `score.musicxml` itself, deflated.

## Import

Scores can be `score-partwise`, versions 1.0 to 4.0, in UTF-8 or UTF-16. A `.mxl` is opened through its container's
first MusicXML root file. If the container has none, the first `.musicxml`/`.xml` outside `META-INF` is used.
`score-timewise` is refused with a message that names it.

Anything that cannot be read raises `FormatException`, and no other exception type escapes:
- the file is not XML, or not a score;
- the ZIP is damaged, or holds no score;
- a pitch has no step or octave;
- a note has no duration, or a duration is not a number or is negative;
- the divisions are zero;
- the numbers overflow.

DTDs are ignored and nothing external is ever resolved. Files over 128 MB are refused, and so are container entries
over that size.

**Tracks.** Each part becomes an instrument track named after the part, falling back to its abbreviation, then to
"Part n". The track has one MIDI region from tick 0 to the end of its last measure. Positions are tracked as exact
fractions of a quarter, and each note's start and length are rounded to the nearest tick (960 PPQ) separately, so
tuplets do not drift. Ids are provisional and sequential: `trk_000001`, `mid_…`, `sym_…`, `lyr_…`, `chd_…`, `mrk_…`,
`sts_…`, and note ids 1, 2, 3…
- **Chords** share the start of the note before them. Voices and staves are followed through `backup` and `forward`.
- **Ties** are merged into one note. A note joins the open tie of the same pitch when it starts within a tick of where
  that tie ends, whether the file marks the stop or not. A note of that pitch that does not continue the tie breaks it.
- **Tuplets** come out exactly from the durations.
- **Grace notes** become 1/32 notes (120 ticks) that end where their principal note starts. Several graces come one
  after another, and a grace chord shares a slot. They overlap the previous note rather than taking time from it, and
  at the very start of the score they are clamped to tick 0.
- **Cue notes** take time but do not sound.
- **Transposing parts** are converted to concert pitch. Concert = written − (the part's transposition), taken from
  `transpose` (chromatic + 12 × octave-change). Keys are converted the same way. A `for-part` in a concert score only
  sets the staff style, because its notes are already at concert pitch.
- **Unpitched notes** take their key from the instrument's `midi-unpitched`, else from their display position. They
  play on channel 10.
- **Velocity** comes from the note's `dynamics` attribute (as a percentage of 90). Otherwise it comes from the last
  dynamic mark at or before the note in that part (pp 36, p 50, mp 64, mf 80, f 96, ff 112…). The default is 80.

**Staff styles.**
- A part whose clefs and transposition match a factory style (the notation engine's `FactoryStaffStyles`) gets that
  style:
  - percussion: drums;
  - treble: treble; treble 8vb: guitar; bass 8vb: bass guitar;
  - alto: viola;
  - bass: cello when the name says cello, else bass;
  - treble +2: B♭ trumpet when the name says trumpet, else B♭ clarinet;
  - treble +7: F horn; +9: alto sax; +14: tenor sax;
  - treble over bass: piano.
- An octave clef written an octave above sounding (guitar's usual `octave-change -1`) counts as the octave clef with no
  transposition.
- Any other setup gets a style of its own, one per distinct setup. Its staves have the part's initial clefs and its
  transposition. On each staff, the split point is the lowest pitch read on that staff, or a default for the clef
  (treble 60, alto and tenor 48, bass 36). The style allows the most voices seen on the staff (1–4) and a display
  quantise of 1/16 and 1/16 triplets. It is braced when the file says so, or when it is treble over bass.

**Project events.** The importing job applies these to an empty project.
- **Signatures** come from the first part. A short first measure marked `implicit` (a pickup) is padded at its start
  to a full bar, so its notes end on the bar line and bars stay on the project's grid. A tempo at the pickup's start
  then holds from tick 0. Any other irregular bar (implicit or overfull) gets its own signature, and the meter returns
  after it.
- **Keys** come from the first part: a key and mode at each measure where they change, at concert pitch.
- **Tempos** come from `sound tempo`, or from a `metronome` mark (beat unit and dots converted to quarters) when there
  is no `sound`. The first tempo seen at a tick wins. A file whose first tempo comes later gets 120 BPM at tick 0, and
  a file with no tempo leaves the project's alone.
- **Rehearsal marks** become markers, one per tick and text.

**Score content.** All of it is attached to the part's track.
- **Note-attached symbols:**
  - accent, strong-accent (marcato), staccato, tenuto;
  - staccatissimo (and spiccato as staccatissimo); detached-legato as tenuto plus staccato;
  - trill-mark;
  - fermata (below when inverted);
  - `notations` dynamics;
  - `tieHidden` from a `tied` start inside `notations print-object="no"`;
  - slurs from start to stop by number, from note to note.
- **Positioned symbols** are placed at the tick, with `staff` on multi-staff parts and `placement`:
  - dynamics; text that is not a dynamic becomes a text symbol;
  - words become tempo text when they are bold or share a direction with a tempo, and text otherwise;
  - `pedal`: start, stop, and change (up, then down);
  - wedges and octave shifts become tick-to-tick spans (8va is `down`).
- **Lyrics.**
  - The verse comes from the last digits of `number` ("1", "verse2"); otherwise it is 1.
  - The text is joined across elisions, with ‿ for an empty elision.
  - `syllabic` and `extend` are kept.
  - There is at most one syllable per note and verse. Syllables on tie continuations are dropped.
- **Chord symbols.**
  - Root, bass and quality. The quality is the `kind`'s `text` when there is one, otherwise the usual text for the
    kind ("m7", "maj7", "dim", "sus4"…).
  - `print-frame="yes"` or a `frame` turns the chord grid on.
  - `kind` none (N.C.) is skipped.
- **Title** is `work-title`, else `movement-title`, else a title credit. **Composer** is the composer `creator`, else a
  composer credit.
- Texts are trimmed to 200 characters.

## What is lost

**On export.**
- **Not in the view, so not written:** voices beyond what the view draws, cross-staff notes, the header and footer, the
  layout's instrument-name overrides beyond the part names, chord grids' fingerings (only `print-frame`), secondary
  pedal styles, and slur and tie shapes.
- **Repeats, endings and multi-measure rests:** none (the score model has no repeats).
- **System and page breaks** are Verovio's, so the file has no `print` elements.
- **Tempos** are written as quarter-note BPM. A ramp in the project's tempo is written as its marks only.

**On import.**
- **Voices** are not kept as voices. Anode's notes have none, so the notation engine derives voices again from overlap
  and the staff style.
- **Staff assignment:** staves become a staff style, and which staff a note sits on is re-derived from the split point.
  Cross-staff notation is lost.
- **Mid-part changes:** only a part's initial clefs are kept, and mid-part clef changes and instrument changes are
  lost.
- **Not imported at all:**
  - repeats, endings, segno and coda (the music is read once, as written);
  - dynamics in `sound` (for example `sound dynamics`);
  - articulations other than those listed above;
  - ornaments other than trills;
  - technical marks, glissandi, arpeggios and fingerings;
  - figured bass;
  - layout, credits other than the title and composer, and fonts.
- **Note anchors:** note-attached wedges and octave lines come back as tick-to-tick spans, and note-attached text as
  positioned text. They are drawn the same way.
- **Rehearsal symbols** come back as markers, not as rehearsal symbols.
- **Grace notes** are a fixed 1/32 before the beat. `steal-time` and `make-time` are ignored.
- **Tuplet timing:** notes in tuplets that do not divide 960 PPQ are rounded to the nearest tick.
- **Microtones:** quarter-tone alterations are rounded to the nearest semitone.
- **Lyrics:** lyrics on rests, and `humming` and `laughing`, are dropped.
- **Timewise scores** (`score-timewise`) are refused. MusicXML's own `parttime.xsl` converts them.

## Validation

The tests validate every exported file, and the hand-written 4.0 fixtures, against the **MusicXML 4.0 XSD**:
- `engine/tests/Anode.Interchange.Tests/MusicXml/Schema/`: `musicxml.xsd`, the `xml.xsd` and `xlink.xsd` it imports
  (resolved locally, never from the network), and `container.xsd` for `.mxl` containers;
- they come from the W3C Music Notation Community Group's repository at tag `v4.0` (`w3c-cg/musicxml`);
- `scripts/fetch-musicxml-schema.mjs` downloads them again and checks their SHA-256. `--check` verifies the committed
  copies only;
- the files keep their CRLF line ends (`-text`), so the hashes hold.

**Licence.** The schema files say they are published under the
[W3C Community Final Specification Agreement](https://www.w3.org/community/about/agreements/final/). That agreement
grants a copyright licence to reproduce, distribute and make derivative works of the specification, so committing the
XSD with the tests is allowed. `xml.xsd` is the W3C's schema for the XML namespace.

The notation engine's shared vectors (`ScoreVectors.json`) and golden views are exported, validated and read back too
(`MusicXmlEngineTests`): every note comes back where the score draws it, with its staff style, and the imported notes
render to the same events; every factory style comes back as itself.

**Fixtures** (`engine/tests/Anode.Interchange.Tests/MusicXml/Fixtures/`, hand-written):
- `string-quartet.musicxml`: four parts. Key and meter change in bar 3, with ties across bar lines, a triplet, slurs,
  dynamics, a crescendo, tempo and a fermata.
- `piano-lyrics-chords.musicxml`: a grand staff with two voices, lyrics in two verses, chord symbols (one with a bass,
  one placed by an offset) and a staccato.
- `grace-notes.musicxml`: a grace note at the score's start, and two before a beat.
- `song.mxl`: a container whose root file is not the default path, holding a UTF-16 MusicXML 3.0 score in a minor key.
- `clarinet-3.1.xml`: a MusicXML 3.1 B♭ clarinet part at written pitch. It has a pickup, a measure-level tempo, and
  MIDI, print and layout data.

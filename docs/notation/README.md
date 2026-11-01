# Notation engine

How `Anode.Notation` turns MIDI into a score (Phase 9, [docs/10-phase-9.md](../10-phase-9.md), workstream A;
[ADR-0011](../adr/0011-notation-rendering.md)). The engine decides what is musically fixed: which notes are drawn
where, their written durations, ties, tuplets, voices, beams, stems, spelling and measures. Verovio engraves the MEI
(spacing, beam slopes, collisions, system and page breaks). The score is a view: the engine derives it from the MIDI
each time, and display settings never change a note.

## API

| | |
|---|---|
| `NotationEngine.Render(state, score, ScoreRequest(setId?, partTrackId?, mode))` | The `ScoreView` of a score set (null: every track that holds MIDI, in track order) or one track's part. A part is written at transposed pitch; the full score at concert pitch unless `layout.transposingScore`. An unknown set or track, or a part track outside the given set, throws `ArgumentException`. |
| `MeiWriter.Write(view)` | MEI 5 text, byte for byte the same for the same view. |
| `StaffPositions.PitchAt`, `PlacementOf`, `Step` | Staff position ↔ concert pitch for a clef, transposition and key (what `moveScoreNotes` and `addScoreNote` are converted with). Also `KeyOf` (a project key as a signature), `WrittenKey` (a transposed staff's key), `DiatonicSteps` (MEI's `trans.diat`), `DrumPitchAt` (a drum staff position's pitch). |
| `DisplayGrid.GridsOf`, `DisplayTick`, `Move` | A region's display grids as drawn, where a note is drawn, and the MIDI start after a horizontal drag of n display steps. |
| `FactoryStaffStyles.All`, `Find`, `Resolve`, `GeneralMidiDrums` | The factory styles as data (`StaffStyleIds`). |

## The pipeline

For each track of the view, under its staff style (`FactoryStaffStyles.Resolve`; null is the piano):

1. **Notes**: every note of the track's MIDI regions (muted ones too) in project ticks, cut at its region's end; a
   note that does not sound (before the region, at or after its end) is not drawn. Region playback parameters
   (transpose, velocity, quantise) are not applied.
2. **Staves**: a note goes to the first staff whose split point (concert pitch) it reaches; the piano's upper staff
   takes 60 (C3 in Logic's names, C4 in MEI's) and up.
3. **Display quantise** (below): each start and end is moved to the display grid.
4. **Chords**: notes of a staff with the same displayed start and end are one unit.
5. **Voices** (below): units into lanes by overlap, then voices within each passage.
6. **Interpretation** (below): each lane's ends tidied, and cut at the lane's next start.
7. **Measures**: from the project start to the last bar a note (or a positioned symbol or chord symbol) reaches.
8. **Durations**: each voice of each measure, its units and the rests between them, written as tuplets and tied values
   by the meter.
9. **Spelling, accidentals, beams, stems**, and the stored symbols, lyrics and chord symbols at their events.

## Display quantise

- A region's grids are its `RegionNotation`'s: a straight grid and a triplet grid in ticks. A straight grid of 0 means
  the staff style's grids (both); the factory styles use 1/16 and the 1/16 triplet (240 and 160, Logic's "16,24").
- Grids as drawn (`DisplayGrid.GridsOf`): the straight grid is a plain value (a power-of-two fraction of a whole note)
  from a 1/128 (30, also for 0) to a whole note; a grid between values takes the value below. The triplet grid is one
  whose triplet unit is a plain value (20, 40, 80, 160, 320, 640, 1280, 2560), else 0 (none).
- Grids are counted from each bar line.
- **Triplet spans**: the triplet grid applies to spans of a bar: its beats when every beat holds whole triplets of the
  grid (a 1/16 or 1/8 triplet grid in 4/4 or 6/8), else three grid units at a time from the bar line (a 1/4-triplet
  grid in 4/4: half bars); a partial span at the bar's end takes no triplets.
- **The rule** (Logic decides per note, from its own heuristics, which are not documented): each span is drawn on the
  triplet grid when its notes' starts are nearer the triplet grid than their straight grids **in sum**, else on the
  straight grid (a tie goes to the straight grid). Deciding per span rather than per note keeps tuplets whole: a beat
  is either triplets or straight, never a mixture that needs nested tuplets.
- **One triplet grid per bar** of a track: the grid of the bar's first note (by start) whose region has one. Straight
  grids stay per region (they always nest). This keeps every display position of a bar compatible when regions with
  different settings overlap.
- **Ends** go to the nearest display position of their span; a bar line stays. A note is at least one display step
  long (to the next display position). A position that would fall inside a triplet span off its grid (a straight grid
  coarser than the span) moves to the nearest triplet position.
- `DisplayTick` decides with the region's notes (and the tick); `Move` steps through the bar's display positions (the
  triplet grid's in a triplet span), across bar lines, keeping a note's offset from the grid, never before 0.

## Voices

`Staff.voices` (1…4) is the most voices a staff makes from overlaps. Units are taken in start order, the highest first
at a start:

- A unit that starts inside a held one and **outlasts** it is legato: with **no overlap** on, it cuts the held one
  where it starts and continues its lane.
- Otherwise it takes a free lane (the nearest in pitch), else a new lane while the staff has voices left. So a held
  note with moving notes under it (or over it) makes a second voice; notes that start together with different ends
  make voices; with no overlap off, legato overlaps make voices too.
- With no voice left, it cuts the most recent unit at its start, or joins it as a chord when they start together
  (on a one-voice staff, notes starting together are one chord, as long as the longest).
- **Numbering**: within each passage of units chained by overlap, lanes are numbered by mean pitch from the top (voice
  1 the highest); a passage with one lane is voice 1. Voice 1 is always present in a measure (a whole-measure rest when
  empty); another voice appears in the measures where it has notes, its gaps filled with rests.

Factory voices: treble, bass, piano (each staff), guitar, viola and cello 2; bass guitar, the wind instruments and drums 1.

## Interpretation

With interpretation on, an end that is not on a beat line or on the beat's subdivision (the half of a simple beat,
the eighths of a compound one) goes to the nearer beat line, the later on a tie, never back to the start: short notes
are lengthened to the beat (staccato quarters read as quarters) and a note just past a beat is shortened to it (tidy
rests). In a triplet span the span's bounds are the lines. With it off, the quantised end stays. Either way an end stops
at the lane's next start.

## Durations

The measure's **metric tree**: the bar; its halves in a meter of four beat groups (4/4, 12/8, 4/2); its beat groups
(3s in compound meters, 6/8 9/8 12/8 6/4 3/8; 1s up to four units; 3 + 2 + 2… for odd numerators, 5/8 = 3 + 2, 7/8 =
3 + 2 + 2, 5/4 = 3 + 2; 2s otherwise); units; halves (thirds of a dotted value) down to the 1/64. Beats are the groups
in compound meters and in eighths or shorter, else the units.

A span of a voice (a note's part in the measure, or a rest) is written by recursion from the bar:

- a span that is exactly a node, and expressible (a value with up to **max dots** dots for notes), is one value;
- a span inside one child goes down to it;
- a **note** crossing children is one value when expressible and: it is inside a beat (anything goes within a beat:
  16th-8th-16th); or it starts at the node's start (in simple meters anywhere, a dotted quarter on beat 1; in compound
  meters and eighth meters only up to a child boundary, so 6/8 splits a half note at the beat); or it fills the last
  two parts of a triple node (a half note on beat 2 of 3/4, a quarter after the first eighth of a 6/8 beat); or
  **syncopation** is on and it starts on a multiple of half its undotted value (a quarter on an offbeat, a half on beat
  2 of 4/4);
- a **rest** crossing children is one value only from the node's start, undotted, ending on a child boundary (a half
  rest on beat 1 or 3 of 4/4, never on beat 2); dotted rests only as a whole compound beat;
- otherwise the span is split at the children's boundaries, each part recursing, the parts tied.

An empty measure of a voice is one **whole rest** whatever the meter (`durationTicks` the measure's length; MEI
`mRest`). Syncopation off, a note on beat 2 of 4/4 lasting two beats is a quarter tied to a quarter; on, a half.

## Tuplets

In each span drawn as triplets where a voice has a boundary (a note or rest starting or ending inside it), the voice
writes **groups of three units** (3:2): the unit is the triplet grid doubled as long as every boundary in the span falls
on it, so eighth triplets in a beat are one group even on the 1/16-triplet grid, and sixteenth triplets two. A group
without a boundary inside is written straight (one value for the whole group); a note crossing a group's edge is tied
there. Each piece is the undotted value of 3/2 its length (a 1/8-triplet unit, 320 ticks, is written as an eighth).
Groups are numbered 1… within a measure's voice (`ScoreTuplet(group, 3, 2)`). Only triplets are made.

## Beams

Consecutive notes and chords of an eighth or shorter, within one beat, not across rests or tuplet boundaries, are a
beam group (two or more; numbered 1… within a measure's voice). In simple meters of four beats (4/4, 4/2), a half bar
of plain eighths is one beam. Compound meters beam by the dotted beat, 5/8 as 3 + 2.

## Stems

With several voices in a staff's measure, odd voices stem up and even down. With one: a beam group or an unbeamed event
stems away from its note farthest from the middle line (down on a tie); a percussion staff stems up. Rests, whole notes
and breves have none.

## Spelling

- **Keys**: a project key event takes effect at the bar line at or after its tick (key changes are drawn on bar lines).
  Major keys by tonic: C G D A E B F♯ (6 sharps) and F B♭ E♭ A♭ D♭ (5 flats); minor keys by their relative major, but
  E♭ minor (6 flats) rather than D♯; so G♯ minor, B♭ minor. No key event is C major.
- **A pitch in a key**: the key's own notes; in minor its raised seventh and sixth as the degree raised (C♯ in D minor,
  C𝄪 in D♯ minor); naturals (B♮ in B♭ major, F♮ in G major); then sharps in sharp keys and flats in flat keys, and in C
  major and A minor C♯ E♭ F♯ G♯ B♭.
- **Accidentals**: against the key signature, carried through the bar for the same step and octave on the staff (every
  voice), cancelled with naturals; a tied continuation draws none and changes nothing. No courtesy accidentals.
- **Transposition**: written = concert + the staff's transpose, in the written key (the concert key moved by the
  interval's fifths, kept within six accidentals: concert B major on a B♭ instrument is written D♭ rather than C♯). A
  part is always transposed; the full score only with `transposingScore`. `ScorePart.transpose` is the first staff's
  when transposed, else 0. MIDI pitches (`ScoreNote.pitch`) stay concert.
- **Clefs**: positions are diatonic steps above the bottom line (MEI `loc`): treble E4, bass G2, alto F3, tenor D3. An
  octave clef's octave is the clef's: a treble 8vb staff's bottom line reads E3, so the guitar's low E (MIDI 40) is
  written E2, seven steps below the bottom line. Percussion and no clef read as treble.
- **Drums**: a percussion staff with a drum map places a mapped note at its entry's position and notehead (written as
  the treble note there, no accidental); an unmapped note is spelled on a treble staff in C. Its key is C.

## Measures and stored items

- `ScoreMeasure`: number, start, length, meter, key; `showTime`/`showKey` on the first measure and at changes; tempo marks
  where the tempo changes value (and at 0); markers as rehearsal marks (`markerId`, the marker's name); the barline is
  double before a meter or key change or a marker on the next bar line, final on the last measure.
- The first measure carries each staff's clef.
- **Symbols, lyrics, chord symbols**: a note-attached item is on the event of the note's first segment; a positioned one
  on the voice-1 event of its staff (`staff`, else the bottom staff for pedals and the top staff otherwise) that covers
  its tick; chord symbols on the top staff's voice-1 event at their tick. An item is in the view when its track is and
  every note it names is drawn. Lists are ordered by id (chords by tick, then id; an event's lyrics by verse).

## MEI

MEI 5 (`meiversion="5.0"`), validated against MEI 5.0's `mei-all.rng` in the tests (with `xmllint`):

- `meiHead` with the title, subtitle (`title type="subtitle"`) and composer (`respStmt/persName role="composer"`).
- The first `scoreDef`: meter, `keysig` (concert), page size and margins in mm, `vu.height` (staff height ÷ 8); a
  `pgHead func="first"` with the title block (title, subtitle, composer, header) and a `pgFoot func="all"` with the
  footer, when set; a `staffGrp` of the parts: a single staff as `staffDef` with `label`/`labelAbbr`, several in a nested
  `staffGrp` (`symbol="brace"` for a braced style, else `bracket`; `bar.thru`). A staff whose written key differs (a
  transposed staff, a percussion staff) has its own `keysig`; a transposed staff `trans.semi`/`trans.diat` (written to
  sounding: −2 and −1 for B♭). Clefs: `clef.shape`/`clef.line`, `clef.dis="8"` with `clef.dis.place` for octave clefs,
  `perc` for percussion, `clef.visible="false"` for none.
- A meter or key change is a `scoreDef` before its measure (with a `staffGrp` of every staff's `keysig` when transposed
  staves differ).
- Measures (`xml:id` `m{n}`, `right="dbl|end"`), staves numbered from 1 across the parts, a `layer` per voice; events
  `note`, `chord` (notes inside, without `dur`), `rest`, `mRest` for an empty measure, inside `tuplet num="3"
  numbase="2"` and `beam` (beams never cross tuplets). Notes: `pname`, `oct` (C4 = 60), `accid` when drawn (`n s f x
  ff`), else `accid.ges` for an altered note, `stem.dir`, `head.shape` (`x`, `+`, `diamond`, `isotriangle`, `slash`),
  `loc` on a percussion staff. Articulations as `artic` inside the note or chord; lyrics as `verse n`/`syl` with
  `wordpos` (`i m t`) and `con` (`d` after a begin or middle syllable, `u` for an extender).
- Control events after the staves: project tempo marks (`tempo` with `midi.bpm`, "♩ = 120"), markers (`reh`), then per
  event its symbols: `dynam`, `dir`, `tempo`, `reh`, `pedal dir`, `fermata`, `trill` (note-attached by `startid`,
  positioned by `tstamp`), `slur` (`startid`/`endid`), `hairpin form="cres|dim"` and `octave dis="8" dis.place` (by notes
  or by `tstamp`/`tstamp2`, an end on a bar line being `0m+{beats+1}`), with `place` from the symbol's placement; chord
  symbols as `harm` (text root, quality and bass: "F♯m7b5", "Fmaj7/A"); ties as `tie` with `startid`/`endid`. A hidden
  tie (`tieHidden`) is left out: MEI 5's `tie` has no visibility attribute.
- Ids are `ScoreIds`: `n{noteId}` / `n{noteId}-{k}`, `c{lowest noteId}`, `r{trackId}-{measure}-{staff}-{voice}-{index}`,
  `s{symbolId}` (on the symbol's element; none for a hidden tie), `l{lyricId}` on the `syl`, `h{chordId}` on the `harm`,
  `m{n}`. Tempo marks, markers and ties have none.

## Vectors

`engine/tests/Anode.Notation.Tests/ScoreVectors.json` (shared with the editor and MusicXML workstreams): `ppq` and
`cases`, each with `name`, `description`, `staffStyleId`, `part` (render as the extracted part), `signatures`, `keys`,
`notation` (`RegionNotation`), `regionLengthTicks`, `notes` (`id`, `pitch`, `startTick`, `lengthTicks`, relative to a
region at tick 0), and `expected`: `transposing`, `transpose`, `measures` (`number`, `startTick`, `lengthTicks`, `time`,
`key`, `barline`) and `events` in measure, staff and voice order: `measure`, `staff` (0…), `voice` (1…), `id`, `kind`,
`tick`, `durationTicks`, `value`, `dots`, `tuplet` (`group`, `actual`, `normal`, or null), `beam`, `stem`, `notes` (`id`,
`noteId`, `pitch`, `written` {`step`, `alter`, `octave`}, `staffPosition`, `accidental`, `tieStart`, `tieStop`,
`notehead`). Names and enums are the contract's JSON. The test `VectorTests` checks the engine against it
(`ANODE_UPDATE_VECTORS=1` rewrites it). Whole `ScoreView`s and their MEI, contract-shaped, are in
`engine/tests/Anode.Notation.Tests/Golden/` (`ANODE_UPDATE_GOLDEN=1`).

## Differences from Logic

- Display quantise decides triplets **per span** (beat), by the sum of the starts' distances, not per note; one triplet
  grid per bar of a track.
- Voices come **from overlaps**, up to the staff's voice count, rather than from Logic's polyphonic staff styles'
  assignment by channel or split; voice 2 rests are drawn rather than hidden.
- Interpretation is a fixed rule (to the nearer beat line, keeping half beats), not Logic's.
- A measure's syncopation and max dots come from the region of its first note; interpretation and no overlap from each
  unit's notes' region; straight display grids per note, the triplet grid per bar.
- Region playback parameters (transpose, quantise) are not shown; Logic reflects some of them.
- Key changes take effect at the next bar line; only triplets are made (no duplets, quintuplets); no grace notes, no
  courtesy accidentals, no cross-staff beams, no user rests.
- Systems and pages are Verovio's (the view has measures only).

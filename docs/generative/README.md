# Session Players and Chord ID (`Anode.Generative`, `Anode.Dsp.Analysis.Chords`)

Phase 11's generative and harmony work ([docs/12-phase-11.md](../12-phase-11.md), workstream D), after
[ADR-0014](../adr/0014-session-players-and-chord-id.md): the Session Player generator and its style library, the
Drummer kits, and Chord ID with key detection. Everything is pure C#, deterministic and Anode's own: the styles are
written for Anode as data, the kits are made by the content builder from Anode's instruments, and no model weights or
downloaded content are used.

## The generator

`SessionPlayerGenerator.Generate(input)` is a pure function of a `GenerationInput` (the region's player, parameters,
seed, place and length, the chord track, the arrangement sections, the signatures, the key track and the rhythm
source's onsets) and the library's version. Before anything else the input is cut to what lies over the region (the
chord, scale, key and section sounding at its start or ending before it, those inside it, the signatures before its
end, the onsets inside it): `Hash` encodes exactly that part (one field per line, invariant numbers, pitch classes for
spelled pitches, section kinds for section names) with the library's version, as lowercase hex SHA-256, so a change
outside the region keeps the cache entry and equal hashes always mean equal notes. A region whose style the library
lacks, or whose style is another player's, plays nothing.

| Step | What it does |
|---|---|
| Timeline | Bar lines from the signatures (4/4 before the first); the region cut into section segments (a gap between sections is a verse that ends where the next section starts); each bar's index in its section and whether it is the section's last; the chords over the region (the key's tonic triad before the first chord, or throughout when `followChords` is off); the scale at a tick (the chord track's scale changes, else the key track). |
| Phrase selection | Per segment, with the pad moved by the section kind's grammar offsets: phrase A is the groove nearest the point (distance \|Δcomplexity\| + ½ \|Δintensity\|; phrases within 0.05 of the nearest are equally likely, the seed picks); phrase B the nearest other groove within 0.3 of A on both axes. |
| Variation grammar | The section kind's form (letters A and B, cycled over the section's bars), its first and last bar roles (intro, pickup, ending), and the substitutions it allows. |
| Fills | The last bar of a section that ends inside the region, when `fillAtSectionEnds`; the fill nearest the pad, its length following the fill amount (a beat at 0, the bar at 1). Inside a section, the last bar of every four, with probability ½ × fill amount. A fill plays over its groove until its `from` step. |
| Players | Drummer, Bass Player and Keyboard Player below. |
| Performance | Swing on the style's grid (16ths or 8ths: the offbeat moves by up to a triplet), feel (push or lay back by up to a 32nd), seeded humanisation per event (timing and velocity, triangular, within the style's limits), the region's bounds, one note per pitch and tick, ids 1, 2, … by start then pitch. Velocities are a token's base times 0.6 + 0.5 × intensity. |

Randomness is hashed, not drawn in sequence: every draw is SplitMix64 over the seed and where it is used (a bar's
start, a step, a note), so a change in one bar leaves the others' draws alone, and no floating-point result beyond IEEE
basic arithmetic decides a note.

### Drummer

A bar's phrase lanes become kit notes on [`DrumKitNotes`](../../engine/src/Anode.Contracts/SessionPlayers.cs) (General
MIDI). Ghost notes (`g`) play with probability (complexity − 0.2) ÷ 0.6. Substitutions, where the grammar allows them:
`crashOnOne` (a crash on a section's first beat; there is always one after a fill), `openHat` (the last offbeat hat
opens, on B bars or loud ones), `rideForHat` (loud bars move the hats to the ride), `ghostNotes` (snare ghosts before
the backbeats at complexity ≥ 0.6) and `halfTime` (the backbeat moves to beat 3). Kit-piece switches: a variation 1…3
plays the style's variation lane for the piece's main note; a piece switched off is silent, and with the hi-hat off
the ride (or its bell, by the style's `timekeeper`) keeps time. A fill keeps the groove's hats, ride and percussion
under it and, when its own notes would not outnumber every groove bar of its section (all their lanes' hits, ghosts
included, and three for substitutions), grows a snare roll (then kicks, then the high tom) back from the bar's end.

**Following a rhythm source:** in a groove bar where the source has onsets, the kicks are the onsets on the grid (the
nearest step), the strongest positions first (half bars, beats, eighths, the rest), at most 2 + 6 × complexity, away
from the backbeat snares. Every such kick is within half a step of an onset.

### Bass Player

Tokens are chord-relative degrees on the chord sounding at the note's grid tick: `R` the root (the slash bass under a
slash chord), `r` the root itself, `3 5 7` the chord's third, fifth and seventh (the octave when it has none), `8` the
octave, `2 4 6` the scale steps nearest a second, fourth and sixth above the root, `A` a half step into the next chord's
root (inside the region), `X` a dead note (with the switch; else a rest). Roots sit in the range's lowest octave and every
pitch is folded into the range. At every chord change the note starting there becomes the root (or slash bass); a note
held across is cut and the root struck; a rest gets the root. `approachNotes` turns the last note in the eighth before a
change into a half step into it; `octaveDoubling` lifts offbeat roots an octave when loud; the slides switch adds a
grace note a whole step under a change's root. One note at a time, after humanisation too.

### Keyboard Player

Each chord is voiced once and voice-led from the chord before ([`Voicer`](../../engine/src/Anode.Generative/Voicer.cs)):
close (four voices, a triad's root doubled, a ninth without its fifth), spread (close with its second voice from the top
dropped an octave) or shell (the bass, third and seventh, the root instead of a slash bass that duplicates one), every
inversion and octave placement inside the range considered, the one moving least chosen (index by index, a penalty
for any voice moving more than a fifth, a pull towards the range's middle). Right-hand tokens: `C` (`c` softer) the
voicing, `T` its top, `U` all but its bottom, `1`…`4` a voice from the bottom (arpeggios); a token held across a chord
change is struck again on the new chord. Left hand, below the range, by its switch: the bass, the bass and its octave,
or the root and fifth as written. `passingNotes` adds, at complexity ≥ 0.6 and now and then, a scale tone between the
two voicings' tops a 16th before a change (at most 8 % of the notes); `octaveDoubling` doubles a loud voicing's top.
The rhythm switch plays the style's rhythm in place of the groove phrases' right (and left) hand.

## The style library

Versioned JSON in `engine/src/Anode.Generative/Styles/`, embedded in the assembly (`StyleLibrary.Builtin()`), loadable
from a folder for authoring (`StyleLibrary.Load(folder)`, which names the file in any error). Comments are allowed.
`library.json` holds the format (1) and the library's version, which **must be bumped with any change to the styles or
the generator's output** (it is part of every hash: a new version regenerates the regions).

A style file (format 1):

| Field | Meaning |
|---|---|
| `id`, `name`, `player` (`drummer`, `bass`, `keyboard`), `genre`, `description` | The catalogue's. Ids are lowercase words and dashes. Names are a genre and a feel: never a person's or a product preset's (ADR-0014). |
| `synthPlayer`, `kitPatchId` | A Synth Player style (keyboard only); the kit patch a new Drummer track gets (drum styles only, and always). |
| `stepsPerBar` (16 or 12), `swingGrid` (240 or 480 ticks) | The lanes' grid (sixteenths, or triplet eighths: no swing) and what swing delays. |
| `humanise` | `timingTicks` (0…60) and `velocity` (0…30): the most either moves. |
| `timekeeper` | Drums: `ride` or `cymbals` (the bell) keeps time with the hi-hat off. |
| `defaults`, `presets[]` (`id`, `name`, `parameters`), `sections` (each kind) | Partial parameters (`complexity`, `intensity`, `fillAmount`, `fillAtSectionEnds`, `swing`, `feel`, `followChords`, `pieces` by kit piece with `on` and `variation`, `bass`, `keys`) over the defaults and the player's switches; they become the catalogue's presets (the first the default) and section defaults. |
| `grammar` (each kind) | `form` (1…16 of A and B), `complexity` and `intensity` offsets (−1…1), `substitutions`, `first` (`intro`) and `last` (`pickup`, `ending`) roles. |
| `variations` | Drums: per kit piece, 1…3 one-bar lanes for its main note. |
| `rhythms[]` | Keyboard: `id`, `name`, `rh` (and `lh`) one-bar lanes. |
| `phrases[]` | `id`, `role` (`groove`, `fill`, `intro`, `ending`, `pickup`), `complexity` and `intensity` (0…1), `bars` (1…4; fills 1), `from` (fills: the step they take over at), `lanes`. |

Lanes: drums a character per step (`X` 118, `x` 96, `o` 74, `g` a ghost, 42; `.` rest) on lanes `kick snare stick clap
hat pedal open tom1 tom2 tom3 floor crash ride bell tamb cowbell shaker`; bass (`bass`) and keys (`rh`, `lh`) a token per
step separated by spaces, `-` holding the note before, `!` accent, `?` soft; `|` marks beats anywhere. Every lane has
`bars × stepsPerBar` steps.

### What Phase 11 ships (library 1.0.0)

| Style | Player | Notes |
|---|---|---|
| Rock, Straight Eights (`drums-rock`) | Drummer | Acoustic kit; 11 grooves, 5 fills, count-in, pickup, ending; presets Straight, Driving, Laid Back, Sparse. |
| Pop, Modern Groove (`drums-pop`) | Drummer | Acoustic kit; claps, shaker and tambourine layers; presets Radio, Big Chorus, Intimate. |
| Funk, Sixteenth Pocket (`drums-funk`) | Drummer | Acoustic kit; ghost notes, cowbell, swing 0.15; presets Pocket, Greasy, Tight. |
| Electronic, Four on the Floor (`drums-electronic`) | Drummer | Electronic kit; offbeat open hats, snare-roll builds; presets Club, Peak, Minimal, Shuffled. |
| Ballad, Brushes (`drums-brushes`) | Drummer | Brushes kit; 12 steps a bar (triplet feel); presets Late Night, Swinging, Whisper. |
| Rock, Driving Eighths (`bass-rock`) | Bass | Roots in eighths, walk-ups; presets Eighths, Roots, Pumping. |
| Funk, Syncopated Sixteenths (`bass-funk`) | Bass | Octaves, sevenths, dead notes; presets On the One, Slap, Clean. |
| Pop, Melodic Roots (`bass-pop`) | Bass | Long roots opening into lines; presets Melodic, Whole Notes, Lively. |
| Pop, Piano Eighths (`keys-pop`) | Keyboard | Rhythms Quarters, Eighths, Syncopated, Offbeats; presets Ballad, Eighths, Driving, Shells. |
| Ballad, Flowing Arpeggios (`keys-ballad`) | Keyboard | Spread voicings, octaves; rhythms Arpeggio, Block, Halves; presets Flowing, Hymn, Swelling. |
| Synth, Pulsing Arpeggio (`keys-synth-arp`) | Keyboard (Synth Player) | Pads to 16th arpeggios; rhythms Pulse, Offbeat, Arpeggio Up, Gated; presets Arpeggio, Pad, Pulse and Bass. |

### The Drummer kits

Made by the content builder (`engine/tools/Anode.ContentBuilder/SessionPlayerContent.cs`, behind its `Ready`) from the
Drum Synth (the T1 synth with `--t1` or without it), as kit patches with a pad on every `DrumKitNotes` note (17), so a
Drummer track is a multi-output Drum Machine Designer (a member track, and a strip, per pad): **Session Acoustic Kit**
(`kit_drummer_acoustic`), **Session Electronic Kit** (`kit_drummer_electronic`), **Session Brushes Kit**
(`kit_drummer_brushes`). The test pack carries the acoustic kit only. Pads are panned as a kit is seen from the
drummer's seat. Workstream E's physical model can replace the toms and cymbals once it is merged.

## Chord ID

| Entry point | Algorithm |
|---|---|
| `MidiChordRecognizer.Recognise(notes, beats, resolutionTicks, key?)` | Per beat, each pitch class weighted by how much of the beat its notes sound times 0.5 + 0.5 × velocity ÷ 127; the bass the lowest note sounding through a quarter of the beat. Decoded as below; chords shorter than the resolution fold into a neighbour. The key, when not given, is `KeyDetector.FromNotes`'s. |
| `AudioChordRecognizer.Recognise(mono, rate, beats, key?)` | The harmonic chromagram (below), each beat the mean of the frames centred a quarter window after its start to half a window before its end, its floor (the median pitch class) taken off, its bass the lowest strong semitone; decoded as below. With fewer than two beats, Phase 5's `BeatTracker` finds them. |
| `KeyDetector.FromNotes`, `FromAudio` | Pearson correlation of the pitch-class weights (notes by duration and velocity, or the chromagram's sum) with the Krumhansl–Kessler profiles of the 24 keys; the confidence is the best correlation. |

**Decoding** (`ChordDecoder`, shared): each beat scores every root × quality of
[`ChordTemplates`](../../engine/src/Anode.Dsp/Analysis/Chords/ChordTemplates.cs) (156 chords) by the cosine between its
chroma and the template, + 0.08 when the bass is the root, + 0.02 when it is another chord tone, − 0.04 when it is
none, + 0.03 when every tone is in the key (major, or minor with the raised seventh); a silent beat prefers no chord.
A Viterbi pass maximises the sum less 0.12 per change. Neighbouring beats of one chord merge; a chord's bass is its
first beat's (−1 when the root), its confidence the mean cosine.

**The chromagram:** Hann frames of 340 ms (16,384 at 48 kHz), hop 85 ms, magnitudes to 4 kHz; percussive energy
suppressed by Fitzgerald's median filtering (H over 7 frames, P over 17 bins, soft mask H² ÷ (H² + P²)); the tuning as
the circular mean of the spectral peaks' offsets from equal temperament; semitone bins E1…C7 on that tuning; overtones
removed from the bottom up (an octave × 0.5, a twelfth × 0.25, two octaves × 0.125, two octaves and a third × 0.06);
pitch classes summed. The bass is the lowest semitone up to C5 whose largest bin is 30 % of the largest there (peaks,
not sums: white noise spreads over the more bins of a higher semitone, a tone does not).

### Method

Material is synthesized by `engine/tests/Anode.Dsp.Tests/Analysis/Chords/ChordMaterial.cs`: the golden path's
progression (C, Am7, Fmaj7, G7, Em, A7, Dm7, G/B, a chord a bar) and random progressions (any root; major, minor, 7,
maj7, m7, dim, m7b5, sus4, 6, m6; a quarter with a slash bass from their tones) as MIDI in four voicings (**Piano**: the
bass in octave 3, the chord close above C4; **Close**: stacked from the bass up from G3; **UpperInversions**: Piano with
the right hand on its first or second inversion; **FirstInversion**: the third at the bottom), humanised (starts and
ends ±a 32nd, velocities 50–120, a passing scale tone every other bar), and rendered at 120 bpm through Anode's Electric
Piano, alone, with white noise, or over the analyses' kick-and-hat loop. A chord is right when the one found at its
bar's start (within an eighth, or holding over it) has its root and quality (and bass); "same notes" also counts a
chord of the same tones over the same bass (Am7/C found as C6: a first inversion of a minor seventh *is* a sixth
chord's root position).

Reproduce with `ANODE_CHORD_REPORT=1 dotnet test --project tests/Anode.Dsp.Tests -c Release -- --filter-class
Anode.Dsp.Tests.Analysis.Chords.ChordReportTests` (from `engine/`), which writes
`engine/artifacts/generative/chord-report.md`.

**Machine:** Apple M1 Max (10 cores), macOS 27.2, .NET 10.0.14, Release, measured 2026-11-03.

### Results

The golden path asks for 7 of 8 (root, quality and bass) on MIDI and 7 of 8 (root and quality) on audio.

| Material | Chords | Root and quality | With the bass | Same notes and bass |
|---|---|---|---|---|
| MIDI, progression, Piano | 8 | 8 | 8 | 8 |
| MIDI, progression, Close | 8 | 8 | 8 | 8 |
| MIDI, progression, UpperInversions | 8 | 8 | 8 | 8 |
| MIDI, progression, FirstInversion | 8 | 6 | 6 | 8 |
| MIDI, progression, humanised with passing notes (3 seeds) | 24 | 24 | 24 | 24 |
| MIDI, 40 random progressions × 8 | 320 | 292 (91 %) | 292 (91 %) | 318 (99 %) |
| Electric Piano, progression, Piano | 8 | 8 | 8 | 8 |
| Electric Piano, progression, Close | 8 | 8 | 8 | 8 |
| Electric Piano, progression, UpperInversions | 8 | 8 | 8 | 8 |
| Electric Piano, progression, FirstInversion | 8 | 6 | 6 | 8 |
| Electric Piano, white noise 30 dB below | 8 | 8 | 8 | 8 |
| Electric Piano, white noise 20 dB below | 8 | 8 | 7 | 7 |
| Electric Piano, white noise 15 dB below | 8 | 7 | 6 | 6 |
| Electric Piano, white noise 10 dB below | 8 | 6 | 6 | 6 |
| Electric Piano over drums, the beat tracker's beats | 8 | 8 | 7 | 7 |
| Electric Piano, 10 random progressions × 8 | 80 | 68 (85 %) | 68 (85 %) | 73 (91 %) |

Every MIDI miss on random material is a chord whose notes are another template's (a first-inversion m7 is a 6, an
inverted m6 a m7b5): "same notes" scores them right. Audio misses add a chord change heard a beat late or early where
the decaying piano and the next chord share tones, and, under heavy noise, sevenths heard as triads. Audio recognition
runs at 84× real time (65 s of 48 kHz mono, one thread), the chromagram's median filtering most of it; MIDI is
immediate.

## Tests

`engine/tests/Anode.Generative.Tests` and `engine/tests/Anode.Dsp.Tests/Analysis/Chords`:

- **Determinism:** every style's notes equal across runs, threads and a library loaded from the source folder;
  fingerprints (SHA-256 of the notes) of three fixed inputs pinned, so a change that moves the notes is seen (and
  needs a library version bump); the hash changes with the region's own inputs and not with the song outside it.
- **The pad:** for every style, hits rise with complexity (0, ½, 1) and the mean velocity with intensity (0, ½, 1).
- **Fills:** for every drum style, five pad points and five seeds, the first section's last bar has more hits than
  each groove bar before it and three or more snares, toms or claps in its second half; none without fills.
- **Bass (300 random chord tracks a run, random styles, pads, ranges and switches):** the root or slash bass at every
  chord change's downbeat, every note in range, one note at a time.
- **Keys (300 random chord tracks a run, ranges of two octaves or more):** every note a tone of its chord (or its slash
  bass) but passing notes, which lead into a change and are under 10 %; right hand in range, left hand below it; no
  voice of a whole voicing moves by more than a fifth to the next chord's.
- **Rhythm following:** for every drum style at three complexities, at least 75 % of kicks within a 16th of a bass
  line's onsets (all of them, by construction).
- **Switches, swing, feel, signatures, Convert to Pattern** (the pattern plays the generated notes exactly: each step's
  offset keeps its note's timing).
- **Chord ID:** the golden path's progression in each voicing (MIDI ≥ 7 of 8 with bass, audio ≥ 7 of 8 roots and
  qualities, and with bass), through loose timing and passing notes, noise at 20 and 15 dB, drums on tracked beats, a
  30-cent detuned piano; random MIDI progressions ≥ 90 %; merging, silence and resolution; keys from notes and audio;
  determinism.

## Notes for callers

- **Beats:** pass the project's beat ticks over the range (the last its end) for MIDI, and the bounce's beat frames for
  audio (from the tempo map, or none for the beat tracker). `resolutionTicks` defaults to a beat.
- **Writing the chord track:** a `RecognisedChord`'s root and bass are pitch classes; spell them in the key Chord ID
  found (`DetectedMusicalKey`), and its quality is `ChordTemplates.Qualities[quality]` (equal to `ChordQualities.All`).
- **Convert to Pattern:** `ToPattern` makes rows as long as the region (an 8-bar region is 128 sixteenth steps), past the
  model's 64-step row limit (`Edits.Patterns.MaxRowLength`): the convert job must raise the limit for converted rows,
  or split the region into patterns of at most four bars.
- **The cache:** `Hash(input)` is cheap (no generation); regenerate when it changes.

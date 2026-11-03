# 12 — Phase 11: generative and analysis

From [the plan, §12](01-implementation-plan.md#phase-11-generative-and-analysis-12-weeks) and §11 (Session Players
and Drummer, the chord track, T3 instruments and effects, Environment). Session Players and Chord ID follow
[ADR-0014](adr/0014-session-players-and-chord-id.md); the Environment follows [ADR-0015](adr/0015-environment.md).

**Exit criterion.** A song gets its chords from what is played, players that follow them, a master that meets a
loudness target, the T3 instruments and effects, and an Environment patch between the keyboard and a track. The
Playwright golden path covers it on the null device:
1. **Chord ID and the chord track.**
   - Chord ID on a MIDI region of eight known chords (C, Am7, Fmaj7, G7, Em, A7, Dm7, G/B, one a bar): the chord track
     holds at least 7 of the 8 right (root, quality and bass), each within an eighth note of its bar.
   - The same on an audio bounce of that region played by the Electric Piano: at least 7 of 8 roots and qualities.
   - Edit a chord on the chord track: it is one undo step.
2. **Pitch Source.** A MIDI bass region in C set to follow the chord track: each bar's notes move by its chord's root
   interval (Root mode) or to the nearest chord tone (Chords mode); an audio region in C set to follow: its pitch moves
   by the root interval within 5 cents (Phase 5's pitch tracker on a bounce).
3. **Drummer.**
   - Create a Drummer track, pick a style: an 8-bar region over two arrangement sections is generated.
   - Complexity at three settings (low, middle, high): the hits per bar rise with complexity; intensity at three
     settings: the mean velocity rises.
   - The last bar of the first section holds a fill (more hits than its groove bars; toms or snare rolls).
   - Same parameters and seed: the same notes; Regenerate: different notes.
   - Follow a rhythm source (the bass track): at least 75 % of kicks within a sixteenth of a bass onset.
   - The kit is multi-output: the kick plays on its own strip.
   - Convert to MIDI region and to a pattern region: the notes are the generated notes.
4. **Bass and Keyboard Players** following the chord track:
   - Bass: on every chord change's downbeat the note is the chord's root (or its slash bass); every note within the
     instrument's range.
   - Keys: every chord voicing holds only the chord's tones (passing notes only between chords, below 10 % of notes);
     voices move by at most a fifth between neighbouring chords (voice leading).
5. **Mastering Assistant** on the golden path's mix: analyse; for each of two characters (Clean, Valve) the master
   bounce's integrated loudness is the target (−14 LUFS) within 0.5 LU and its true peak at most −1.0 dBTP; Valve's
   even harmonics on a test tone exceed Clean's by at least 6 dB; the level-matched bypass is within 1 LU of the
   processed loudness.
6. **T3 instruments and effects.**
   - The physical-modelling instrument: notes C2–C6 within 5 cents; lower damping, longer decay (−60 dB time at
     least 1.5×).
   - The tape-replay instrument: a held note stops at its tape's length (8 s); its pitch within 5 cents with wow and
     flutter at zero.
   - Sample Alchemy: load the test sample; the resynthesis plays at the key's pitch within 5 cents in additive and
     granular modes.
   - Saturation: THD rises with drive (three settings, monotone).
   - The Down Mixer on a 5.1 bus to stereo: BS.775 coefficients within 0.1 dB; Binaural Post-Processing on a 7.1.4 bus
     at 90° left: ILD at least 6 dB, ITD 0.5–0.8 ms.
7. **Environment.** Patch physical input → transformer (notes +12) → arpeggiator (up, 1/16) → a track's input, and a
   monitor on the transformer's output. Play a chord through live MIDI while recording: the region holds the chord's
   notes an octave up, arpeggiated on the 1/16 grid; the monitor shows them. A fader object (CC 7 to that track's
   volume) moved on screen changes the strip's volume.
8. Save, close and reopen: everything is intact, and the Session Player regions regenerate identically.

The contracts below are committed code. Each workstream builds against them and changes them only with a note in its
report. A REST change means regenerating `engine/src/Anode.Server/openapi.json`
(`ANODE_UPDATE_OPENAPI=1 dotnet test`) and the editor client (`nx run api-client:generate`).

## Scope

**In:**
- **Chord track and Chord ID:**
  - The chord track: a global track of chords (root, quality, bass) at ticks, and key and scale changes; add, move,
    edit, delete; shown with the other global tracks; Session Players and Pitch Source follow it.
  - Chord ID (a job): from MIDI regions (template matching per beat, smoothed) and from audio regions or files
    (harmonic chroma, beat-synchronous, the same templates and an HMM), writing to the chord track (replace or merge
    over a range). Also writes a region's detected key.
  - Pitch Source: a MIDI or audio region follows the chord track: Off, Root (transpose by the chord's root interval
    from the region's key) or, for MIDI only, Chords (each note to the nearest chord tone, keeping its octave). MIDI
    follows at compile time; audio through the region's transpose (Phase 5's pitch shift or Flex Pitch, per region),
    one transposition per chord segment, crossfaded.
- **Session Players** (ADR-0014):
  - Track kinds: a Drummer track (drums through a Drum Machine Designer kit, multi-output), a Bass Player track and a
    Keyboard Player track (with Synth Player styles), each playing Session Player regions.
  - A Session Player region: player, style, preset, complexity and intensity (the XY pad), fills (amount, and fill at
    section ends), swing, feel (push or lay back), the player's switches (drums: kit pieces on or off with their
    pattern variations, follow a rhythm source track; bass: octave range, slides, dead notes; keys: voicing (close,
    spread, shell), range, left-hand part, rhythm pattern), and a seed. Regions get default parameters from their
    arrangement section kind (intro, verse, chorus, bridge, outro).
  - The Session Player Editor (Logic's, in the bottom panel): style and preset browser, XY pad, the switches, fill
    and swing controls, Regenerate, Follow (chord track, rhythm source), Convert to MIDI, Convert to Pattern (drums).
  - Own styles: at least four drum styles (rock, pop, funk, electronic), three bass styles and three keys styles
    (including one Synth Player), each with phrase banks covering complexity and intensity and fills; generic names.
  - Own kits: three Drummer kits (acoustic-style, electronic, brushes) made by the content builder from Anode's
    instruments, multi-output as DMD kits.
- **Mastering Assistant:** on the master (a built-in effect with its own window): analyse a section or the whole song
  (loudness, true peak, spectral balance against a reference curve per character, dynamics, stereo width); a chain of
  EQ (match to the curve, limited), dynamics (gentle compression), the character's colour (Clean, Valve, Punch,
  Transparent), stereo width, and a true-peak limiter to the loudness target (−14 LUFS by default, editable); level-
  matched bypass; the analysis and settings saved with the project.
- **T3 instruments** (in `Anode.Dsp`, after the T1 and T2 structure):
  - **Physical-modelling instrument** (Sculpture-like): an object (string, bar, membrane, tube; material: stiffness,
    damping, inharmonicity) excited by up to three exciters (pick, bow, strike, blow) at positions, read by two
    pickups; a body resonance; filter, envelopes, LFOs and vibrato as the T2 synths.
  - **Tape-replay instrument** (Mellotron-style): each key plays its own recording from its start, at most the tape's
    length (8 s), with tape wow and flutter, tape speed, start motor time; three tape sets (strings, flutes, choir)
    made by the content builder from Anode's own instruments rendered through a tape model.
  - **Sample Alchemy:** a loaded sample (the user's, or a generated default) resynthesised: granular (position, size,
    density, spray, pitch per grain), additive (partials tracked from the sample, morphable by number and spread) and
    spectral (a smoothed spectral envelope over noise and partials) modes; ADSR and filter.
- **T3 effects:** saturation (ChromaGlow-like: tube, tape, transformer and modern models, drive, tone, mix), the Down
  Mixer as an insert (a surround bus to a smaller layout, BS.775), Binaural Post-Processing as an insert (a surround
  bus to binaural stereo, ADR-0012's renderer), and surround versions where a T1 effect needs per-channel controls
  (the Compressor's and Limiter's channel link: all, or by speaker group).
- **Environment** (ADR-0015): the Environment window (layers, objects, cables), the object set in ADR-0015, the host's
  object graph on the MIDI input path, monitors and faders reported through telemetry.

**Out:**
- An ONNX sequence model for players, or an ONNX chord model (ADR-0014).
- Session Players beyond drums, bass and keys (Logic 11's other players), Drummer's per-drummer "personalities"
  beyond styles, Session Player audio (not MIDI) output.
- Sampled content from outside Anode (ADR-0014).
- The Environment's mixer layer, alias objects, touch tracks, and Environment objects on output paths other than
  MIDI ports.
- Mastering Assistant's reference tracks (matching to a user's song).

## Contracts

| What | Where |
|---|---|
| Chord track: chords (root, quality, bass) at ticks, key and scale changes; Chord ID request and result; Pitch Source (Off, Root, Chords) on a region | `engine/src/Anode.Contracts/Chords.cs`; `Project.ChordTrack`, `Region.PitchSource` |
| Session Players: player kinds, styles and presets (catalogue), a Session Player region's parameters, the XY pad, switches per player, seed; track kinds `drummer`, `bassPlayer`, `keyboardPlayer`; a region kind `sessionPlayer` | `engine/src/Anode.Contracts/SessionPlayers.cs`; `TrackKind`, `Region` |
| Mastering Assistant: character, loudness target, analysis result (loudness, true peak, spectrum, width), settings | `engine/src/Anode.Contracts/Mastering.cs`; the master's insert state |
| Environment: layers, objects (kind, parameters, position), cables; object kinds and their inlets and outlets | `engine/src/Anode.Contracts/Environment.cs`; `Project.Environment` |
| Jobs: Chord ID, Mastering analysis, Session Player convert | `engine/src/Anode.Contracts/Jobs.cs` |
| New commands (chord track edits, Pitch Source, Session Player tracks and regions and their parameters, regenerate, convert, Mastering Assistant settings, Environment edits) | `engine/src/Anode.Contracts/Commands.cs` |
| `/ws`: `environmentMonitor` (object id, the last events), `environmentFader` (object id, value); `mastering` (live loudness and gain reduction) | `Realtime.cs`, mirrored in `models.ts` |
| REST (stubbed): `/project/chords` (get, Chord ID job), `/library/session-players` (styles and presets), `/project/regions/{id}/session-player` (regenerate, convert), `/project/mastering` (analyse, settings), `/project/environment` (get), `/project/environment/objects/{id}/input` (live MIDI into an on-screen object) | `engine/src/Anode.Server/Api/GenerativeApi.cs`, `EnvironmentApi.cs` → `openapi.json` → `@anode/api-client` |
| IPC: the Environment graph in the model load and `setEnvironment`; the telemetry of monitors and faders; the Mastering Assistant's chain parameters | `engine/src/Anode.Ipc/IpcMessage.cs`, telemetry layout |
| Generator (stubs that throw): the generator's entry point (parameters, chord track, sections, rhythm source → events), the style library loader | `engine/src/Anode.Generative/` (new project, server only) |
| Chord ID (stubs that throw): MIDI and audio chord recognition, key detection | `engine/src/Anode.Dsp/Analysis/Chords/` |
| DSP (stubs that throw): the three T3 instruments, saturation, Down Mixer and Binaural Post-Processing inserts, the Mastering Assistant's analysis and chain | `engine/src/Anode.Dsp/Instruments/`, `Effects/`, `Mastering/` |
| Environment objects (stubs that throw): the object graph and every object kind | `engine/src/Anode.Engine/Environment/` |

### Rules the contracts imply

- **Ticks and chords:** the chord track's chords hold from their tick to the next; a region's key is its own (set by
  the user or detected by Chord ID), else the project's key; transposition is by the shortest interval from the
  region's key root to the chord root (−6 to +5 semitones).
- **Generation is deterministic:** the same region parameters, length, seed, chord track and sections over its range,
  rhythm source notes over its range, and style library version give the same events, on every machine. The server
  caches generated events by a hash of those; the host plays them as MIDI.
- **Session Player regions are not MIDI regions** in the model: editing their notes needs Convert to MIDI first; the
  piano roll shows the generated notes read-only.
- **Environment default:** a project without an Environment (all projects before schema 11) routes physical input to
  sequencer input; the editor creates that pair when the window first opens.
- **Environment timing:** objects process at the event's sample; delays and the arpeggiator schedule within the
  host's event queue, bounded; an event the graph cannot schedule in the block is dropped and counted.
- **Mastering Assistant** is an insert on the master like any other (so it bounces, freezes and automates), placed
  last by default; its analysis runs as a job over an offline render of the master before it.
- **Package schema 11:** the chord track, regions' Pitch Source and keys, Session Player tracks and regions, the
  Environment, the Mastering Assistant's analysis in its insert state.

## Workstreams

Five, in parallel, each on its own branch; they meet only at the contracts above.

### A — Engine (`Anode.Ipc`, `Anode.Engine`, `Anode.EngineHost`, `Anode.Host`)

- **Environment graph** in the host on the MIDI input path (ADR-0015): compile the server's flat graph; every object
  kind; monitors and faders to telemetry; live MIDI into an on-screen object; track-input and port outputs; control
  targets through the existing parameter addresses. Zero cost when empty.
- **Inserts that change the channel format** (the Down Mixer and Binaural Post-Processing): the strip's channel count
  after the insert changes, and the rest of the strip, sends and the output follow it (Phase 10's multichannel graph).
- **Mastering Assistant chain** as a built-in master insert, with its live loudness and gain reduction through the
  telemetry; the T3 instruments and effects registered.
- **Tests:** each Environment object at sample accuracy; the golden path's patch (transform, arp) against hand-worked
  events; the empty graph's cost unchanged; no allocations in the callback with a 20-object graph; a format-changing
  insert's routing and PDC.

### B — Server domain (`Anode.Model`, `Anode.Server`)

- **Model:** every new command with validation and inverses; package schema 11 with a migration from 10; the chord
  track; regions' Pitch Source and keys; Session Player tracks and regions (defaults from the section kind); the
  Environment document (validation: no cycles, known kinds, real inlets and outlets); the Mastering Assistant's state.
- **Compiler:** Session Player regions to MIDI through D's generator, cached by input hash, regenerated when a chord,
  section or rhythm source in their range changes; Pitch Source for MIDI (Root, Chords) and audio (per-segment
  transpose); the Environment graph flattened for the host; Drummer tracks as DMD kits.
- **Jobs:** Chord ID through D (MIDI and audio, writing the chord track, one undo step), Mastering analysis (an offline
  render of the master before the assistant, E's analysis, settings into the insert), Convert to MIDI and to pattern.
- **API:** the endpoints above; the style catalogue from D's library.
- **Tests:** properties for the commands; migration; the compiler's Session Player cache and invalidation; Pitch
  Source over chord changes; Environment validation; jobs with doubles; API tests.

### C — Editor (`editor/`; built with `editor/DESIGN.md`)

- **Chord track** in the global tracks (chords drawn as symbols, edit with a popover, key and scale changes), the
  Chord ID command on regions, Pitch Source in the region inspector.
- **Session Players:** the new track kinds in New Tracks; the Session Player Editor (style and preset browser, XY pad
  in `audio-ui`, switches per player, fills, swing, feel, Regenerate, Follow, Convert); Session Player regions drawn
  with their notes, read-only in the piano roll.
- **Mastering Assistant** window (analysis view: spectrum against the character's curve, loudness, true peak, width;
  character, target, bypass).
- **T3 panels:** the three instruments' and the effects' editors (generic editor where enough, custom for the
  physical model's object and pickups and Sample Alchemy's source view).
- **Environment window** with `@xui/node-graph`: layers, object palette, cables, object inspectors, live monitors and
  faders, an on-screen keyboard object.
- **Fake server:** every new endpoint and command; a fake generator (fixed patterns) and fake Chord ID.
- **Tests:** stores; XY pad and chord-track math; the Environment graph store (cables, validation mirrors the
  server's); stories with axe for the XY pad, the chord lane and the Environment nodes; the golden-path spec as
  `test.fixme`.

### D — Generative and harmony (`Anode.Generative`, `Anode.Dsp/Analysis/Chords`, the content builder's styles and kits)

- **Generator** (ADR-0014): the style library format (versioned data in the repository) and loader; phrase selection
  by complexity and intensity; the variation grammar per section kind; fills; swing and feel; humanisation (seeded);
  drums following a rhythm source; bass and keys mapping degrees onto the chord track with voice leading and ranges.
- **Styles:** author the styles in Scope (phrase banks, grammar, presets); kits through the content builder from
  Anode's instruments (Drum Synth and E's physical model, once merged; until then Drum Synth), multi-output.
- **Chord ID:** MIDI and audio recognition and key detection (ADR-0014).
- **Tests:** determinism (same seed, same events; bit-identical across runs); densities and velocities rise with the
  XY pad; fills at section ends; chord-tone, range and voice-leading properties over random chord tracks; rhythm
  following; Chord ID on synthesized material (MIDI and rendered audio, including inversions and noise) with recorded
  accuracies in `docs/generative/README.md`.

### E — T3 DSP and Mastering (`Anode.Dsp`, the content builder's tape sets)

- **Instruments:** the physical-modelling instrument (a modal or waveguide model per object kind; exciters; pickups),
  the tape-replay instrument (and its tape sets through the content builder), Sample Alchemy (granular, additive with
  partial tracking, spectral).
- **Effects:** saturation (four models, oversampled), the Down Mixer and Binaural Post-Processing inserts (Phase 10's
  down-mixer and renderer), channel link groups for the Compressor and Limiter on surround.
- **Mastering Assistant:** the analysis (BS.1770 loudness and true peak from Phase 4's meters, a smoothed spectrum
  against each character's curve, crest factor, width) and the chain (EQ, compression, the character, width, a true-
  peak limiter) whose settings come from the analysis; the target reached by the limiter's input gain.
- **Tests:** golden renders for every new instrument and effect; pitch across the range; the tape's length; THD by
  drive; down-mix coefficients; the assistant reaching its target on varied material (quiet, loud, bass-heavy,
  bright) within 0.5 LU and true peak at most −1 dBTP; no allocations; CPU per voice recorded in
  `docs/instruments/T3.md` and `docs/effects/T3.md`.

Content builder: D owns `SessionPlayerContent.cs`, E owns `TapeContent.cs`; the contracts commit adds both files
with stub entry points and their calls in `Program.cs`, so neither workstream edits `Program.cs`.

## Integration

1. Merge in this order: D, E, A, B, then C.
2. Build the content (styles, kits, tape sets) and install it for the tests.
3. Run the two-process tests: Session Player regions through the compiler into the host, the Environment on live
   MIDI, the Mastering Assistant on a bounce.
4. Run the Playwright golden path for the exit criterion.
5. Fix the seams.

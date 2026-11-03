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

### Contract notes

Where the committed code differs from, or adds to, the shapes planned for this commit, and the shapes chosen where
the plan left latitude:
- **Chord track** (`Chords.cs`): `Chord(id, tick, root, quality, bass?)` with `root` and `bass` as the score's
  `PitchName` (spelled) and `quality` in the score's `ChordSymbol.quality` vocabulary. **`ChordQualities`** names it
  (`""` major, `m`, `7`, `maj7`, `m7`, `dim`, `m7b5`, `aug`, `sus2`, `sus4`, `6`, `m6`, `9`: ADR-0014's templates, in
  that order), with `All`, `IsKnown` and `Intervals(quality)` (real data). **Key and scale changes** are
  `ScaleChange(tick, tonic, scale)` (no id: tick-keyed, as `KeyEvent`), `ScaleKind` (`major | minor | harmonicMinor |
  melodicMinor | dorian | phrygian | lydian | mixolydian | locrian`); **decided:** they are the chord track's own, and
  before the first (or with none) the chord track's key is the project's key track (`settings.keys`, major as `major`,
  minor as `minor`), which stays the key signature track. `ChordTrack(chords, scales)` (`Empty`); `Project.chordTrack`
  (required).
- **Pitch Source and region keys:** the model has no common `Region` type, so **`pitchSource: PitchSourceMode` (`off |
  root | chords`) and `key: RegionKey?` are on both `MidiRegion` and `AudioRegion`** (required; `chords` is MIDI only).
  `RegionKey(tonic: PitchName, mode: KeyMode)` (spelled, as the chords). `PitchSources.PitchClass(pitch)` and
  `RootInterval(from, to)` (−6…+5, **a tritone goes down**) are real and mirrored in `models.ts` (`pitchClass`,
  `rootInterval`). Every existing constructor passes `off`/null; a package without the fields reads as `off`/null (the
  enum's default and a missing nullable), so no interim filler is needed. `RegionTimingVectors.json` is regenerated (it
  serialises regions).
- **Chord ID:** `ChordIdRequest(regionIds, startTick?, endTick?, write = replace, detectKeys = true, resolutionTicks?)`
  (MIDI and audio regions together; left-out range: the regions' span; resolution: a beat). `ChordIdWrite` adds
  **`none`** (keys only) to replace and merge; **merge** adds detected chords only where the chord track has none within
  a beat. The job's result is `ChordIdResult(startTick, endTick, write, chords: DetectedChord(tick, lengthTicks, root,
  quality, bass?, confidence)[], keys: DetectedKey(regionId, key, confidence)[])`, applied by **`applyChordId`** (jobs
  only, one undo step).
- **Session Players** (`SessionPlayers.cs`): `SessionPlayerKind` (`drummer | bass | keyboard`). Track kinds `drummer`,
  `bassPlayer`, `keyboardPlayer`; **decided:** a Drummer track *is* a Drum Machine Designer (`drumMachine` is its kit,
  its pads member instrument tracks: multi-output), Bass and Keyboard Player tracks are instrument tracks
  (`instrument`). **The region kind is its own record and list**, `SessionPlayerRegion(id, trackId, name, startTick,
  lengthTicks, muted, parameters, seed)` in `Project.sessionPlayerRegions`, beside MIDI and pattern regions (there is no
  region-kind enum to add `sessionPlayer` to); deltas `sessionPlayerRegionUpserted`/`sessionPlayerRegionRemoved`. **The
  seed is the region's, not a parameter** (presets carry no seed). `SessionPlayerParameters(styleId, presetId?, pad,
  fillAmount, fillAtSectionEnds, swing, feel, followChords, drums?, bass?, keys?)`: the XY pad is
  `SessionPlayerPad(complexity, intensity)` (0…1), `feel` −1 (lay back) … 1 (push) as a fraction of a 32nd,
  `followChords` (bass and keys; off plays the region's key's tonic chord), and the switches of the region's player
  only: `DrummerSwitches(pieces: DrumPieceSetting(piece, on, variation 0…3)[], rhythmSourceTrackId?)` over `DrumPiece`
  (`kick | snare | hiHat | toms | cymbals | percussion`, each once) — **the rhythm source to follow is a Drummer switch,
  per region**; `BassSwitches(lowNote, highNote, slides, deadNotes)` (the range as MIDI notes, at least an octave,
  24…72); `KeysSwitches(voicing: close | spread | shell, lowNote, highNote, leftHand: off | root | octaves | rootFifth,
  rhythmId?)` (36…96). **`DrumKitNotes`**: the Drummer plays General MIDI percussion notes (kick 36, snare 38, side
  stick 37, clap 39, hats 42/44/46, toms 50/47/45/41, crash 49, ride 51, bell 53, tambourine 54, cowbell 56, shaker 82)
  and its kits' pads sit on them (`Of(piece)`, the main note first).
- **Section kinds:** `SectionKind` (`intro | verse | chorus | bridge | outro`). **Decided: arrangement sections keep no
  stored kind**; `SectionKinds.Of(name)` derives it from the name's start, ignoring case (`pre-chorus` and `hook` are
  choruses, `middle` and `break` bridges, `ending` and `end` outros; anything else, and no section, a verse), mirrored
  as `sectionKind` in `models.ts`.
- **Catalogue:** `SessionPlayerCatalog(version, styles)` (`GET /library/session-players`); `SessionPlayerStyle(id, name,
  player, genre, description, synthPlayer, kitPatchId?, rhythms: SessionPlayerRhythm(id, name)[], presets:
  SessionPlayerPreset(id, name, parameters)[], sections: SectionDefaults(kind, parameters)[])` — a style holds its
  defaults per section kind (every kind once) and the kit patch a new Drummer gets; kits are pack kit patches (the
  content builder's), not catalogue entries. The library's `version` is part of every generation input.
- **Generated notes** (added): `GET /project/regions/{regionId}/session-player` answers `SessionPlayerNotes(regionId,
  hash, notes)` (relative to the region's start, as a MIDI region's; `hash` the cache key) for the editor's read-only
  drawing; refetched after `sessionPlayerRegionUpserted`, or a chord, section or rhythm source change over the region.
  Regenerate is `POST …/session-player/regenerate` (`RegenerateRequest(seed?)` → `CommandResult`); Convert `POST
  …/session-player/convert` (`SessionPlayerConvertRequest(target: midi | pattern, regionIds?)` → a job, applying
  **`convertSessionPlayerRegions`** with `ConvertedSessionPlayerRegion(regionId, target, notes: NewNote[], pattern?)`).
- **Mastering Assistant** (`Mastering.cs`): plugin `anode.mastering-assistant`. **Decided: its settings are its insert
  parameters** (`MasteringParameters`: `character` 0…3 as `MasteringCharacter` `clean | valve | punch | transparent`,
  `targetLufs` −24…−6 (−14), `ceilingDbtp` −3…0 (−1), `eq` and `dynamics` 0…1 (1, the analysis' correction's strength),
  `width` 0…2 (1), `matchedBypass` 0/1), so they automate and bounce as any insert's; **its analysis is
  `Insert.mastering: MasteringAnalysis?`** (a new optional field; null on every other insert):
  `MasteringAnalysis(startTick, endTick, revision, integratedLufs, maxShortTermLufs, loudnessRange, truePeakDbtp,
  crestFactorDb, width (0…1), spectrumDb[], curveDb[])` over **`MasteringBands`**: 30 ISO 266 third-octave centres, 25
  Hz … 20 kHz. The typed view is `MasteringSettings` (`Default`) and `MasteringState(insertId, settings, analysis?)`
  (`GET /project/mastering`); `PUT /project/mastering` (added) takes `MasteringSettings` and applies `setMastering`.
  `MasteringAnalysisRequest(startTick?, endTick?, add = true)` (`add`: put one last on the master first when there is
  none). The chain is not stored: it follows from the analysis and settings (E's `MasteringChainDesigner`).
- **Environment** (`Environment.cs`): the document is **`MidiEnvironment(layers, objects, cables)`** (`Empty`), named so
  to avoid `System.Environment`; `Project.environment` (required). `EnvironmentLayer(id, name)`; `EnvironmentObject(id,
  layerId, kind, name, x, y, parameters, port?, trackId?, address?, conditions?, operations?, taps?, chord?)` — numeric
  settings in `parameters` by id, the rest typed: `port` (physical input: one device, null every input; MIDI port: the
  output), `trackId` (track input; control target, null the master), `address` (control target, an automation address
  such as `volume` or `insert:{id}:{param}`), `conditions`/`operations` (transformer: **Phase 7's `TransformCondition`
  and `TransformOperation` reused**, on `status` (conditions only), `channel`, `pitch` = data 1, `velocity` = data 2,
  not the position, length or time-based ones), `taps` (`DelayTap(delayTicks 1…3840, transpose, velocity)`, at most 16)
  and `chord` (chord memoriser: semitones from the key; null its Chord parameter's). `EnvironmentCable(id, fromObjectId,
  outlet, toObjectId, inlet)` (indices; fan-out and merge allowed, cables cross layers, no cycles). **Object kinds**
  (`EnvironmentObjectKind`, ADR-0015's thirteen; `EnvironmentObjectKinds.All`/`Of`, real data mirrored in `models.ts`):

  | Kind | Inlets | Outlets | Parameters |
  |---|---|---|---|
  | `physicalInput` (at most one without a port) | — | Out | — |
  | `sequencerInput` (at most one) | In | — | — |
  | `keyboard` | — | Out | `channel`, `velocity` |
  | `monitor` | In | Out | — |
  | `fader` | In | Out | `message` (0 CC, 1 note, 2 program, 3 pitch bend), `channel`, `number`, `min`, `max`, `value` |
  | `transformer` | In | Matched, Unmatched | — (conditions, operations) |
  | `delayLine` | In | Out | `dry` (taps) |
  | `arpeggiator` | In | Out | `anode.arpeggiator`'s |
  | `chordMemorizer` | In | Out | `anode.chord-trigger`'s (chord) |
  | `channelSplitter` | In | Channel 1 … Channel 16, Other | — |
  | `midiPort` | In | — | — (port) |
  | `trackInput` | In | — | — (trackId) |
  | `controlTarget` | In | — | — (trackId, address) |

  Live MIDI into an on-screen object: `EnvironmentInputRequest(events: LiveMidiEvent[])` (`POST
  /project/environment/objects/{objectId}/input`, 204; keyboards and faders only).
- **Jobs** (`JobKind`): `chordId`, `masteringAnalysis`, `sessionPlayerConvert`.
- **Commands** (22): chord track `addChords(chords: NewChord(tick, root, quality, bass?)[])`, `editChords(chords)`,
  `deleteChords(chordIds)`, `setScales(scales)`, `applyChordId(result)` (jobs only); Pitch Source
  `setPitchSource(regionIds, mode)`, `setRegionKey(regionIds, key?)` (null clears); Session Players
  `addSessionPlayerTrack(player, styleId?, presetId?, kitPatchId?, pluginId?, name?, index?, regions = true)` (with
  `regions`, a region per arrangement section with its kind's defaults and its own seed, else 8 bars from the start),
  `addSessionPlayerRegion(trackId, startTick, lengthTicks?, parameters?, name?, seed?)`,
  **`setSessionPlayerRegion(regionId, startTick?, lengthTicks?, name?, muted?, styleId?, presetId?, complexity?,
  intensity?, fillAmount?, fillAtSectionEnds?, swing?, feel?, followChords?, drums?, bass?, keys?)`** — flat scalars (an
  XY drag sends two numbers, as `setPanner`), whole switch records (required-nullable in the generated client: send
  `null` for the ones not changed); a preset applies first, any other parameter change clears `presetId`, the seed stays
  — `regenerateSessionPlayer(regionIds, seed?)`, `convertSessionPlayerRegions(regions)` (jobs only); Mastering
  `setMastering(insertId?, character?, targetLufs?, ceilingDbtp?, eq?, dynamics?, width?, matchedBypass?)`,
  `setMasteringAnalysis(insertId, analysis)` (jobs only); Environment `addEnvironmentLayer(name?)`,
  `setEnvironmentLayer(layerId, name?, index?)`, `removeEnvironmentLayer(layerId)` (its objects and their cables),
  `addEnvironmentObject(kind, x, y, layerId?, name?, parameters?, port?, trackId?, address?, conditions?, operations?,
  taps?, chord?)` (no layer: the first, made as `Layer 1` when none), `setEnvironmentObject(objectId, …)` (parameters
  merged; an empty `port`, `trackId` or `address` clears it, an empty `chord` too),
  `removeEnvironmentObjects(objectIds)`, `connectEnvironment(fromObjectId, outlet, toObjectId, inlet)` (422 for a
  duplicate or a cycle), `disconnectEnvironment(cableIds)`. `moveRegion`, `copyRegions` and `deleteRegions` take Session
  Player regions (documented on the region; B implements). Deltas: `chordTrackChanged(chordTrack)`, the two Session
  Player region ones, `environmentChanged(environment)` (the whole document); Pitch Source and keys come as
  `regionUpserted`/ `midiRegionUpserted`, mastering as `masterChanged`.
- **Realtime:** `environmentMonitor` (`EnvironmentMonitorEvent(objectId, events: MonitoredEvent(status, data1, data2,
  sample)[]`, at most 8, oldest first), `environmentFader` (`EnvironmentFaderEvent(objectId, value)` 0…127) and
  `mastering` (`MasteringEvent(insertId, momentaryLufs, shortTermLufs, integratedLufs, truePeakDbtp,
  compressorReductionDb, limiterReductionDb)` — **two gain reductions**, the compressor's and the limiter's), at the
  meter rate; `models.ts` adds them to `ServerMessage` (with `MonitoredEvent` and `MasteringReading`).
- **REST** (`GenerativeApi.cs`, `EnvironmentApi.cs`, `Pending.Phase11`): `GetChordTrack` (`GET /project/chords`),
  `DetectChords` (`POST /project/chords/detect`), `GetSessionPlayerCatalog` (`GET /library/session-players`),
  `GetSessionPlayerNotes`, `RegenerateSessionPlayer`, `ConvertSessionPlayer`
  (`/project/regions/{regionId}/session-player`, `…/regenerate`, `…/convert`), `GetMastering`, `SetMastering`
  (`GET`/`PUT /project/mastering`), `AnalyseMastering` (`POST /project/mastering/analyse`), `GetEnvironment` (`GET
  /project/environment`), `EnvironmentInput` (`POST /project/environment/objects/{objectId}/input`). The editor's
  `AnodeApi`: `chordTrack`, `detectChords`, `sessionPlayerCatalog`, `sessionPlayerNotes`, `regenerateSessionPlayer`,
  `convertSessionPlayer`, `mastering`, `setMastering`, `analyseMastering`, `environment`, `environmentInput`.
- **IPC** (`IpcMessage.cs`): `LoadModelMessage.environment: EngineEnvironment?` (a whole model's; null the default path;
  a per-track segment leaves it), **`SetEnvironmentMessage(environment?)`** and, added for live MIDI into an on-screen
  object, **`EnvironmentMidiMessage(objectIndex, events)`**. `EngineEnvironment(objects)` is flat and ordered so every
  route goes forward: `EngineEnvironmentObject(index, kind: EngineEnvironmentKind, parameters, routes:
  EngineEnvironmentRoute(outlet, to, inlet)[], port?, trackId = −1, control: EngineAutomationTarget?, conditions?,
  operations?: EngineTransformRule(field, op, a, b)[] (contract names), taps?: EngineDelayTap(delayTicks, transpose,
  velocity)[], chord?, telemetrySlot = −1)`; no layers, no contract ids. The Mastering Assistant's chain:
  **`EngineInsert.mastering: EngineMasteringChain(eqGainsDb[30], compressorThresholdDb, compressorRatio, loudnessLufs,
  chainGainDb)`** — what the analysis sets, at full strength; the insert's parameters scale and steer it live, and the
  limiter's input gain is `targetLufs − loudnessLufs − chainGainDb` (at the current strengths).
- **Telemetry** (`TelemetryLayout.cs`): `TelemetryHeader.EnvironmentDropped` (appended); after the channel meters,
  `MaxMonitors` (32) **`EnvironmentMonitorEntry`** (`Object` = index + 1, 0 free, as a new region is zeroed; `Count`
  monotonic; an 8-event ring, event n at `n % 8`, each as its sample and `status | data1 << 8 | data2 << 16`),
  `MaxFaders` (64) **`EnvironmentFaderEntry`** (`Object`, `Value`), then one **`MasteringEntry`** (`Active`, the three
  loudnesses, true peak, the two reductions): `MonitorsOffset`, `FadersOffset`, `MasteringOffset`, `Size`.
- **Stubs that throw** (through `Pending.Workstream…()`, as earlier phases' are):
  - **`Anode.Generative`** (new, server only; references Contracts; `Styles/**/*.json` embedded):
    `GenerationInput(player, parameters, seed, startTick, lengthTicks, chords, sections, signatures, keys,
    rhythmOnsets?)` — **the rhythm source as onset ticks** (MIDI note starts or audio transients, gathered by B);
    `GeneratedNotes(hash, notes)`; `SessionPlayerGenerator(library)` with `Generate(input)`, `Hash(input)` (lowercase
    hex SHA-256 with the library's version), `static ToPattern(notes, lengthTicks, stepTicks = 240)` and the real
    `static PlayerOf(trackKind)`; `StyleLibrary` (`Builtin()`, `Load(folder)`, `Version`, `Catalog`).
    `Anode.Generative.Tests` holds one placeholder test (`PlayerOf`).
  - **`Anode.Dsp/Analysis/Chords`**: `ChordTemplates` (real: qualities and intervals, tested equal to the contract's);
    `ChordNote(startTick, lengthTicks, pitch, velocity)`, `RecognisedChord(start, end, root, quality index, bass,
    confidence)`, `DetectedMusicalKey(tonic, minor, confidence)`; `MidiChordRecognizer.Recognise(notes, beats,
    resolutionTicks, key?)`, `AudioChordRecognizer.Recognise(mono, sampleRate, beats, key?)` (frames),
    `KeyDetector.FromNotes`, `FromAudio`.
  - **T3 instruments** (`IInstrument`, descriptor real, parameters empty for E to fill): `anode.physical-model`
    (`Instruments/Physical/PhysicalModel`), `anode.tape-replay` (`Instruments/Tape/TapeReplay`, loads `.antape` tape
    sets), `anode.sample-alchemy` (`Instruments/Alchemy/SampleAlchemy`, loads `.wav`, `.aif`, `.aiff`; the brief's name
    kept). **Effects:** `anode.saturation` (`Effects/Drive/Saturation`, an `AudioEffect` with its parameters fixed:
    `model` Tube/Tape/Transformer/Modern, `drive`, `tone`, `mix`, `output`); a new **`IFormatEffect`** for inserts that
    change the channel format (planar in and out, `OutputFormat(input)`, `Prepare(input, output, rate, frames)`), with
    `anode.down-mixer` (`Effects/Surround/DownMixerInsert`: `format`, a `SpeakerFormat` index, stereo by default, and
    `lfe` dB) and `anode.binaural` (`Effects/Surround/BinauralInsert`: always stereo, `UseHrtfs(set)`); a format change
    is structural (compiled, not automated). **`Anode.Dsp/Mastering`**: `MasteringBands` (real, tested equal),
    `MasteringCharacterKind`, `MasteringMeasurement`, `MasteringAnalyzer.Analyse(left, right, rate)` and `Curve`,
    `MasteringChainSettings`, `MasteringChainDesigner.Design`, and `MasteringAssistant` (an `AudioEffect`, parameters
    fixed and tested equal to `MasteringParameters`; `SetChain`). **None is registered** in `BuiltinEffects` or
    `BuiltinInstruments`: the contract, golden-render and demo tests instantiate and render every entry; A registers
    them (and the engine creates the `IFormatEffect`s by id) once E's work is in.
  - **`Anode.Engine/Environment`**: `EnvironmentEvent(sample, status, data1, data2, port?)`, `IEnvironmentSink`
    (`SequencerInput`, `TrackInput`, `MidiPort`, `Control`), `IEnvironmentEmitter` (`Emit`, `Schedule`),
    `EnvironmentGraph` (`Empty` and `Compile` of a null or empty graph are real: the default path costs nothing;
    `QueueCapacity` 4096, `Input`, `InputAt`, `Process`, `Publish`, `Reset`, `Dropped`), `EnvironmentNode` with
    `Create(settings)` and one node class per kind. **The namespace `Anode.Engine.Environment` shadows
    `System.Environment` inside `Anode.Engine.*`** (the tests' namespaces included): the existing uses now say
    `System.Environment`, and new code must too.
- **Content builder:** `SessionPlayerContent.AddKits(pack, test, t1)` (D) and `TapeContent.AddTapeSets(pack, test)` (E),
  each behind its `Ready` (false), which `Program.cs` checks, so `scripts/build-content.mjs` runs as before.
- **Interim:** the model refuses the 22 commands and new or imported tracks of the Session Player kinds (and imported
  regions with a Pitch Source or key), 422 (`Edits.Phase11.cs`); projects answer an empty chord track and Environment
  and no Session Player regions (`ProjectState.ToProject`); the identity property skips `RandomProjects.PendingPhase11`,
  and random projects are built without them (`Buildable`). The editor's fake Server answers the commands, the Session
  Player track kinds and the 11 endpoints 422 (`fake-phase11.spec.ts`); its `ProjectStore` keeps `chordTrack`,
  `sessionPlayerRegions` and `environment` and applies their deltas. The Server's JSON leaves `insert.mastering` and
  `region.key` out when null (required-nullable in the generated client, as `movie`; read them `?? null`).
- **Not contracted here:** the Compressor's and Limiter's channel link on surround strips (E adds the parameter; A's
  `MultichannelPolicy.Linked` groups by it).

### What each workstream must know

- **A:** `EnvironmentGraph.Compile` of an empty graph must stay `Empty` (zero cost); write monitors and faders into
  their `telemetrySlot`s (index + 1); `EnvironmentMidiMessage` enters at an object; register the T3 instruments and
  effects (including the `IFormatEffect`s, which change the strip's format after them) and the Mastering Assistant (pass
  `EngineInsert.mastering` to `SetChain`, publish `MasteringEntry`); qualify `System.Environment` in `Anode.Engine`.
- **B:** replace `Edits.Phase11.cs`, `PendingPhase11` and the generators; schema 11 (the chord track, regions' Pitch
  Source and keys, Session Player tracks and regions, the Environment, `Insert.mastering`); section kinds by name;
  compile Session Player regions through `SessionPlayerGenerator` (rhythm sources as onset ticks), the Environment to
  `EngineEnvironment` (route order, telemetry slots, `EngineAutomationTarget`s), and the assistant's chain through
  `MasteringChainDesigner`.
- **C:** `setSessionPlayerRegion` and `setEnvironmentObject` are flat except the switch records (send null); kinds,
  bands, qualities, drum notes and section kinds are values in `models.ts`; generated notes come from
  `sessionPlayerNotes`; read `mastering` and `key` `?? null`.
- **D:** shapes in `Anode.Generative` and `Anode.Dsp/Analysis/Chords` are fixed; `ChordTemplates` must stay equal to
  `ChordQualities`; Drummer notes and kit pads are `DrumKitNotes`; flip `SessionPlayerContent.Ready`.
- **E:** the instruments' parameter lists are yours; Saturation's and the assistant's parameters are fixed (append
  only); `MasteringBands` must stay equal to the contract's; flip `TapeContent.Ready`.

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

### Server domain decisions (workstream B)

- **Seams** (`Anode.Server/Projects/Generative.cs`): `ISessionPlayerLibrary` (D's `StyleLibrary.Builtin()` and
  `SessionPlayerGenerator`, loaded on first use; a library that cannot load answers 503 and the Session Player
  commands 422 for the style they lack), `IChordRecognition` (D's recognisers and `KeyDetector`), `IMasteringAnalysis`
  (E's `MasteringAnalyzer`), and in the model `IMasteringDesigner` (E's `MasteringChainDesigner`). The real classes
  are what `Program.cs` registers: D's and E's work drops in with no code change.
- **Styles in edits:** `Edits.Apply` takes a `SessionPlayerCatalog` (the server passes the library's for the commands
  that pick styles and presets). Tracks keep no style: a new region takes its track's region before it (else the
  track's first, else the player's first style). A Bass or Keyboard Player style's `kitPatchId`, when set, is the
  instrument patch its new tracks get (a Synth Player's synth); else the plugin asked for, else the default synth. A
  Drummer whose kit patch is not installed gets a Drum Synth pad on each of the 17 notes in `DrumKitNotes` (still
  multi-output). `addTrack` refuses the Session Player kinds (`addSessionPlayerTrack` adds them). Bass and Keyboard
  Player tracks count as instrument tracks, a Drummer as a stack, wherever Drum Machine Designers and instrument
  tracks did (strips, MIDI FX, freeze, patches, groups, VCAs).
- **Seeds** come from fresh ids (`IdPrefix.Seed`: the SHA-256 of an id, so tests stay deterministic). A copy keeps its
  seed. A region's name is its section's (else its style's).
- **Generation input** (`SessionPlayers.Input`): the chords, key and scale changes, signatures and key events in the
  region's range with the one sounding at its start; the sections over it with the one before it; the rhythm source's
  unmuted MIDI and pattern note starts, or its audio regions' transients from the files' cached analyses (none until a
  file is analysed). The cache is by `Hash(input)` (at most 4096 regions); a failing generator gives the region no
  notes, logged once per input. The compiler gets the notes by region id and plays them as MIDI regions (a Drummer's
  through its pads); the same note lists are the same models.
- **Pitch Source:** MIDI before the first chord plays as it is; in Chords mode a note goes to the nearest pitch whose
  class is a chord tone (the slash bass counts), a tie down. Audio follows through the pitch shift (the region compiled
  warped), a playlist entry per chord segment crossfaded over 10 ms past each change; a change that would leave a
  segment shorter than four crossfades (40 ms) is dropped. A following track recompiles when the chord or key track
  changes. Live Loops cells do not follow.
- **Chord ID:** the MIDI regions are heard together (their notes in the range, with their playback transpose and
  velocity); each audio region alone (its file's frames over the range, mixed to mono, the range's beats as frames).
  The result is the chords of both in tick order, a MIDI chord first where both start at a tick. The project's key
  track is the recognisers' prior when it has keys. **Spelling:** the recognisers give pitch classes, so roots and
  slash basses are spelled with flats or sharps as the detected key (`KeyDetector` over the notes, or the audio)
  spells: flats in F, B♭, E♭, A♭ and D♭ major and D, G, C, F, B♭ and E♭ minor. Merge keeps a chord already within a
  beat.
- **Mastering analysis:** the master is rendered offline before the assistant by swapping the host's master strip for
  one with the assistant and every insert after it bypassed, at unity gain, for the render, then sending the project's
  strip again (no IPC change; an edit to the master during the render is restored by the next sync). A surround
  master is analysed on its first two channels. Without an assistant, one is added last in the analysis' undo step.
  The chain is designed per analysis and character (`MasteringChains`) and travels in `EngineInsert.mastering`; a new
  chain is a structural strip change (`setChannel`); a designer that fails sends none.
- **Convert to Pattern:** the generator's pattern has one row the region's length, longer than a row may be
  (`MaxRowLength`, 64 steps). The limit stays; `convertSessionPlayerRegions` splits such a pattern into consecutive
  pattern regions of 64 steps (4 bars of 16ths in 4/4; the last what is left), each with its part of every row.
- **Environment:** objects are stored with their kind's parameters at their defaults (the MIDI FX objects' as given);
  the transformer refuses `useMap` (no map) besides the time-based operations; at most 1024 objects, 4096 cables, 64
  layers, 16 taps, 16 chord notes. A removed track's track inputs play nothing and its control targets drive nothing
  (track and address cleared). A whole model carries the Environment; a change to it, or to the tracks or strips it
  names, goes as `setEnvironment`. Monitors and faders take telemetry slots in compiled order (32 and 64; the rest
  report nothing); their events are published when a monitor's count or a fader's value changes, the assistant's
  while the transport plays.
- **Package schema 11:** `project.json` holds `chordTrack` and `environment`, track files `sessionPlayerRegions`;
  regions' `pitchSource` and `key` and an assistant's `mastering` are where they were. Damaged parts read as none
  (chords and cables that fail their checks, a track input to no track, a rhythm source that is gone); Session Player
  regions keep styles the library no longer has.
- **Undo:** labels for every Phase 11 command; an XY pad drag, a Mastering Assistant knob and an Environment object's
  move coalesce into one step.

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

### Integration decisions

Settled when the five workstreams met (the contract changes are marked **contract**):
- **T3 registered.** `Anode.Dsp.T3` and its flag are gone: the instruments are in `BuiltinInstruments`, saturation and
  the assistant in `BuiltinEffects`, the two inserts in `BuiltinFormatEffects`, all in the contract lists (the format
  inserts through `T3ContractTests`, their own path). `Tier3` and every `Pending` are gone. The assistant's and
  saturation's goldens stay `T3GoldenRenderTests`' (they share a file name with `EffectGoldenRenderTests`, which leaves
  them out). `MasteringInsertTests` keeps a double for "one chain per change" under its own id and adds the real class on
  a master; the real Down Mixer and Binaural inserts are in `Phase11TwoProcessTests`.
- **Channel link.** `MultiInsert` numbers the layout's speaker groups and holds, for the Compressor and Limiter, an
  instance per group after the first; `Linked` reads `link` from the insert's own instance each block, so it can move
  without a rebuild. By group, each group has its key, instance and gain.
- **Binaural HRTF.** `monitoring.HrtfPath` is sent only while listening binaurally, so an insert had none otherwise. The
  Server's `HrtfLocator` (split out of `MonitoringService`, same search) fills `EngineInsert.file` for `anode.binaural`.
- **Mastering.** The assistant is in the host's catalogue, so `setInsertParameter` and automation name it as any insert.
  The first-second settling meets the limit: −13.98 and −14.01 LUFS on the golden path's mix. The target is of what the
  analysis measured (open, below).
- **A constant transposition is exact** (replaces the stretcher's for Pitch Source and a region's transpose, in
  Polyphonic and Monophonic). Signalsmith Stretch's own transposition left low tones up to 15 ¢ off (a sine at 130.8 Hz,
  5 semitones down), over the 5 ¢ of step 2; it is a matter of the FFT's bins, in hertz. `TransposingRenderer` stretches
  by the factor at no transposition and resamples with Speed's windowed sinc: 0.01 ¢ on the same tone. Pitch curves,
  formants and gains still go to the stretcher ([docs/flex](flex/README.md)).
- **Chords mode** (replaces B's "the root interval, then the nearest tone"): a note goes to its own nearest chord tone,
  keeping its octave, as the Scope says; Root mode moves by the interval.
- **Environment control** (**contract**): `EnvironmentControlMessage(moves: EngineControlMove(target, value)[])`, host to
  Server, the latest of each target at most every 30 ms. `ControlService` takes each as a control's gesture (volume on
  the fader law, as a surface's), so the model, the editor's strip and undo follow. `AutomationEvents.Address` is
  `Describe`'s inverse. Telemetry's fader value stays what the Environment window shows.
- **Rhythm following in fills.** A fill's kick rolls left the kick off a plain bass (0.36 of kicks on onsets at high
  complexity): in a fill bar only kicks within 180 ticks of an onset stay.
- **Content.** The test pack has all three Drummer kits: the first style is Brushes, whose kit it lacked, so a Drummer
  fell back to Drum Synth pads. The e2e global setup builds the pack when it is missing or older than the content
  builder. Packs stay under `engine/artifacts` (ignored); nothing generated is committed.
- **Editor.** T3 ids, ranges and defaults in the fake are the host's; Sample Alchemy's panel takes its modes by name
  (Additive first), its grain size and spray in milliseconds; the physical model's panel binds the pickup spread and
  body (the host has no pickup levels). The fake's Convert to Pattern splits as the Server does. The XY pad keeps its
  value until the region answers (a held key lost steps), and Regenerate goes through the edits' queue (the next edit
  named a stale revision and got a 409).
- **Golden path**: `generative-analysis-golden-path.spec.ts` is a test. Its Chord ID, Session Player Editor, Mastering
  window, Environment window and physical-model panel steps run on the real Server. Bounces of a track are stems (a
  `mix` ignores `trackIds`), so the mastering steps go through the master with the tone soloed. Its pitch tracker is YIN
  with a spectral refinement.

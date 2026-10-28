# 03 — Phase 2: MIDI and instruments

From [the plan, §12](01-implementation-plan.md#phase-2-midi-and-instruments-8-weeks).

**Exit criterion:** a song made with MIDI plus audio.
- An instrument track plays the built-in synth.
- MIDI is recorded (from a device, or the on-screen keyboard through `/midi/live`).
- Notes are drawn and edited in the Piano Roll, then quantised.
- Playback mixes the synth with audio tracks.
- A sampler track plays an SFZ.
- A CLAP instrument loads and plays.
- An external MIDI track sends its notes to a port.
- Save and reopen keeps every instrument's parameters and state.
- The Playwright golden path covers it on the null device.

The contracts below are committed code. Each workstream builds against them and changes them only with a note in its
report. A change to the REST contract means regenerating `engine/src/Anode.Server/openapi.json`
(`ANODE_UPDATE_OPENAPI=1 dotnet test`) and the editor's client (`nx run api-client:generate`).

## Scope

**In:**
- **Tracks:** instrument and external MIDI tracks.
- **MIDI regions:** notes, controllers, pitch bend, channel pressure and program changes.
  - Non-destructive region playback: transpose, velocity, quantise with strength and swing.
  - Destructive quantise.
  - Region move, split and delete shared with audio regions.
- **MIDI recording:** a new region, or merged into the region under the playhead (`mergeMidiRecording`).
- **Live MIDI:** device input to record-armed tracks, else to the MIDI-thru track (the selected track).
- **External MIDI out.**
- **Instruments:**
  - The built-in `anode.synth` and `anode.sampler`.
  - CLAP instruments hosted in the engine process. This is ADR-0003's opt-in "in-host" mode; the sandbox comes with
    VST3 and AU in Phase 4.
- **Generic parameter editors.**
- **Plugin state in the project.**
- **Piano Roll v1, the on-screen keyboard and musical typing.**

**Out (later phases):**
- MIDI FX, the arpeggiator, Scripter and the Step Editor.
- Event List, Score, articulations, MPE, MIDI 2.0.
- CLAP effects, sandboxed plugins, native plugin editor windows (spike S6), VST3 and AU.
- Note chase on locate: the host sends note-offs for hanging notes on stop, locate and cycle wrap, and does not
  re-trigger notes already sounding.
- Region looping.

## Contracts

| What | Where |
|---|---|
| Track kinds, instrument slot, MIDI I/O settings | `engine/src/Anode.Contracts/Project.cs`, `Plugins.cs`, `Midi.cs` |
| MIDI regions, notes, controls, region playback | `engine/src/Anode.Contracts/Midi.cs` |
| New edit commands (15) | `engine/src/Anode.Contracts/Commands.cs` |
| Deltas `midiRegionUpserted` / `midiRegionRemoved` | `engine/src/Anode.Contracts/Delta.cs` |
| `/ws`: `midiActivity`, `pluginsChanged`, `midiDevicesChanged` | `engine/src/Anode.Contracts/Realtime.cs`, mirrored in `editor/libs/api-client/src/lib/models.ts` |
| REST (stubbed): `/plugins`, `/plugins/rescan`, `/midi/devices`, `/midi/live`, `/midi/thru` | `engine/src/Anode.Server/Api/{PluginsApi,MidiApi}.cs` → `openapi.json` → `@anode/api-client` |
| Server ↔ host messages | `engine/src/Anode.Ipc/IpcMessage.cs` (`EngineTrack.Instrument/Midi`, `RecordingFinishedMessage.MidiTakes`, catalogue, ports, instrument, parameter, MIDI I/O, thru, live MIDI, activity, states) |
| MIDI event kinds in segments | `engine/src/Anode.Ipc/Segments/Layout.cs` (`PlaybackEventKind.NoteOn…ProgramChange`, `PlaybackEvent.Channel`) |
| Instrument interface | `engine/src/Anode.Dsp/Instruments/IInstrument.cs`, `BuiltinInstruments.cs` |

### Rules the contracts imply

- **Ids:**
  - Notes and control events have `long` ids, unique in the project and assigned by the server.
  - A delta carries the whole MIDI region.
  - MIDI region ids are `mid_…`. `moveRegion`, `splitRegion` and `deleteRegions` accept either region kind;
    `resizeRegion` is audio-only and `resizeMidiRegion` MIDI-only.
- **Compilation (server):**
  - Notes become `NoteOn`/`NoteOff` events in samples.
  - Region playback is applied: transpose clamped to 0…127; velocity offset clamped to 1…127; quantise moves note
    starts by `strength` towards the grid, with swing delaying every second grid step.
  - Notes are cut at the region end, and nothing before the region start plays.
  - On one track, later-starting regions win where they overlap, as with audio.
  - At equal samples, `NoteOff` sorts before `NoteOn`. Channels are 0…15 in segments and 1…16 in the domain.
- **Instrument tracks:**
  - A new instrument track gets `anode.synth` unless `pluginId` says otherwise.
  - `setInstrument` resets the parameters to the plugin's defaults.
  - `instrument.parameters` always holds every parameter by id.
  - Parameter ids come from `InstrumentParameter.Id`, or for CLAP from `clap_param_info.id` as a decimal string.
- **State:**
  - On save, the server sends `requestInstrumentStates`.
  - The host answers with `instrumentStates`, and the server writes each non-null state to
    `Plugin States/<trackId>.bin`.
  - On load, it passes the state back in `EngineInstrument.State`.
  - `instrument.file` is relative to the project when it is inside the project.
- **Live MIDI:**
  - Device input goes to every record-armed instrument or external MIDI track whose `midiInput` matches.
  - When none is armed, it goes to the MIDI-thru track.
  - `/midi/live` plays on the named track.
  - While recording, a record-armed track keeps what it receives (device or live) as a MIDI take, in project samples,
    shifted back by the device's input latency.
- **Recording:**
  - `recordingFinished.midiTakes` becomes one undoable "Record" batch, together with any audio takes.
  - A take becomes a new region spanning the pass, rounded out to bars.
  - With `mergeMidiRecording` on, the notes are added to the region under the start of the pass instead.
- **External MIDI:**
  - The host sends the track's events to `midiOutput.device` on its channel, timestamped to the buffer's output time.
  - It sends note-offs for hanging notes on stop, locate and cycle wrap.
- **Activity:** the host batches live MIDI it received or played (≤ 30/s), and the server relays it as `midiActivity`
  with track ids.
- **Catalogue:**
  - The host sends `pluginCatalog` after its hello (built-ins first, then CLAP) and after `scanPlugins`.
  - `/plugins` answers the last catalogue, or an empty list without an engine.
  - CLAP folders: `~/Library/Audio/Plug-Ins/CLAP`, `/Library/Audio/Plug-Ins/CLAP`, `%COMMONPROGRAMFILES%\CLAP`,
    `%LOCALAPPDATA%\Programs\Common\CLAP`, `~/.clap`, `/usr/lib/clap`, plus `CLAP_PATH`.

## Workstreams

Four, in parallel, each on its own branch, meeting only at the contracts above.

### A — Engine host (`Anode.Audio`, `Anode.Engine`, `Anode.EngineHost`, `Anode.Host`, `engine/native`)

- **MIDI I/O:**
  - Port Cathode's `NativeMidi` / `MidiInput` (over `cth_midi_*`) with port hotplug.
  - Timestamps go to device frames.
  - Output through `cth_midi_send`, timestamped.
- **Per-track instruments through `IInstrument`:**
  - Built-ins come from `BuiltinInstruments`.
  - CLAP plugins are wrapped in it: CLAP's C ABI from C# with function pointers, the CLAP headers pinned and fetched
    as `cth_nam` was, the plugin scan, and the host extensions instruments need (params, state, note ports, audio
    ports, latency, thread check, log).
  - The engine builds an instance on the control thread and swaps it in.
- **Instrument tracks in the render graph:** segment MIDI events and live/thru input are rendered into the instrument
  per buffer at sample offsets. Its output feeds the track's strip.
- **Note bookkeeping:** all-notes-off on stop, locate and cycle wrap.
- **MIDI recording:**
  - Per armed track, from device and `liveMidi`.
  - Takes are compensated for latency.
  - They are reported in `recordingFinished`.
- **Other messages:** parameters (control thread → audio thread queue), states, catalogue, ports, activity batches.
- **A test CLAP plugin** in `engine/native` (a small C sine instrument with two parameters and state) used by the
  tests.
- **Constraints:** zero allocations and no locks on the callback with instruments active, enforced by a test. Callback
  cost measured with 16 synth tracks.

### B — Server domain (`Anode.Model`, `Anode.Server`)

- **Model:**
  - Every new command, with validation and inverses.
  - Remove `RandomProjects.PendingPhase2` and the placeholder generators, and write generators that apply.
  - `setParameter` coalescing.
  - MIDI regions in the package (in each track's file), with a schema-2 migration from Phase 1 packages.
- **Compiler:** MIDI regions → events per the rules above, incremental per track.
- **Server:**
  - Implement the stubs in `PluginsApi` and `MidiApi`.
  - The catalogue from `pluginCatalog`, with `pluginsChanged`.
  - Ports from `midiPorts`, with `midiDevicesChanged`.
  - Sync instrument, parameter, MIDI I/O and thru to the host.
  - Live MIDI.
  - Instrument states on save and open.
  - `midiTakes` → regions (new or merged).
  - `midiActivity` relay.
  - Default instrument parameters from the catalogue.
- **`FakeHost`:**
  - Serves a catalogue built from `BuiltinInstruments.All` plus a fake CLAP entry.
  - Accepts the new messages.
  - Fakes MIDI takes when recording (a few notes).
  - Emits activity for live MIDI.
- **Tests:**
  - Properties for every new command.
  - Package migration from a Phase 1 package.
  - Compiler tests for every playback rule.
  - API tests for the new endpoints.
  - Host-link tests for the new messages.

### C — Editor (`editor/`)

- **Tracks:**
  - The add-track menu with kind (audio / instrument / external MIDI).
  - MIDI regions in the Tracks area: note miniatures, move, split, delete and trim (`resizeMidiRegion`).
  - Pencil (double-click) creates an empty MIDI region.
- **Piano Roll v1 in the Editors pane, built on `@anode/timeline`:**
  - A keyboard column and a grid with snap.
  - Notes coloured by velocity.
  - Pointer (select, marquee, move, resize, ⌥-copy), pencil, eraser.
  - Velocity lane.
  - ↑/↓ transpose, Q quantise to the grid menu.
  - Several regions of one track at once.
- **Inspector:**
  - MIDI region playback: transpose, velocity, quantise, strength, swing.
  - Track: instrument picker from `/plugins`, a generic parameter editor (knobs and choices from `ParameterInfo`, edits
    as coalesced `setParameter`), "Load file…" for instruments with `fileTypes` (desktop dialog; browser: server path),
    and MIDI input and output pickers from `/midi/devices`.
- **Live input:**
  - The on-screen keyboard and musical typing (A W S E D F T G Y H U J K; Z/X octave) send `/midi/live` to the
    selected track.
  - Selection changes call `/midi/thru`.
  - `midiActivity` lights the keyboard and a track activity dot.
- **Control bar:** the merge-recording toggle.
- **Fake server:** implements every new command and endpoint; remove the placeholders.
- **Tests:** stores, Piano Roll hit-testing and snapping, stories with axe for the new audio-ui parts, and the golden
  path extended per the exit criterion (`test.fixme` until integration).

### D — Instruments DSP (`Anode.Dsp`, `engine/tests/Anode.Dsp.Tests`)

- **`anode.synth`, subtractive:**
  - Polyphony: 16 voices, with voice stealing (oldest released, then oldest).
  - Oscillators:
    - Two oscillators: saw, square/pulse with width, triangle, sine, noise. Band-limited with PolyBLEP.
    - Octave and fine tune per oscillator, plus mix.
  - Filter: 24 dB low-pass (ladder or cascaded SVF) plus HP and BP, with resonance, key tracking and envelope amount.
  - Envelopes: amp and filter ADSR.
  - LFO: sine, triangle, saw, square or S&H. Rate, routed to pitch, filter or amp; the mod wheel scales it.
  - Glide, unison 1–4 with detune, velocity sensitivity, pitch-bend range.
  - Parameters smoothed.
- **`anode.sampler`:**
  - **SFZ subset:**
    - Structure: `<control>`, `<global>`, `<group>`, `<region>`; `default_path`.
    - Mapping: `sample`, `key`, `lokey`, `hikey`, `pitch_keycenter`, `lovel`, `hivel`, `tune`, `transpose`.
    - Level: `volume`, `amp_veltrack`, `pan`.
    - Playback: `offset`, `loop_mode` (no_loop, one_shot, loop_continuous, loop_sustain), `loop_start`, `loop_end`.
    - Envelope: `ampeg_attack`, `ampeg_hold`, `ampeg_decay`, `ampeg_sustain`, `ampeg_release`.
    - Round robin: `seq_length`, `seq_position`.
    - Note names (`c4`).
  - **A single WAV** maps chromatically around its root (`smpl` chunk when present, else C3).
  - **Engine:** 32 voices, interpolating playback (cubic), samples loaded into memory on `LoadFile`.
  - **Parameters:** volume, tune, attack, release, velocity sensitivity.
- **Both instruments:**
  - Stable parameter ids, a state that round-trips, zero allocations in `Process`, and `Reset` silences at once.
  - Fill `BuiltinInstruments.All` / `Create`.
- **Tests:**
  - Zero-allocation processing.
  - Pitch accuracy (FFT, ±1 cent).
  - Envelope timing.
  - Voice stealing.
  - Filter response at cutoff.
  - SFZ parsing and mapping (velocity layers, round robin, loops).
  - State round-trip.
  - Golden render hashes.

## Integration

1. Merge A, B, C and D.
2. Run the two-process tests: the real host with built-ins, the test CLAP plugin, and MIDI recording through
   `/midi/live` on the null device.
3. Run the Playwright golden path extended to MIDI.
4. Fix the seams.

## Status (2026-10-28)

**Exit criterion met on macOS.** The Playwright MIDI golden path passes against the real desktop app on the null device:
- an instrument track with the synth and a parameter change;
- recording from the on-screen keyboard;
- Piano Roll pencil and quantise;
- a sampler loading an SFZ;
- the engine's test CLAP instrument;
- an external MIDI track sending to a port;
- playback;
- save and reopen with parameters and the sampler file kept.

The Phase 1 golden path and the smoke test still pass.

| Check | Result |
|---|---|
| Engine tests (`dotnet test --solution engine/Anode.slnx`) | 392 pass, 2 skipped (D's opt-in demo and CPU renders); stable over repeated runs |
| Editor (`nx run-many -t typecheck lint test build`) | 5 projects pass; Storybook + axe 36 stories |
| e2e (`nx e2e desktop`) | MIDI golden path, Phase 1 golden path, smoke |
| Skala, Prettier, generated client | clean |
| Callback, CoreAudio, 64 frames, 16 × `anode.synth` | p50 141 µs, p99 160 µs, worst 204 µs of 1,333 µs; with 128 audio tracks added, worst 251 µs; 0 allocations |

**Decided at integration:**
- New tracks are numbered by kind, as Logic numbers them: the first instrument track is "Inst 1" whatever else the
  project holds.
- `anode-host --virtual-midi` offers one virtual MIDI input and one output instead of the OS's ports. The desktop app
  passes it when `ANODE_VIRTUAL_MIDI=1`, and the e2e sets it with `CLAP_PATH` pointing at the test CLAP plugin.
- The host's playback-pace test asserts that the playhead moves and never runs ahead of real time. How far a starved
  null clock falls behind is a property of the machine, not the engine.

**Contract and vendored changes:**
- `cth_audio`'s `cth_midi_event` carries its `source` port, and `cth_midi_send_at` schedules output. Both are noted in
  `engine/native/cth_audio/VENDORED.md` for Cathode; the ALSA and WinMM edits were not compiled here.
- Recorded MIDI is shifted back by one buffer plus the output latency, so notes land where the player heard the music.

**Open:**
- Plugin latency compensation (reported, not applied).
- CLAP `request_restart` (only logged).
- `SaveState` is called while the instrument may be processing: allowed for CLAP, and the built-ins should be
  confirmed safe.
- Controller, pitch-bend and pressure editing UI.
- Physical MIDI devices on real hardware.
- Linux and Windows.
- An instrument-file upload endpoint for remote browsers.
- On-screen keyboard colours in the light theme.

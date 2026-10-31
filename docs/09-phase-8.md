# 09 — Phase 8: control and sync

From [the plan, §12](01-implementation-plan.md#phase-8-control-and-sync-6-weeks), §8 (MIDI, Control surfaces, Sync)
and §11 (the rows marked 8). The Stem Splitter follows [ADR-0010](adr/0010-stem-splitter.md).

**Exit criterion.** Anode can be played and mixed from hardware, follows and leads other gear in time, and splits a
recording into stems. The Playwright golden path covers it on the null device, with `anode-midiprobe` (a test tool,
workstream A) standing in for hardware on Anode's virtual MIDI ports and in the Link session:
1. **Controller assignments.**
   - Open Controller Assignments and turn Learn on.
   - Select track 2's volume and move a knob (the probe sends CC 21). The assignment is made.
   - Make a second one: a button (note 36) toggles "selected track mute".
   - Turning the knob changes track 2's volume. It changes only the volume: the CC does not reach the track's
     instrument.
   - With the track's automation in Latch, a pass with the knob writes the knob's moves as volume automation.
2. **Smart Controls from hardware.** Learn knob 1 of the selected track's Smart Controls to CC 22. Moving it moves the
   mapped parameter.
3. **Mackie Control.** Add a Mackie Control on the probe's port pair. The probe plays the surface:
   - The faders of the first eight tracks arrive as their volumes (14-bit pitch bend), and the scribble strips show the
     track names.
   - Moving fader 1 (with touch) changes track 1's volume.
   - Play, Stop and Record work.
   - Bank right shows tracks 9–16 on the strips.
   - Meters arrive while playing, and the timecode display shows the playhead.
4. **HUI.** Swap the surface to HUI. The ping is answered, and fader 1 moves track 1's volume.
5. **OSC.** Over UDP:
   - `/anode/transport/play` starts playback, and `/anode/track/1/volume 0.5` sets track 1's volume.
   - A client that subscribed receives `/anode/track/1/volume` when the volume changes in the editor.
6. **MIDI Clock and MTC out.** With clock and MTC sent to the probe at 120 bpm:
   - Clock ticks are 20.833 ms apart within 0.5 ms of jitter.
   - Start, Stop and Song Position Pointer follow the transport, including a locate.
   - MTC quarter frames decode to the playhead within one frame.
7. **MIDI Clock and MTC in.**
   - The probe sends clock at 100 bpm with Start: Anode follows it, its tempo within 0.5 bpm, and stops on Stop.
   - The probe sends MTC from 00:00:10:00: Anode chases to the matching position within one frame and plays.
8. **Ableton Link.** Turn Link on.
   - The probe joins at 128 bpm; Anode's tempo becomes 128.
   - A tempo change in Anode reaches the probe.
   - Playing, Anode's beat phase matches the probe's within 1 ms at the same host time.
9. **Stem Splitter.** On an audio region of a mix made from known stems (built by `scripts/build-content.mjs` from
   Anode's own instruments), split into vocals, drums, bass and other.
   - Four new tracks appear under the region's track, with regions at the same position.
   - Each stem matches its source with an SDR of at least 6 dB (drums, bass) and 3 dB (vocals, other).
10. Save, close and reopen. Assignments and surfaces (user settings) and the project's sync settings are intact.

The contracts below are committed code. Each workstream builds against them and changes them only with a note in its
report. A REST change means regenerating `engine/src/Anode.Server/openapi.json`
(`ANODE_UPDATE_OPENAPI=1 dotnet test`) and the editor client (`nx run api-client:generate`).

## Scope

**In:**
- **Virtual MIDI ports:** Anode publishes a virtual input and output ("Anode In", "Anode Out") through CoreMIDI; WinMM
  has none, so on Windows they are absent (the Windows MIDI Services API is later).
- **Control routing:**
  - A port given to a control surface is a control port: its messages go to the surface, never to tracks.
  - On any other port, a message that matches a controller assignment is consumed; the rest play the track as before.
  - In Learn mode, messages still play, and the last one is offered for learning.
- **Controller Assignments** (Logic's, simplified):
  - An assignment maps an input (port or any, channel, a CC (7-bit, 14-bit pair or NRPN), a note, pitch bend or
    program change) to a target, with a mode: absolute, relative (two's complement, signed bit, binary offset), toggle
    or momentary; a range; and pickup (the control has no effect until it passes the target's value).
  - Targets use the automation addresses (`volume`, `pan`, `send:…`, `insert:…:…`, `instrument:…`,
    `midiEffect:…:…`) on a fixed track or on the selected track; `smartControl:{knobN}` on the selected track; track
    switches (mute, solo, record arm, select next or previous); the transport (play, stop, record, cycle, rewind,
    forward, go to start); and key commands by id.
  - Zones and modes: assignments live in modes within zones; an assignment can switch a zone's mode.
  - Learn mode: select a target in the editor (or "Learn" on a control), move the control, done.
  - Stored in the user settings, not the project, as in Logic.
- **Control surfaces:**
  - **Mackie Control** and **Mackie Control XT** (extenders add eight strips each): 14-bit motor faders with touch,
    VPots with LED rings, select, solo, mute and record buttons with LEDs, the two-line scribble strip, meters, the
    timecode and assignment displays, banking (8 and 1), transport, jog, cursor and zoom, automation mode buttons,
    name/value display. VPot modes: pan, sends, and plug-in (the selected track's insert, its parameters paged by 8).
  - **HUI:** the ping, zones and ports, faders with touch, VPots, the 4-character strips, meters, transport, banking.
  - A surface's state follows the project live (names, volumes, mutes, meters), and the surface acts on it.
- **OSC:**
  - A UDP server (default port 8000), advertised as `_osc._udp` over Bonjour on macOS.
  - Anode's address space (documented in `docs/control/osc.md`): transport, tracks by index and id (volume, pan, mute,
    solo, arm, sends, names), the selected track, markers, Smart Controls, key commands.
  - Clients subscribe (`/anode/subscribe port`) for feedback; values are sent on change, at most 30 a second per
    address.
- **Sync:**
  - **MIDI Clock out** (24 ppq, Start, Stop, Continue, Song Position Pointer on locate), sample-accurate through the
    host's scheduled MIDI send, with a per-destination delay compensation.
  - **MTC out** (24, 25, 29.97 drop, 30 fps; quarter frames while playing, a full frame on locate).
  - **MIDI Clock in:** Anode follows the external tempo (a smoothed estimate) and Start/Stop/Continue/SPP.
  - **MTC in:** Anode chases the timecode, with a project start offset, and stops when it stops.
  - One sync source at a time: internal, MIDI Clock, MTC or Link.
  - **Ableton Link:** tempo, beat phase at the project's quantum (a bar), and start/stop sync (optional).
- **Stem Splitter** per ADR-0010: on an audio region, into vocals, drums, bass and other, or the six stems (adding
  guitar and piano). It runs as a job with progress. The stems are new files and new tracks below, and the source
  region is muted (an option keeps it).

**Out:**
- LTC, EuCon, video sync.
- MIDI 2.0 / UMP, MPE (later in the plan).
- The Logic Remote equivalent (`remote` route, Phase 10).
- Controller assignment import from Logic.
- Surfaces beyond MCU, MCU XT and HUI (Novation, Push and others use generic assignments).
- Stem Splitter on MIDI, or of a whole project.

## Contracts

| What | Where |
|---|---|
| Controller assignments: inputs, targets, modes, zones, the Learn state | `engine/src/Anode.Contracts/Control.cs` |
| Control surfaces: kind (`mackieControl`, `mackieControlXt`, `hui`), ports, order, settings | `engine/src/Anode.Contracts/Control.cs` |
| OSC settings (port, on/off, clients) | `engine/src/Anode.Contracts/Control.cs` |
| Sync: source, MIDI Clock and MTC destinations, frame rate, offset, Link (on, start/stop sync); the sync state (locked, external tempo, peers) | `engine/src/Anode.Contracts/Sync.cs`; `ProjectSettings.Sync` |
| Stem Splitter: the request (region, stems, keep source), the job kind | `engine/src/Anode.Contracts/Jobs.cs` |
| New commands: `setSyncSettings`, and the stem result (applied by the job, like `setFileVersion`) | `engine/src/Anode.Contracts/Commands.cs` |
| `/ws` events: `controlLearn` (the last message while learning), `sync` (state), `surfaces` (connected surfaces) | `Realtime.cs`, mirrored in `models.ts` |
| REST (stubbed): `/control/assignments` (get, put), `/control/learn` (start, stop), `/control/surfaces` (list, add, remove, reorder), `/control/osc` (get, put); `/project/sync`; `/project/regions/{regionId}/split-stems` (a job); `/system/midi` gains the virtual ports | `engine/src/Anode.Server/Api/ControlApi.cs` → `openapi.json` → `@anode/api-client` |
| IPC: `setControlRouting` (server → host: control ports and the matchers whose messages are consumed; learn on/off), `controlMidi` (host → server: consumed messages, timestamped), `sendMidi` (server → host: surface feedback to a port), `setSync` (server → host: source, destinations, Link), `syncState` (host → server) | `engine/src/Anode.Ipc/IpcMessage.cs` |
| Native: virtual MIDI ports in `cth_audio` (`cth_midi_virtual_open`, `close`); `an_link` (Link's `abl_link` C API, built with the SDK) | `engine/native/cth_audio/include/cth_audio.h`; `engine/native/an_link/include/an_link.h` |
| Control protocols: MCU, HUI, assignments engine, OSC codec and address space (stubs that throw) | `engine/src/Anode.Control/` (new project, no dependencies beyond Contracts) |
| Sync engines: MIDI Clock and MTC encoders and decoders, the tempo estimator (stubs that throw) | `engine/src/Anode.Engine/Sync/` |
| Stem separation: the model runner, chunking and overlap-add (stubs that throw) | `engine/src/Anode.Stems/` (new project, referenced by the server only, so the host never loads ONNX Runtime) |

### Rules the contracts imply

- **Ports** are named by their `cth_midi_port` uid; the virtual ports have fixed uids (`anode:in`, `anode:out`).
- **Routing:**
  - The host decides per message, on the MIDI thread, from the server's matcher table: a control port's messages and
    matching messages go to the server as `controlMidi`; others go to tracks.
  - Matchers are (port or any, status nibble, channel or any, data1 or any). 14-bit CC and NRPN matchers consume
    their whole message family (MSB, LSB; 99, 98, 6, 38).
  - While learning, the host forwards everything as `controlMidi` and also plays it.
- **Assignments:**
  - The server runs them (workstream D's engine). A control move becomes the same change an editor's would be: a
    command, coalesced into one undo step per gesture (a gesture ends 500 ms after the last move, or on touch release),
    and recorded as automation when the track's mode writes.
  - Selected-track targets follow the selection, which the editor reports to the server (`PUT /project/selection`,
    added if missing).
  - Pickup holds until the control's value crosses the target's.
- **Surfaces:**
  - The server holds each surface's state (D's protocol classes: bank offset, VPot mode, display) and sends feedback
    through `sendMidi` only for what changed, faders and LEDs at most 30 times a second, meters 10.
  - Extenders follow the main surface's bank, in `order`.
  - Faders send their value while touched; moves from the project while touched are not sent back.
- **OSC:** the server listens; values are normalised 0…1 unless the address says otherwise (`…/db`, `…/name`).
- **Sync:**
  - The host owns the clock: it generates clock and MTC in the audio callback from the transport, and sends with
    `cth_midi_send_at` at the sample's host time plus the destination's delay.
  - Following external clock or MTC, the host drives the transport (tempo, position, start, stop); the server sees the
    usual transport state and `syncState`. Tempo changes from clock are not written into the tempo map.
  - Link runs in the host: the audio thread reads the session state each block (`captureAudioSessionState`) and aligns
    the transport's beat to Link's at the output's host time. Tempo changes from either side go both ways.
  - The sync settings are the project's (`ProjectSettings.sync`), as in Logic.
- **Stem Splitter:**
  - The job reads the region's file over the region's span, runs the model in chunks with overlap, and writes one
    file per stem into the project's `Audio Files`.
  - One undoable command then adds the tracks (below the source track, in stem order, with the source's output) and
    regions, and mutes the source region unless kept.

## Workstreams

Five, in parallel, each on its own branch; they meet only at the contracts above.

### A — Engine (`cth_audio`, `an_link`, `Anode.Engine`, `Anode.EngineHost`, `Anode.Host`, `tools/Anode.MidiProbe`)

- **Virtual ports** in `cth_audio` (CoreMIDI `MIDISourceCreate`/`MIDIDestinationCreate`), listed with the others.
- **Routing:** the matcher table, control ports, learn forwarding, `controlMidi` with host timestamps; `sendMidi`.
- **Sync** (`Anode.Engine/Sync`):
  - MIDI Clock and MTC encoders, driven by the transport per block, scheduled to the sample.
  - Decoders and a tempo estimator (a second-order loop over clock intervals) that drive the transport; MTC chase.
  - **Link:** `an_link` from the Link SDK (pinned, fetched by `build.sh`, GPLv2-or-later; add it to ADR-0008's
    table), and the transport's alignment to it.
- **`anode-midiprobe`:** a command-line test tool that opens Anode's virtual ports (or a named pair), sends scripted
  messages, records what arrives with host timestamps as JSON, joins a Link session at a tempo and reports its beat at
  given host times. The e2e golden path drives it.
- **Tests:**
  - Routing: control ports never reach tracks; matched messages are consumed; learn plays and forwards.
  - Clock out: tick spacing and jitter at several tempi and buffer sizes, through a tempo ramp; SPP after a locate.
  - MTC out at each frame rate; MTC in chases within a frame.
  - Clock in: tempo within 0.5 bpm after 2 beats, through a step change.
  - Link: two `an_link` instances in one test agree on tempo and phase.
  - No allocations in the callback with sync running.

### B — Server domain (`Anode.Model`, `Anode.Server`)

- **Model:** `setSyncSettings` and the stem result command, with validation and inverses; package schema 8 with a
  migration from 7; remove the `PendingPhase8` placeholders.
- **Control service:** user settings for assignments, surfaces and OSC; the matcher table pushed to the host whenever
  they change; `controlMidi` into D's assignment engine and surface protocols; their actions as commands and transport
  calls, coalesced per gesture; automation recording from control moves; feedback (`sendMidi`) from project deltas and
  meters at the rates above; Learn over REST and `/ws`.
- **OSC server:** the UDP listener and sender around D's codec and address space; subscriptions; Bonjour.
- **Sync:** the project's settings to the host (`setSync`); `syncState` to `/ws`.
- **Stem Splitter job:** through E's runner; the result command.
- **`FakeHost`:** the new messages; injects `controlMidi` from tests.
- **Tests:** properties for the commands; migration; assignment flows end to end with `FakeHost` (learn, absolute,
  relative, pickup, selected track, gesture coalescing, automation writing); a Mackie Control session against D's
  vectors; OSC round trips over real UDP; the stem job on a small fixture model; API tests.

### C — Editor (`editor/`; built with `editor/DESIGN.md`)

- **Controller Assignments window:** Learn mode (a banner, the last message, the target from the editor's
  selection), the list of assignments by zone and mode, and the inspector for the input, target, mode, range and
  pickup.
- **"Learn Assignment for …"** in the context menu of faders, knobs, Smart Controls and plug-in parameters.
- **Control Surfaces setup:** add a surface by kind, its ports, order (extenders), and its state (connected or not).
- **OSC settings:** on/off, port, the address of this machine, subscribed clients.
- **Sync:** the project's Synchronization settings (source, clock and MTC destinations, frame rate, offset, Link);
  the control bar's sync indicator (locked, external tempo) and a Link button showing peers.
- **Stem Splitter:** on an audio region's context menu and the Edit menu: the stems, keep source, job progress.
- **Fake server:** every new endpoint and event; remove the placeholders.
- **Tests:** stores and the learn flow; the assignment inspector; stories with axe for the new parts; the golden-path
  spec, as `test.fixme`.

### D — Control protocols (`Anode.Control`)

- **Assignments engine:** input matching (7-bit, 14-bit, NRPN, notes, pitch bend, program), modes (absolute,
  relative kinds, toggle, momentary), range, pickup, zones and modes; the matcher table it implies; output as target
  changes.
- **Mackie Control and XT:** decode (faders, touch, VPots, buttons, jog) and encode (fader positions, LEDs, rings,
  scribble strips, meters, timecode and assignment displays); the surface model (banking across extenders, VPot modes,
  plug-in paging, name/value), from a view of the project the server supplies.
- **HUI:** the same over HUI's zone/port scheme and its ping.
- **OSC:** the 1.0 codec (messages and bundles; int, float, string, blob, true/false) and Anode's address space as a
  table of addresses to targets.
- **Tests:** byte vectors for each protocol, written to `engine/tests/Anode.Control.Tests/McuVectors.json` and
  `HuiVectors.json` (B tests against them); banking and paging; every assignment mode; OSC encode/decode round trips.
- **Doc:** `docs/control/README.md` (surfaces, what each button does) and `docs/control/osc.md` (the address space).

### E — Stem Splitter (ADR-0010)

- **Model:** export HTDemucs (4 and 6 stems) to ONNX from the official weights with a script
  (`scripts/export-demucs.py`); STFT and iSTFT stay outside the graph, in C#. Record the weights' licence in ADR-0010
  and ADR-0008. The model files are not in the repository: they are published as release assets and fetched by
  checksum (`scripts/fetch-models.mjs`, and on first use by the server into the user library).
- **Runner:** ONNX Runtime through `Microsoft.ML.OnnxRuntime` (MIT), CPU, with CoreML on macOS when it is faster; the
  STFT, chunking with overlap-add and the model's sample rate (resample in and out with the Phase 7 resampler).
- **Content:** a test mix from known stems for the golden path, built by `scripts/build-content.mjs`; a tiny fixture
  model (a few KB) that B's and C's tests can run.
- **Tests:** STFT/iSTFT round trip; chunk boundaries leave no seams (a split of a long file equals the split of its
  parts within 1e-4); SDR on the test mix; time per minute of audio, recorded.

## Integration

1. Merge in this order: D, E, A, B, then C.
2. Fetch the models; build the test mix.
3. Run the two-process tests: routing and learn through the real host, clock and MTC out and in through the virtual
   ports, Link with the probe.
4. Run the Playwright golden path for the exit criterion.
5. Fix the seams.

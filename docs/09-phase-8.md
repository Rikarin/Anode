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

### Contract notes

Where the committed code differs from, or adds to, the shapes planned for this commit, and the shapes chosen where
the plan left latitude:
- **Assignments** (`Control.cs`): `ControlInput(port?, kind, channel?, number)` with `ControlInputKind`
  (`controlChange | controlChange14 | nrpn | note | pitchBend | programChange`; `number` is the controller, the
  14-bit pair's MSB 0…31 (LSB +32), the NRPN 0…16383, the key, or 0); `port` null is any port that is not a
  surface's, `channel` null any (else 1…16). `ControlTarget(kind, trackId?, address?, switch?, transport?,
  keyCommandId?, zoneId?, modeId?)` with `ControlTargetKind` (`parameter | smartControl | trackSwitch | transport |
  keyCommand | zoneMode`): typed fields instead of the planned single `action` string. `trackId` null is the selected
  track; `address` is the automation address (parameter) or the Smart Control id, `knob1`… (smartControl);
  `TrackSwitchAction` (`mute | solo | recordArm | selectNext | selectPrevious`); `ControlTransportAction` (`play |
  stop | record | cycle | rewind | forward | goToStart`; not `TransportAction`, which `Anode.Ipc` has). The mode enum
  is **`ControlValueMode`** (`absolute | relativeTwosComplement | relativeSignedBit | relativeBinaryOffset | toggle |
  momentary`), since `ControlMode` is the record of a zone's mode. `ControlAssignment(id, name?, input, target, mode,
  min, max, pickup)`: `min` and `max` are normalised 0…1 over the target's range (`min > max` inverts); a relative step
  is 1/128 of the range. `ControlMode(id, name, assignments)`, `ControlZone(id, name, modes, activeModeId)`,
  `ControllerAssignments(zones)` (`ControllerAssignments.Empty` for a new user); ids unique across all of them; an
  input matches at most one assignment among the active modes, the first in order.
- **Learn:** `ControlMessage(port, status, data1, data2, input)` (the raw message and the input it reads as);
  `LearnState(on, target?, zoneId?, modeId?, last?)`; `LearnRequest(target?, zoneId?, modeId?)`. A message received
  while learning with a target makes the assignment (replacing one of the same input in that mode; with no zone, the
  first zone's active mode, made when there is none). `/ws` `controlLearn(message, assignmentId?)`.
- **Surfaces:** `ControlSurfaceKind` (`mackieControl | mackieControlXt | hui`), `ControlSurface(id, kind, inputPort,
  outputPort, order)` with no further settings (none was needed yet), `AddSurfaceRequest(kind, inputPort,
  outputPort)`, `ReorderSurfacesRequest(ids)`, `SurfaceStatus(id, connected, bankOffset)`; `/ws` `surfaces(surfaces)`
  carries every surface's status. Added `GET /control/surfaces/status` so the editor can show it before an event.
- **OSC:** `OscClient(host, port)`, `OscSettings(enabled, port, clients)` (`OscSettings.Default`: off, 8000, none;
  `clients` always get feedback, subscribers only for the session), and **`OscStatus(settings, listening, addresses,
  subscribed)`**, added for the editor's OSC settings: `GET /control/osc` answers it and `PUT` takes `OscSettings`.
- **Selection:** the server did not know the editor's selection, so `PUT /project/selection` takes
  `SelectionRequest(trackId?)` (not part of the project).
- **Virtual ports:** `VirtualMidiPorts` (`InputId` `anode:in`, `InputName` "Anode In", `OutputId` `anode:out`,
  `OutputName` "Anode Out"), mirrored as `VirtualMidiPorts` in `models.ts`. The port list is `GET /midi/devices`
  (there is no `/system/midi`); the host lists the virtual ports with the others, so only its summary changed.
- **Sync** (`Sync.cs`): `SyncSource` (`internal | midiClock | mtc | link`), `MtcFrameRate` (`fps24 | fps25 |
  fps2997Drop | fps30`), `MidiSyncDestination(port, clock, mtc, delayMs)` (−50…50 ms), `SyncSettings(source,
  destinations, frameRate, startOffsetSeconds, linkStartStop, clockInPort?, mtcInPort?)` and `SyncState(source,
  locked, externalTempo?, linkPeers)`. There is **no `linkEnabled`**: Link is on exactly when `source` is `link` (one
  source at a time), so the two cannot disagree. The offset is **seconds** of timecode at the project start (0…86399),
  independent of the frame rate. `SyncSettings.Default` (a new project's): internal, no destinations, 25 fps, 0, Link
  start/stop off, any input. `ProjectSettings.sync` is required.
- **`/project/sync`** is `GET` only and answers the live `SyncState`; the settings change through the
  **`setSyncSettings`** command (`SetSyncSettingsCommand(source?, destinations?, frameRate?, startOffsetSeconds?,
  linkStartStop?, clockInPort?, mtcInPort?)`; left out stays, `destinations` replaces the list, an empty port string
  sets "any"), so they are undoable and saved with the project like any setting. `/ws` `sync(state)`, at most 10 a
  second.
- **Stem Splitter** (`Jobs.cs`): `JobKind.stemSplit`; `StemKind` (`vocals | drums | bass | other | guitar | piano`);
  `StemSplitRequest(stems?, keepSource = false)`: a list (left out: the four), and the six-stem model runs when guitar
  or piano is in it; the region is in the path, `POST /project/regions/{regionId}/split-stems` answering
  `Accepted<Job>`. The result command is **`addStems`**: `AddStemsCommand(regionId, stems: StemFile(kind, fileId)[],
  keepSource)`, applied by the job after it adds the files (each covers the region's span: a region of the whole file
  at the source's position), like `setFileVersion`; clients do not send it.
- **REST** (`ControlApi.cs`, `Pending.Phase8`, a new `Pending.cs` since B had removed Phase 7's):
  `GetControllerAssignments`, `SetControllerAssignments` (`GET`/`PUT /control/assignments`), `GetLearn`,
  `StartLearn`, `StopLearn` (`GET /control/learn`, `POST /control/learn/start`, `POST /control/learn/stop`),
  `ListSurfaces`, `ListSurfaceStatus`,
  `AddSurface`, `RemoveSurface`, `ReorderSurfaces` (`GET`/`POST /control/surfaces`, `GET /control/surfaces/status`,
  `DELETE /control/surfaces/{id}`, `PUT /control/surfaces/order`), `GetOsc`, `SetOsc` (`GET`/`PUT /control/osc`),
  `SetSelection`, `GetSync`, `SplitStems`. The editor's `AnodeApi` has a method for each (`controllerAssignments`,
  `setControllerAssignments`, `learn`, `startLearn`, `stopLearn`, `surfaces`, `surfaceStatus`, `addSurface`,
  `removeSurface`, `reorderSurfaces`, `osc`, `setOsc`, `setSelection`, `syncState`, `splitStems`), through the
  generated client in `HttpAnodeApi`; the fake Server answers them 422. A nullable object property
  (`LearnRequest.target`) is required but nullable in the generated client, as `Track.drumMachine` is: send
  `target: null`.
- **IPC:** `SetControlRoutingMessage(controlPorts, matchers: EngineControlMatcher[], learn)`, server → host, with
  `EngineControlMatcher(port?, status, channel, data1, family)` (status nibble 0x80…0xE0, channel 0…15 or −1,
  data1 0…127 or −1) and `EngineMatcherFamily` (`single | controlChange14 | nrpn`: a 0x90 matcher also takes its key's
  note offs; a 14-bit matcher takes controllers n and n + 32; an NRPN matcher takes CC 99, 98, 6, 38, 96 and 97 on its
  channel whatever the parameter, and the server's engine matches the parameter). `ControlMidiMessage(events:
  EngineControlEvent(port, timeNs, data: byte[])[])`, host → server, batched (at most every millisecond): bytes rather
  than three ints so a surface's SysEx can pass. `SendMidiMessage(port, data: byte[])`, server → host (one or more
  whole messages, SysEx included; sent now, not scheduled). `SetSyncMessage(source, destinations:
  EngineSyncDestination(port, clock, mtc, delayMs)[], frameRate, startOffsetSeconds, linkStartStop, clockInPort?,
  mtcInPort?)` with `EngineSyncSource` and `EngineMtcRate` (`Anode.Ipc` does not reference the contracts), and
  `SyncStateMessage(source, locked, externalTempo?, linkPeers)`, host → server, on change, at most 10 a second. The
  Link quantum is not sent: the host takes the bar from the signature at the playhead.
- **Native** (declarations only, not built): `cth_midi_virtual_open(name, uid, is_input)` and
  `cth_midi_virtual_close(uid)` in `cth_audio.h`, one port per call, `CTH_MIDI_VIRTUAL_UNSUPPORTED` (−2) on WinMM; a
  virtual input's messages enter the `cth_midi_read` ring like any source's. `cth_midi_event` holds three bytes and
  the ring drops SysEx, and `cth_midi_send` says "short message": workstream A widens both for SysEx on control
  ports and surface feedback (Mackie Control works without its SysEx handshake, but its scribble strips are SysEx).
  `engine/native/an_link/include/an_link.h`: opaque `an_link` and `an_link_session_state`; `an_link_create(bpm)`,
  `destroy`, `enable`/`is_enabled`, `enable_start_stop_sync`/`is_start_stop_sync_enabled`, `num_peers`,
  `clock_micros`, `create_session_state`/`destroy_session_state`, `capture_audio_session_state`/
  `commit_audio_session_state`, `capture_app_session_state`/`commit_app_session_state`, `tempo`, `set_tempo(bpm,
  time)`, `beat_at_time`, `phase_at_time`, `time_at_beat`, `request_beat_at_time`, `is_playing`, `set_is_playing`, and
  (added) `set_is_playing_and_request_beat_at_time` and `version`. No callbacks: the host polls peers and tempo.
- **Stubs that throw:**
  - `Anode.Control` (`Pending.WorkstreamD()`; references only the contracts):
    `Assignments.AssignmentEngine(assignments)` with `Assignments`, `Matchers` (`ControlMatcher(port?, status, channel,
    data1, family)`, `MatcherFamily`), `Receive(port, message, ITargetValues)` → `TargetChange(assignmentId, target,
    value)[]` (values normalised 0…1), `Learn(port, message)` → `ControlInput?`, `Reset()`;
    `ITargetValues.Value(target)` is the server's side (current normalised values, for pickup, relative and toggle).
    `Surfaces.IControlSurface` (`Kind`, `Strips`, `BankOffset`, `Connected`, `Decode(message, timeNs)` →
    `SurfaceAction[]`, `Encode(view)`, `EncodeMeters(view)`, `Poll(timeNs)`, `Invalidate()`), `MackieControl(extender)`,
    `Hui()`, `ControlSurfaces.Create(kind)`; `SurfaceAction(kind, strip, trackId?, address?, value, automation)` with
    `SurfaceActionKind` (`SetParameter`, `StepParameter`, `Touch`, `Mute`, `Solo`, `RecordArm`, `Select`, `Play`,
    `Stop`, `Record`, `Rewind`, `Forward`, `Cycle`, `Jog`, `CursorUp`, `CursorDown`, `CursorLeft`, `CursorRight`,
    `Zoom`, `Banked`, `SetAutomationMode`); the server's view of the project is `ProjectView(tracks, transport, cycle,
    tempo, timecode, beats, markers, smartControls)` with `TrackView(id, name, fader, volumeDb, pan, mute, solo,
    recordArm, selected, automation, meter, sends, inserts)`, `InsertView(id, name, parameters)`,
    `ParameterView(address, name, value, display)`, `Timecode`, `BeatPosition`, `MarkerView`. `Osc`: `OscPacket`,
    `OscMessage(address, arguments)`, `OscBundle(timeTag, elements)`, `OscArgument` (`OscInt`, `OscFloat`, `OscString`,
    `OscBlob`, `OscBool`), `OscCodec.Decode`/`Encode`, `OscAddressSpace.Root` (`/anode`), `Resolve(message, view)` →
    `OscRequest(kind, target?, value, text?, number)` with `OscRequestKind` (`Set`, `SetDb`, `SetName`, `GoToMarker`,
    `Subscribe`, `Unsubscribe`), and `Feedback(view)`.
  - `Anode.Stems` (`Pending.WorkstreamE()`; references the contracts for `StemKind`, no ONNX Runtime yet):
    `IStemSeparator` (`Model`, `Stems`, `SampleRate`, `Separate(channels, sampleRate, stems, progress,
    cancellationToken)` → per-stem channels of the input's length and rate), `StemModel` (`FourStems`, `SixStems`),
    `StemSeparators.ModelFor(stems)`, `FileName(model)`, `Open(model, path)`.
  - `Anode.Engine.Sync` (`Pending.WorkstreamA()`): `SyncMidiEvent(frame, status, data1, data2, length)`;
    `MidiClockEncoder.Process(playing, startTick, endTick, frames, output)` and `Locate(tick, output)`;
    `MtcEncoder(rate)` with `Process(playing, startSeconds, endSeconds, frames, output)` and `FullFrame(seconds,
    output)`; `MidiClockDecoder.Receive(message, timeNs)` → `ClockInput(kind, songPosition, timeNs)` with
    `ClockInputKind`; `TempoEstimator` (`Tempo`, `Locked`, `Tick(timeNs)`, `Check(nowNs)`, `Reset()`);
    `MtcDecoder(rate)` with `Running`, `Receive(message, timeNs)` → `MtcPosition?(seconds, timeNs, fullFrame)` and
    `Check(nowNs)`.
- **Projects:** `Anode.Control` and `Anode.Stems` are referenced by `Anode.Server` (already, so B need not touch the
  project file); `Anode.Control.Tests` and `Anode.Stems.Tests` hold one placeholder test each.
- **Interim:** schema-7 packages read through `Phase8Fields.Fill` (`settings.sync` at `SyncSettings.Default`), which
  workstream B replaces with schema 8 and its migration. The model refuses `setSyncSettings` and `addStems` (422,
  `Edits.Phase8.cs`); the identity property skips `RandomProjects.PendingPhase8`, and random projects are built
  without those commands (`RandomProjects.Buildable`) so they stay Phase 7's; the editor's fake Server starts projects
  with the default sync settings and answers the new commands and endpoints 422 (`fake-phase8.spec.ts`).

### Integration notes

Where integration settled the seams between the workstreams (contract changes are marked **contract**):
- **Surfaces in the server:** the control service drives D's `SurfaceGroup`s, not its own bank chain: an XT joins the
  Mackie Control (or XT) before it in `order`; anything else, a HUI always, is a group of its own. The group owns the
  bank; the service polls a group right after its input, so handshake and ping replies go out at once. Learn makes its
  assignment with `AssignmentEngine.Assign` (D's default modes: toggle for switches and `mute`, absolute otherwise); a
  control moved while learning the same target re-learns it, keeping the assignment's id.
- **Buttons:** surfaces report buttons on press only (Touch, Rewind, Forward and Zoom carry 1/0); assignment functions
  give 1 on press and 0 on release, and the server acts on 1 (Rewind and Forward stop on 0). Strip switches set the
  state the view does not have, so nothing toggles twice.
- **Matchers:** pitch bend matches `data1 = −1` (its first byte is the value's LSB). A program change matches its
  program (`data1` = the number), as D's engine claims only that program; the host compares `data1` the same way, so an
  unassigned program still plays.
- **contract** `/ws` gains `selection(trackId?)` (hardware or OSC changed the server's selection: a surface's select
  or cursor keys, select next or previous; not sent for the editor's own `PUT /project/selection`) and
  `keyCommand(id)` (a key command an assignment or OSC asked for that the server does not run itself); `models.ts`
  mirrors both; the editor selects the track without reporting it back, and runs the key command. No REST change.
- **Stem models:** `StemModels` goes through E's `ModelStore` in `<DataDirectory>/Models/`: a verified model opens; a
  missing one is installed by checksum from `Anode:ModelSource` (default the `ANODE_MODELS` environment variable, the
  name E's tests read; the e2e global setup points it at `models/`), else downloaded (refused, 422, while
  unpublished).
- **Stem naming:** tracks are named `Edits.StemTrackName` ("Mix – Vocals"), files "Mix Vocals.wav" (the job); the fake
  Server does the same.
- **External tempo:** following Link, the server writes the session's tempo into the project (`setTempo`), so regions,
  the grid and a save follow it; a burst of changes (no more than 2 s apart) is one undo step. It cannot loop: the
  host pushes the model's tempo to Link only when the model's changes and differs from Link's. MIDI Clock's tempo
  stays out of the project (the rule above): the transport follows the smoothed estimate, regions keep their sample
  positions, and the tempo map is the project's own when the clock stops; writing a jittering estimate would flood the
  undo history and the saved project.
- **Virtual ports in tests:** `anode-host --virtual-midi` (the e2e's `ANODE_VIRTUAL_MIDI=1`) publishes Anode's own
  CoreMIDI ports on macOS and hides every other endpoint (`OwnPortsMidiBackend`); elsewhere it keeps the fake pair.
- **Sync input on a control port:** system messages (and MTC full frames) go to the sync follower first when the port
  is its clock or MTC input, so a surface and the clock can share a port; the rest of a control port's input is the
  surface's.
- **The golden path** uses a 44.1 kHz project (the test mix's rate; a project takes files at its own rate), judges
  clock jitter by `p99DeviationMs` and `outliers` rather than the maximum (the JIT host on the null device stalls now
  and then), Link phase by `linkClockError.maxAbsMs`, and MTC out by the first set of quarter frames against the
  playhead and the drift.

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

### E — Stem Splitter (`Anode.Stems`, `scripts/`; ADR-0010)

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

## Status (2026-10-31)

**Exit criterion met on macOS.** The Playwright golden path `phase8/control-sync-golden-path.spec.ts` passes on the
real desktop app with the null audio device, `anode-midiprobe` on Anode's virtual MIDI ports and in the Link session,
and the locally exported HTDemucs models. All ten steps pass. All nine e2e specs pass, twice in a row.

| Criterion | Measured | Limit |
|---|---|---|
| MIDI Clock out at 120 bpm: median tick | 20.839 ms | 20.833 ms |
| MIDI Clock out: jitter (99th percentile) / outliers | 0.023 ms / 0 | 0.5 ms / 2 |
| MTC out against the playhead: start / drift | 0.000 s / 0.000 frames | 1 frame |
| MIDI Clock in at 100 bpm | 100.000 bpm | ± 0.5 bpm |
| MTC in: chase | 0.011 s, 0.008 s | 1 frame plus the read window |
| Link: beat phase against the probe's | 0.022 ms, 0.023 ms | 1 ms |
| Stem Splitter (four stems): SDR vocals, drums, bass, other | 12.07, 14.34, 11.91, 4.74 dB | 3, 6, 6, 3 dB |

Steps 1–5 and 10 are pass/fail: Learn, a knob moving only its volume, Latch automation from the knob, a Smart Control
from CC 22, Mackie Control (faders, scribble strips, touch, transport, bank right, meters, timecode), HUI (ping and
fader), OSC (requests and subscribed feedback), and save and reopen.

| Check | Result |
|---|---|
| Engine tests (`dotnet test --solution engine/Anode.slnx --max-parallel-test-modules 2`) | 2162 pass, 18 skipped (opt-in); one load-sensitive MIDI timing test failed once and passed 3 of 3 on rerun |
| Control protocols ([docs/control](control/README.md)) | assignments, Mackie Control and XT, HUI, OSC 1.0; byte vectors shared by the protocol and server tests |
| Sync (engine tests) | clock spacing within 0.23 ms under ±2 ms wake-up jitter; MTC within ¼ frame at every rate; clock in within 0.5 bpm 2 beats after a step; two Link peers within 0.1 ms; no allocations in the callback |
| Stem Splitter ([ADR-0010](adr/0010-stem-splitter.md)) | HTDemucs on ONNX Runtime (CPU), about 20 s per minute of audio; matches demucs to 6.5·10⁻⁵ |
| Editor (`nx run-many -t typecheck lint test build`) | 5 projects pass; Storybook + axe pass |
| e2e (`nx e2e desktop`) | control and sync, MIDI editing, loops and performance, advanced audio, mixing, arranging, MIDI and Phase 1 golden paths, smoke; twice in a row |
| Skala, Prettier, generated client | clean |

**Contract changes and decisions at integration** (detailed in "Integration notes" above):
- `/ws` events `selection(trackId?)` and `keyCommand(id)`: selection made from hardware or OSC reaches the editor, and
  key commands the server cannot run are run by the editor.
- The control service drives D's `SurfaceGroup` (banking across a Mackie Control and its extenders) and
  `AssignmentEngine.Assign` (Learn).
- Following Link, the session's tempo is written into the project (a burst of changes is one undo step); MIDI Clock in
  drives only the transport.
- MIDI Clock or MTC on a port a surface also uses still reaches the follower when that port is the sync input.
- `anode-host --virtual-midi` publishes Anode's own CoreMIDI ports and hides real devices (e2e).
- Stem models are found through `ModelStore`, installed from `ANODE_MODELS` (`Anode:ModelSource`) when missing.
- Stem tracks are named after the source and the stem ("mix – Vocals").

**Open:**
- **The HTDemucs weights are research-only** (ADR-0010): the models are not published, and outside development the
  Stem Splitter needs a model the user provides. A licence grant or a replacement model is needed before release.
- The MCU master fader is ignored (the surface view has no master strip).
- `SurfaceTests.Faders_and_LEDs_go_out_at_most_30_times_a_second…` failed once in four runs alongside another test
  class.
- Windows: the WinMM SysEx changes were reviewed, not compiled; WinMM has no virtual ports.
- Linux and Windows.

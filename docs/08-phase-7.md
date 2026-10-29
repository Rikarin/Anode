# 08 — Phase 7: advanced MIDI editing

From [the plan, §12](01-implementation-plan.md#phase-7-advanced-midi-editing-6-weeks) and §11 (Tracks, Recording,
Arranging and Editors rows marked 7). Scripter follows [ADR-0009](adr/0009-scripter.md).

**Exit criterion.** MIDI and audio can be edited at the event and sample level, and grooves, articulations and scripts
shape a performance. The Playwright golden path covers it on the null device:
1. **Event List.** On a MIDI region:
   - filter to notes;
   - edit one note's position, length and velocity numerically;
   - insert a program change and a CC 1 event;
   - delete an event.
   The region plays the edits.
2. **Step Editor.** Draw a velocity ramp and a CC 74 lane over a bar with the line tool. The events land as drawn.
3. **MIDI Transform.**
   - Apply the Humanize preset (seeded, so deterministic).
   - Apply a custom Select and Operate: notes above C4 get velocity 100 and a fixed 1/16 length.
   The events are exactly as expected.
4. **Grooves.**
   - Make a groove template from a swung audio drum loop (its transients) and another from a MIDI region.
   - Quantise a straight MIDI region to the audio groove: its notes land on the groove within 2 ms.
   - Make the drum track the groove track; another track set to follow it quantises to it.
5. **Articulations.**
   - Create an articulation set whose outputs are keyswitches, for a Sampler program with two keyswitched articulations
     (a test SFZ: one sine group, one square group).
   - Give notes articulations in the Piano Roll.
   A bounce plays each note with its articulation (checked by its harmonics).
6. **Step input.**
   - With the Step Input Keyboard, enter notes, a chord and a rest.
   - Then step-enter from the virtual MIDI input.
   The region holds exactly those notes.
7. **Audio File Editor.** On an audio region's file:
   - normalise a selection to −1 dBFS;
   - fade in;
   - reverse a part;
   - silence a part;
   - undo the silence;
   - run loop search on a periodic tone; it finds a loop within one period of the truth.
   The file's samples reflect each operation.
8. **Scripter.** Insert Scripter on a synth track:
   - Load a factory preset.
   - Write a script that sends each note plus an octave above it 100 ms later, with a `PluginParameters` slider for the
     interval. A bounce has the delayed octave notes, within 1 ms. `Trace` output appears in the console.
   - A runaway script (`while (true) {}`) is bypassed with a reported problem, and playback continues.
9. Save, close and reopen: everything is intact.

The contracts below are committed code. Each workstream builds against them and changes them only with a note in its
report. A REST change means regenerating `engine/src/Anode.Server/openapi.json`
(`ANODE_UPDATE_OPENAPI=1 dotnet test`) and the editor client (`nx run api-client:generate`).

## Scope

**In:**
- **Event List:**
  - Every MIDI event of a region or track: notes, CC, pitch bend, channel and poly pressure, program change, and
    articulation.
  - Filters by type; numeric editing of position (bar.beat.division.tick), length, channel, data and velocity.
  - Insert and delete; follow the playhead.
- **Step Editor:**
  - Lanes of velocity, any CC, pitch bend, pressure, program, and a note's own lane.
  - Lane sets: saved per project, with a default set.
  - Pencil and line tools, with the lane's resolution.
- **MIDI Transform:**
  - The Transform window's rule engine: conditions on position, length, channel, pitch, velocity and status.
  - Operations: thru, fix, add, subtract, minimum, maximum, flip, multiply, divide, scale, range, random, ± random,
    quantise, exponent, crescendo, relative crescendo, and a 128-step map.
  - Modes: select only, operate only, select and operate.
  - Presets: humanize, quantize, crescendo, fixed velocity, transpose, double speed, half speed, reverse position,
    reverse pitch, length limits, and others.
  - Custom sets saved in the user library.
- **Groove templates:**
  - Made from a MIDI region (its notes) or an audio region (its transients). Stored in the user library and in the
    project.
  - Quantise to a groove (the existing quantise commands take a groove), with strength.
- **Groove track:** one track per project; tracks with "match groove track" quantise to it.
- **Articulation sets:**
  - Articulations, each with an id, a name and a symbol.
  - Input switches: keyswitch notes or CC values recorded as articulations.
  - Outputs: keyswitch note, CC, program or channel, sent before each note.
  - Per-note `articulationId`, assigned in the Piano Roll and Event List.
  - Sets are saved in the user library.
  - The Sampler plays SFZ keyswitches (`sw_lokey`, `sw_hikey`, `sw_last`, `sw_default`).
- **Step input:**
  - The Step Input Keyboard window: note values, dotted and triplet, velocity, rest, tie, chord mode.
  - Step-entering from a MIDI keyboard (held keys make a chord).
  - Entry is at the edit position on the selected region, or a new one.
- **Audio File Editor** (destructive, with undo):
  - Edits a file of the project at the sample level: selection, zoom to samples, snap to zero crossings.
  - Operations: normalise (peak), gain, fade in and out with curves, reverse, invert, silence, remove DC offset, trim,
    sample-rate conversion.
  - Loop search; groove extraction to a template.
  - Each operation writes a new version of the file; undo restores it.
  - Regions that use the file follow it.
- **Scripter** per ADR-0009:
  - A compatible subset of Logic's API:
    - callbacks: `HandleMIDI`, `ProcessMIDI`, `NeedsTimingInfo`, `GetTimingInfo`, `ParameterChanged`, `Reset`,
      `Idle`;
    - parameters: `PluginParameters` (lin, log, menu, checkbox, momentary, text), `GetParameter`, `SetParameter`,
      `UpdatePluginParameters`;
    - the event classes `Event`, `NoteOn`, `NoteOff`, `PolyPressure`, `ControlChange`, `ProgramChange`,
      `ChannelPressure`, `PitchBend`, `TargetEvent`, with `send`, `sendAfterMilliseconds`, `sendAtBeat`,
      `sendAfterBeats` and `trace`;
    - `MIDI` helpers; `Trace`.
  - The Script Editor window, with a code editor, a console, Run, and factory presets.
  - Parameters from the script become the slot's parameters: automatable and mappable to Smart Controls.

**Out:**
- Scripter `Fader` and `Environment` objects.
- Scripter on audio.
- Event List for audio regions and markers. Logic's Event List shows them; regions and markers have their own lists
  here.
- MIDI Transform on audio.
- The Score's step input (Phase 9).
- Multi-take groove extraction.

## Contracts

| What | Where |
|---|---|
| MIDI events for the Event List: control events cover CC, pitch bend, pressure, program, poly pressure; a note gains `articulationId` | `engine/src/Anode.Contracts/Midi.cs` |
| Transform: conditions, operations, modes, sets, presets | `engine/src/Anode.Contracts/Transform.cs` |
| Grooves: templates (positions and velocities over a length), the groove track, a track's `matchGroove`; quantise commands take `grooveId` | `engine/src/Anode.Contracts/Groove.cs`; `Project.Grooves`, `ProjectSettings.GrooveTrackId`, `Track.MatchGroove` |
| Articulation sets: articulations, input switches, outputs; a track's `articulationSetId` | `engine/src/Anode.Contracts/Articulations.cs`; `Project.ArticulationSets`, `Track.ArticulationSetId` |
| Step Editor lane sets | `Project.LaneSets` |
| Step input: the state (on, position, note value, velocity, chord), and its commands | `engine/src/Anode.Contracts/StepInput.cs` |
| Audio File Editor: operations, file versions | `engine/src/Anode.Contracts/AudioEdit.cs`; `AudioFile.Version` |
| New commands (about 20) | `engine/src/Anode.Contracts/Commands.cs` |
| `/ws` `scriptLog` (slot, lines) and `parameters` (a slot's parameter list changed) events | `Realtime.cs`, mirrored in `models.ts` |
| REST (stubbed): `/library/grooves`, `/library/transform-sets`, `/library/articulation-sets` (list, save, delete); `/project/files/{fileId}/edit` (an operation, as a job), `/project/files/{fileId}/loop-search`, `/project/files/{fileId}/samples` (a range of samples for the editor); `/project/step-input` (state) | `engine/src/Anode.Server/Api/MidiEditApi.cs` → `openapi.json` → `@anode/api-client` |
| IPC: `slotParameters` (host → server: a slot's parameter list changed), `scriptLog` (host → server), `setScript` (server → host: a Scripter slot's source), `liveMidi` forwarding while step input is on, `previewRange` (preview part of a file) | `engine/src/Anode.Ipc/IpcMessage.cs` |
| `Insert.Parameters` for slots whose plugin defines its parameters at run time (Scripter), and `Insert.Source` for a script | `engine/src/Anode.Contracts/Mixer.cs` |
| Native: `an_script` (QuickJS-ng behind a C ABI: create with an arena, compile, call, interrupt budget, trace ring) | `engine/native/an_script/include/an_script.h` |
| DSP: Audio File Editor operations, loop search, groove extraction; MIDI Transform engine; groove quantise; SFZ keyswitches | `engine/src/Anode.Dsp/Editing/`, `engine/src/Anode.Model/Transform/`, `engine/src/Anode.Model/Groove/` (stubs that throw) |

### Rules the contracts imply

- **Events:**
  - A note's `articulationId` (0: none) plays its articulation's outputs just before the note: at the note's tick, sorted
    before it, on the note's channel.
  - A channel output changes the note's channel.
- **Transform:**
  - Conditions are ANDed.
  - Operations apply to each selected event in the order position, length, channel, pitch, velocity.
  - Random operations draw from a generator seeded by the set's seed and the event's id, so a transform is
    deterministic and its inverse is exact.
  - "Select only" changes the editor's selection, not the project.
  - The result is one undo step.
- **Grooves:**
  - A template is a list of (position, velocity scale) within a length (1–4 bars), extracted from the strongest onset per
    grid step.
  - Quantising a note moves it to the template position of its grid step, scaled by strength.
  - Velocity follows the template only when the quantise asks for it.
  - A track that matches the groove track quantises with the groove extracted from the groove track's first region, or
    its notes, live at quantise time.
- **Articulations:**
  - While recording, a track's input switches turn into the following notes' `articulationId`; the switches themselves
    are not recorded.
  - The Sampler's SFZ keyswitches select groups; an articulation set with keyswitch outputs drives them.
- **Step input:**
  - The server holds the step position.
  - Each step adds the notes held (a chord when several keys go down before any goes up) at the position, with the
    current note value and velocity, then advances; a rest advances; a tie lengthens the last step.
  - Steps from the keyboard window and from MIDI input (forwarded by the host while step input is on) are the same
    command.
- **Audio File Editor:**
  - An operation reads the file's current version and writes `<name>.v<N>.wav` beside it.
  - It sets `AudioFile.Version` and `Path` in one undoable step, so undo points back at the previous version, whose file
    is kept until the project is saved and the undo history no longer reaches it.
  - Peaks are recomputed.
  - Regions keep their frames: length-changing operations (trim, sample-rate conversion) shift and clip regions to keep
    what they played, or are refused when a region would vanish.
- **Scripter:**
  - The slot's `source` is the script.
  - The host compiles it on the control thread and answers with `slotParameters`, the script's `PluginParameters`. The
    server stores them in `Insert.Parameters`, so they automate, map to Smart Controls and show in the editor.
  - Errors and the budget follow ADR-0009 and are reported as `pluginProblem` with the line.
  - Timing (`GetTimingInfo`, `sendAtBeat`) comes from Phase 6's `TransportContext`.

### Contract notes

Where the committed code differs from, or adds to, the shapes planned for this commit, and the shapes chosen where
the plan left latitude:
- **Events** (`Midi.cs`): `ControlKind` gains `polyPressure` (`number` the key, `value` 0…127; appended, so the
  others keep their values), and the segment's `PlaybackEventKind` gains `PolyPressure = 9` (Data1 key, Data2
  pressure; the host already plays it). `Note.articulationId` (`int`, 0 none) is required on the wire;
  `NewNote.articulationId` (`int?`, left out: 0) and `NoteChange.articulationId` (`int?`, 0 clears) are optional. The
  Event List edits through `addNotes`, `editNotes`, `deleteNotes`, `addControls`, `deleteControls` and the new
  `setControls(regionId, changes: ControlEventChange(id, tick?, channel?, number?, value?))`; an event's kind does not
  change (delete and add instead). Articulation is a note's field, not an event of its own.
- **Transform** (`Transform.cs`): `TransformField` (`position | length | channel | pitch | velocity | status |
  subposition`; for controller events `pitch` is the first data byte and `velocity` the value, as in Logic; `status`
  is the status nibble as a number, 144 note … 224 pitch bend; `status` and `subposition` are conditions only);
  `TransformConditionOp` (`all | equal | unequal | lessEqual | greaterEqual | inside | outside`);
  `TransformCondition(field, op, a, b)`; `TransformOperationKind` (`thru | fix | add | subtract | min | max | flip |
  multiply | divide | scale | range | random | plusMinusRandom | quantize | exponent | crescendo | relativeCrescendo
  | useMap`, each defined on the enum); `TransformOperation(field, op, a, b)`; `TransformMode` (`selectOnly |
  operateOnly | selectAndOperate`); `TransformSet(id, name, conditions, operations, map?, mode, seed)`. The command is
  `transform(regionIds, set, noteIds?, controlIds?)`; it refuses `selectOnly`, which the editor runs itself (it has the
  TypeScript port for the vectors). Factory presets are sets with ids `factory.…` (`TransformPresetIds`:
  `humanize`, `quantize`, `crescendo`, `fixedVelocity`, `transpose`, `doubleSpeed`, `halfSpeed`, `reversePosition`,
  `reversePitch`, `lengthLimits`), listed first by `GET /library/transform-sets` and never saved over or deleted.
- **Grooves** (`Groove.cs`): `GroovePoint(tick, velocityScale)`, `GrooveTemplate(id, name, lengthTicks, stepTicks,
  points)` with one point per step; the template repeats from the project start. `Project.grooves`,
  `ProjectSettings.grooveTrackId`, `Track.matchGroove`. `quantizeNotes` takes `grooveId?` and `grooveVelocity?`;
  `quantizeAudio` takes `grooveId?` only (audio has no velocities). Added `RegionPlayback.grooveId` and
  `setMidiRegion.grooveId` (empty string: the grid), so the region inspector's non-destructive quantise can use a
  groove too. A quantise on a matching track without `grooveId` uses the groove track's live groove. Commands:
  `makeGroove(regionId, name?, lengthBars?, stepTicks?)` (MIDI or audio region), `setGroove(groove)` (add, or replace
  by id: how a library groove enters the project), `deleteGroove(grooveId)`, `setGrooveTrack(trackId?)` and
  `setMatchGroove(trackId, matchGroove)`, a dedicated command rather than a `setTrack` field.
- **Articulations** (`Articulations.cs`): `Articulation(id 1…254, name, symbol?)`; `ArticulationSwitch(articulationId,
  kind, number, value?)` with `ArticulationSwitchKind` `note | controlChange`; `ArticulationOutput(articulationId,
  kind, number, value)` with `ArticulationOutputKind` `note | controlChange | program | channel` (a program's and a
  channel's datum is `value`, their `number` 0; a keyswitch note is one tick long at velocity `value`);
  `ArticulationSet(id, name, articulations, switches, outputs)`. The enums say `controlChange`, not `cc`, as
  `ControlKind` does. `Project.articulationSets`, `Track.articulationSetId`. Commands `setArticulationSet(set)`,
  `deleteArticulationSet(setId)`, `assignArticulationSet(trackId, setId?)`.
- **Lane sets** (`StepEditor.cs`): `LaneKind` (`velocity | controlChange | pitchBend | channelPressure | polyPressure |
  program | note`), `Lane(kind, number?, name, resolutionTicks)`, `LaneSet(id, name, lanes)`; `Project.laneSets`,
  empty in a new project, where the editor shows the default set documented on `LaneSet`. Commands `setLaneSet(set)`
  and `deleteLaneSet(setId)`.
- **Step input** (`StepInput.cs`): `StepInputState(on, regionId?, trackId?, positionTick, noteValueTicks, dotted,
  triplet, velocity, chord, midiInput)` and `StepInputRequest` (all optional), on REST only: `GET` and `PUT
  /project/step-input`, and a rest as `POST /project/step-input/rest` (a rest changes no project, so it is not a
  command). Undoable steps are `stepNotes(pitches, velocity?, regionId?, trackId?, positionTick?, lengthTicks?,
  channel?)` and `stepTie(regionId?, noteIds?, lengthTicks?)`: clients send the pitches, and the server fills the
  rest from the state before the model applies them, so the model stays pure. Added the `/ws` event
  `stepInput(state)`, so the Piano Roll follows MIDI step input. There is no `setStepInput` command.
- **Audio File Editor** (`AudioEdit.cs`): `AudioEditKind` (`normalize | gain | fadeIn | fadeOut | reverse | invert |
  silence | removeDc | trim | convertRate`); `AudioEditOperation(kind, startFrame, endFrame, value?, curve?,
  targetRate?)`; `AudioFile.version` (`int`, 0 as imported). `POST /project/files/{fileId}/edit` answers an
  `audioEdit` job (new `JobKind`); when it finishes the server applies `setFileVersion(fileId, version, path, frames?,
  sampleRate?)` with any region shifts in one undo step: the job produces the command, which clients do not send.
  `POST …/loop-search` takes `LoopSearchRequest(startFrame, endFrame, count?, minFrames?)` and answers
  `LoopSearchResult(startFrame, endFrame, score)[]`, best first. `GET …/samples?start&frames&bucket` answers
  `SampleRange(startFrame, frames, channels, bucket, samples?, min?, max?)`: raw samples at bucket 1, else min and max
  per bucket. Added `POST …/preview` (`PreviewRangeRequest(startFrame, endFrame, loop?)`) for the range preview that
  the IPC's `previewRange` serves.
- **Scripter:** `Insert.parameters` already holds a slot's parameter values, so the run-time list is
  **`Insert.runtimeParameters`** (`ParameterInfo[]?`); `Insert.source` is the script. `setScript(trackId, slotId,
  source)`: `trackId` is required, as MIDI FX slots exist only on tracks.
- **Deltas and events:** `grooveUpserted`/`grooveRemoved`, `articulationSetUpserted`/`articulationSetRemoved`,
  `laneSetUpserted`/`laneSetRemoved`. `/ws` `scriptLog(trackId, slotId, lines, dropped)`, `parameters(trackId,
  slotId, parameters)` (carrying the list) and `stepInput(state)`.
- **REST** (`MidiEditApi.cs`, `Pending.Phase7`): `ListGrooves`, `SaveGroove`, `DeleteGroove`, `ListTransformSets`,
  `SaveTransformSet`, `DeleteTransformSet`, `ListArticulationSets`, `SaveArticulationSet`, `DeleteArticulationSet`
  (save bodies are the objects themselves), `EditFile`, `SearchLoops`, `GetFileSamples`, `PreviewFileRange`,
  `GetStepInput`, `SetStepInput`, `StepRest`.
- **IPC:** `SlotParametersMessage(trackId, slotId, parameters: EngineParameter[])` and `ScriptLogMessage(trackId,
  slotId, lines, dropped)`, host → server; `SetScriptMessage(trackId, slotId, source)`, server → host;
  `SetStepInputForwardMessage(on)`, server → host, and `StepInputMidiMessage(pitches, velocity)`, host → server (one
  step: the keys down since the previous step, sent when the first is released, with the highest velocity), rather
  than reusing `liveMidi`; `PreviewRangeMessage(path, startFrame, endFrame, loop, gain)`. `EngineInsert.source`
  carries a Scripter slot's script in models and strips, so a load compiles it without a separate `setScript`.
- **Native** (`engine/native/an_script/include/an_script.h`, declaration only, not in CMake): `an_script_create(arena
  bytes, random seed, emit capacity, trace capacity)`, `destroy`, `compile` (error and line through `last_error`),
  `call(name, number args, budget µs, result)`, `set_global`/`get_global` (numbers), `eval_json` (reads
  `PluginParameters` after a compile), `read_emitted` (records of 8 numbers the prelude's `__emit` writes: D defines
  them), `read_trace` (the `__trace` ring, with a dropped count), `collect`, `memory_used`, `arena_bytes`, `reset`,
  `version`; results `AN_SCRIPT_OK`, `ERROR`, `INTERRUPTED`, `OUT_OF_MEMORY`, `NO_FUNCTION`, `INVALID`.
- **Stubs that throw** (`Pending.WorkstreamE()`): `Anode.Dsp.Editing`: `AudioEdits` (`Normalize`, `Gain`, `FadeIn`,
  `FadeOut`, `Reverse`, `Invert`, `Silence`, `RemoveDc` in place; `Trim`, `ConvertRate` returning new channels),
  `ZeroCrossings.Nearest`/`Find`, `LoopSearch.Find` → `LoopCandidate[]`, `GrooveFromAudio.Onsets` → `GrooveOnset[]`.
  `Anode.Model.Transform`: `TransformEngine.Apply(notes, controls, set, signatures)` → `TransformResult(notes,
  controls, selectedNoteIds, selectedControlIds)`, positions in project ticks; `TransformEngine.Presets()`.
  `Anode.Model.Groove`: `Grooves.FromOnsets`, `FromNotes`, `Target`, `Quantize`, with `GrooveOnsetTick(tick,
  strength)` (the model does not reference `Anode.Dsp`, so the server turns `GrooveOnset` frames into ticks).
- **Commands** (16 new, plus the groove and articulation fields): `setControls`, `transform`, `makeGroove`,
  `setGroove`, `deleteGroove`, `setGrooveTrack`, `setMatchGroove`, `setArticulationSet`, `deleteArticulationSet`,
  `assignArticulationSet`, `setLaneSet`, `deleteLaneSet`, `stepNotes`, `stepTie`, `setFileVersion`, `setScript`.
  Fewer than about 20 because a rest and the step input state are REST, a groove track's matching is one command, and
  the library is REST.
- **Interim:** schema-6 packages read through `Phase7Fields.Fill` (files' `version` 0, tracks' `matchGroove` false,
  notes' `articulationId` 0), which workstream B replaces with schema 7 and its migration. `ProjectState` answers empty
  `grooves`, `articulationSets` and `laneSets`. The model refuses the new commands, and the groove and articulation
  fields of existing commands (422, `Edits.Phase7.cs`); the identity property skips `RandomProjects.PendingPhase7`;
  the editor's fake Server answers them 422 and leaves every new field at its default.

## Workstreams

Five, in parallel, each on its own branch; they meet only at the contracts above.

### A — Engine (`Anode.Ipc`, `Anode.Engine`, `Anode.EngineHost`, `Anode.Host`)

- **Run-time slot parameters:** a slot whose instance changes its parameter list pushes `slotParameters`. Parameters
  are addressed by id, and automation and `setInsertParameter` work on them.
- **Scripter in the chain:**
  - Workstream D's `anode.scripter` (`IMidiEffect`) takes `setScript` on the control thread (compile, then swap).
  - Its trace lines are pushed as `scriptLog`, at most 50 a second per slot; the rest are counted as dropped.
  - Budget overruns are pushed as `pluginProblem`.
- **Live MIDI forwarding** to the server while step input is on, with the held-key state.
- **Preview of a file range** (`previewRange`) for the Audio File Editor, with a loop option.
- **Tests:**
  - Parameter changes and automation by id.
  - `setScript` swaps without a gap.
  - Log rate limiting; forwarding; range preview.
  - No allocations with a running script (using D's arena).

### B — Server domain (`Anode.Model`, `Anode.Server`)

- **Model:**
  - Every new command with validation and inverses.
  - Remove the `PendingPhase7` placeholders.
  - Package schema 7 with a migration from 6.
- **Commands and features:**
  - Event List edits (control events of every kind; `articulationId`).
  - Transform as a command (through workstream E's engine).
  - Grooves: extract, store, quantise, groove track and matching.
  - Articulation sets and per-note articulations; input-switch handling while recording.
  - Lane sets.
  - Step input state and steps.
  - Audio File Editor operations as jobs with file versions (through E's operations); loop search.
  - Scripter source and `slotParameters` into `Insert.Parameters`; `scriptLog` relayed.
  - Library endpoints for grooves, transform sets and articulation sets.
- **Compiler:** articulation outputs before notes; the channel output.
- **`FakeHost`:** the new messages; forwards step-input MIDI from tests.
- **Tests:**
  - Properties for every command.
  - Transform commands and groove quantise through workstream E's engine (E writes the shared `TransformVectors.json`
    and `GrooveVectors.json`; until E merges, against your own small cases).
  - Articulation compilation.
  - Step input sequences; file versions and undo.
  - Migration; API tests.

### C — Editor (`editor/`; built with `editor/DESIGN.md`)

- **Event List:** a data table, with filters, inline editing in bar.beat.division.tick, insert and delete, follow, and
  articulation names.
- **Step Editor:** lane sets, lanes, pencil and line tools; side by side with the Piano Roll's time axis.
- **MIDI Transform window:** the preset menu, the conditions and operations grid, the 128-step map editor, the three
  modes, and save to the library.
- **Grooves:**
  - "Make Groove Template" from a region; grooves in the quantise menus (region inspector, Piano Roll).
  - The groove track toggle in the track header, and "match groove track".
- **Articulations:**
  - The Articulation Set editor (articulations, switches, outputs).
  - A track's set in the inspector; per-note articulation in the Piano Roll (a lane and the note inspector) and the
    Event List.
- **Step Input Keyboard window**, and MIDI step input (an on/off toggle, the position shown in the Piano Roll).
- **Audio File Editor:**
  - The waveform at the sample level (from `/samples`), selection, zero-crossing snap.
  - The operations menu, with job progress; loop search markers.
  - Groove extraction; range preview.
- **Script Editor window:**
  - A code editor: CodeMirror 6, MIT; add it to ADR-0008's table.
  - A console fed by `scriptLog`, Run, factory presets, and the script's parameters as controls.
- **Fake server:** every new command, endpoint and event; remove the placeholders.
- **Tests:**
  - Transform vectors parity.
  - The position parser and formatter.
  - Line-tool interpolation; step-input sequences.
  - Stories with axe for the new audio-ui parts: event table row, lane, transform grid, step keyboard.
  - The golden-path spec, as `test.fixme`.

### D — Scripter (`engine/native/an_script`, `Anode.Dsp/MidiEffects/Scripter`)

- **`an_script`:**
  - QuickJS-ng, pinned and fetched by `build.sh`.
  - An arena allocator per instance; the interrupt budget; no std or os modules; a trace ring; a C ABI.
- **`anode.scripter`** as an `IMidiEffect`:
  - The API subset above.
  - Events scheduled with `sendAfterMilliseconds` and `sendAtBeat` across blocks.
  - Parameters from `PluginParameters`.
  - State: the source, plus the parameter values.
- **Factory presets:** about 10, written for Anode, in the spirit of Logic's tutorial scripts: transposer, delay notes,
  chord, random velocity, note repeater, guitar strummer, drum humaniser, CC smoother, arpeggio, Trace demo.
- **Tests:**
  - API behaviour per callback and class.
  - Timing to the sample (`sendAfterMilliseconds` at several tempi; `sendAtBeat` across a ramp).
  - The budget interrupt, out-of-arena, a syntax error with its line, recovery after edit.
  - No allocations outside the arena on the audio thread.
  - CPU per event.
- **Doc:** `docs/scripter/README.md` documents the API subset, with the differences from Logic.

### E — Editing algorithms (`Anode.Dsp/Editing`, `Anode.Model/Transform`, `Anode.Model/Groove`, the Sampler)

- **Audio File Editor operations:**
  - normalise, gain, fades with curves, reverse, invert, silence, DC removal, trim;
  - sample-rate conversion (reuse the bounce resampler);
  - zero-crossing search; loop search (correlation of candidate loop ends, scored for continuity);
  - groove extraction from audio (D's transients from Phase 5).
- **The MIDI Transform engine:** conditions, every operation, the three modes, the presets as data, seeded randomness.
- **Groove:** template extraction from notes, and quantise to a template.
- **The Sampler:** SFZ keyswitch support (`sw_lokey`, `sw_hikey`, `sw_last`, `sw_default`, `sw_label`) and articulation
  switching by keyswitch.
- **Tests:**
  - Each operation against hand-worked samples.
  - Loop search on periodic tones and noise.
  - Transform presets and groove quantise: write the shared vectors
    `engine/tests/Anode.Model.Tests/TransformVectors.json` and `GrooveVectors.json` (inputs and exact expected events);
    workstreams B and C test against them.
  - Groove extraction and quantise; keyswitch switching in renders.

## Integration

1. Merge in this order: E, D, A, B, then C.
2. Run the two-process tests: Scripter through the real host, step-input forwarding, articulations in a bounce.
3. Run the Playwright golden path for the exit criterion.
4. Fix the seams.

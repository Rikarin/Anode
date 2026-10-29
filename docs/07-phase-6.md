# 07 — Phase 6: loops, patterns and performance

From [the plan, §12](01-implementation-plan.md#phase-6-loops-patterns-and-performance-8-weeks), §7 (T2 instruments and
effects, MIDI FX) and §11 (Loops and media, Smart Controls, Live Loops and Step Sequencer).

**Exit criterion.** A beat-maker's session can be built and performed from Anode's own content. The Playwright golden
path covers it on the null device:
1. **Content.** Install the test content pack (`.anodepack`) in the Sound Library window. The Loop Browser lists its
   loops. Filter by the keyword buttons (Drums, Bass) and by key. Preview a loop in sync with the project.
2. **Loops.** In a 110 bpm project in D minor, add an audio drum loop recorded at 100 bpm in C and a MIDI bass loop:
   - the audio loop follows tempo (a bounce has its onsets on the 110 bpm grid within 5 ms);
   - the MIDI loop is transposed to D.
3. **Drum Machine Designer.** Create a DMD track with a kit of Drum Synth and Quick Sampler pads. Edit a pad's sound and
   put an insert on one pad's channel strip.
4. **Step Sequencer.** Make a pattern region on the DMD track:
   - kick, snare and hat rows; per-step velocity, gate, repeats, chance and offset;
   - a 7-step hat row against 16-step rows; ping-pong play mode on one row.
   A bounce has the hits where the pattern says (within 1 ms). Then **convert the pattern to a MIDI region**, which
   plays identically.
5. **MIDI FX.** Put an Arpeggiator (1/16, up, 2 octaves) and a Chord Trigger on a synth track. Hold one note: a bounce
   has an onset every 16th within 2 ms.
6. **Smart Controls.**
   - Open them for the synth track; automatic mapping fills 8 knobs.
   - Map one knob manually to two parameters, one through an inverted mapping graph.
   - Automate the knob; Compare shows the saved state.
7. **Live Loops.**
   - Fill a grid: the pattern, the drum loop, the bass loop and a recorded MIDI cell.
   - Launch scene 1; launch a cell in scene 2, quantised to the next bar (it starts on the bar within 1 ms).
   - Engage Remix FX (filter and repeater) during playback.
   - Record the performance: it lands in the Tracks area as regions and automation where it was played.
8. **T2 sound.** Use the FM synth, the drawbar organ (with its rotary), the electric piano and the wavetable synth on
   tracks. Put a convolution reverb (with an imported IR) and a Multipressor on buses. Every T2 plugin's editor opens.
9. Save, close and reopen: everything is intact.

The contracts below are committed code. Each workstream builds against them and changes them only with a note in its
report. A REST change means regenerating `engine/src/Anode.Server/openapi.json`
(`ANODE_UPDATE_OPENAPI=1 dotnet test`) and the editor client (`nx run api-client:generate`).

## Scope

**In:**
- **Tagged loops:**
  - Anode's loop format: a WAV or MIDI file plus a JSON sidecar `<file>.anodeloop.json` (kind, tempo, beats, key,
    scale, tags, transients).
  - ACID WAVs imported (the `acid` chunk: root note, beats, one-shot flag) and BWF `iXML` read where present.
  - Audio loops follow tempo (Phase 5's Flex & Follow) and key (transpose); MIDI loops follow both by playing in ticks
    and transposing notes.
  - "Add to Loop Library" from a region.
- **Content packs:**
  - A `.anodepack` is a zip of loops, patches, kits and IRs with a `pack.json` manifest.
  - Install from a file, list and remove in a Sound Library window.
  - Anode Essentials, our own pack, is built by a content builder from Anode's own instruments. It is generated, not
    committed.
- **Loop Browser:**
  - Keyword buttons, columns (name, tempo, key, beats), search, scale filter, favourites.
  - In-sync preview; drag to the Tracks area or a Live Loops cell.
- **Step Sequencer and pattern regions:**
  - Rows for notes, chord degrees or a drum pad.
  - Per-step velocity, gate, repeats (ratchets), chance and offset.
  - Per-row length (1–64) and rate.
  - Play modes: forward, reverse, ping-pong, random.
  - Swing, randomise, step and live record, pattern templates, convert to MIDI.
- **Drum Machine Designer:**
  - A DMD track is a track stack whose sub-tracks are pads, each with its own instrument and channel strip, triggered
    by one note.
  - Kits are patches; pads take Drum Synth or Quick Sampler.
- **MIDI FX** on instrument tracks, before the instrument: Arpeggiator, Chord Trigger, Modifier, Modulator, Note
  Repeater, Randomizer, Transposer (scale-aware), Velocity Processor.
- **Smart Controls:**
  - Layouts of 8 or 12 knobs, plus switches and a pad.
  - Automatic mapping from the plugins' declared macro hints; manual mapping of a control to any number of parameters,
    each with a range, invert and a mapping graph (curve points).
  - Automation of a control; Compare.
  - The arpeggiator button.
- **Live Loops:**
  - A grid of scenes × tracks; cells hold audio, MIDI or pattern content.
  - Quantised start (off, 1/16 … 4 bars); play modes (loop, one shot, one shot retrigger).
  - Track activation: a track plays either the Tracks area or its cells.
  - Scene launch, stop, record into cells, a cell editor.
  - Remix FX; performance recording to the Tracks area.
- **Transport context for plugins:** effects, MIDI FX and instruments get the playing state, position in quarter
  notes, tempo, signature and bar start every block, from a tempo map in the segment. This replaces Phase 4's
  tempo-from-the-clicks.
- **T2 instruments:**
  - Drum Synth: kick, snare, clap, hat, tom, cymbal, percussion models.
  - FM synth: 4 operators, 8 algorithms.
  - Drawbar organ: 9 drawbars, percussion, key click, chorus/vibrato scanner, built-in rotary.
  - Electric piano: modelled tines and reeds.
  - Wavetable synth: 2 wavetable oscillators with position modulation, a sub, filter, 3 envelopes, 2 LFOs, a
    modulation matrix.
  - Quick Sampler: one-shot, classic, slice and recorder modes.
- **T2 effects:**
  - Convolution reverb (IR import, pre-delay, length, filter, mix).
  - Linear Phase EQ.
  - Multipressor (4 bands).
  - Enveloper, Exciter, SubBass.
  - Delay Designer (up to 26 taps).
  - Pitch Shifter.
  - Pitch Correction (real-time, scale-aware).
  - Ensemble, Rotor Cabinet, Phat FX.
  - Step FX, Beat Breaker.
  - Remix FX.

**Out:**
- Scripter (Phase 7).
- Hardware assignment of Smart Controls (Phase 8).
- Downloading packs from the internet: local `.anodepack` install only.
- Apple Loops (CAF) import.
- Vocoder, Match EQ, amp and pedalboard simulation (waits for the Cathode DSP extraction), Vocal Transformer, Spectral
  Gate, Ringshifter, clav, Studio-instrument players.
- Granular synthesis in the wavetable synth.
- Drummer (Phase 11).

## Contracts

| What | Where |
|---|---|
| Pattern regions: rows, steps, play modes, rates | `engine/src/Anode.Contracts/Patterns.cs`; `Project.PatternRegions` |
| Drum Machine Designer: a track's `drumMachine` (pads: note, name, sub-track id) | `Patterns.cs`; `Track.DrumMachine` |
| MIDI FX slots on a strip (`midiEffects`, same `Insert` shape) | `Project.cs` / `Mixer.cs` |
| Smart Controls: layout, controls, mappings, graphs | `engine/src/Anode.Contracts/SmartControls.cs`; `Track.SmartControls` |
| Live Loops: grid, scenes, cells, quantise, play modes, track activation, Remix FX state | `engine/src/Anode.Contracts/LiveLoops.cs`; `Project.LiveLoops`, `Track.LiveLoopsActive` |
| Loops and packs: loop metadata, loop query and results, packs | `engine/src/Anode.Contracts/Loops.cs` |
| New commands (about 24) | `engine/src/Anode.Contracts/Commands.cs` |
| `/ws` `cell` events (cell state: queued, playing, stopping; at which sample) | `Realtime.cs`, mirrored in `models.ts` |
| REST (stubbed): `/library/loops` (query), `/library/loops/{id}/preview`, `/library/loops` (POST: add a region to the loop library), `/library/packs` (GET, POST install, DELETE), `/project/loops` (POST: add a loop to the project), `/project/live-loops/launch`, `/stop`, `/record` | `engine/src/Anode.Server/Api/LoopsApi.cs` → `openapi.json` → `@anode/api-client` |
| Segment format 4: `TempoPoint` array (sample, quarter-note position, bpm, ramp); per-track `MidiEffects`; cells (event and playlist slices per cell); DMD note routes | `engine/src/Anode.Ipc/Segments/Layout.cs` |
| IPC: `EngineStrip.MidiEffects`; `EngineTrack.NoteRoutes`, `LiveLoopsActive`; `launchCells`, `stopCells`, `cellState`; `setPreviewSync` (loop preview at a tempo ratio and transpose) | `engine/src/Anode.Ipc/IpcMessage.cs` |
| `TransportContext` (playing, sample, quarter notes, bpm, signature, bar start quarter note); `IMidiEffect`; effects and instruments receive the context | `engine/src/Anode.Dsp/Transport/`, `Anode.Dsp/MidiEffects/IMidiEffect.cs`, `IAudioEffect`, `IInstrument` |
| Plugin metadata: macro hints (which parameters Smart Controls maps first), category `midiEffect` | `engine/src/Anode.Contracts/Plugins.cs`, `Anode.Dsp` descriptors |

### Rules the contracts imply

- **Tempo in the engine:**
  - The segment carries the compiled tempo map (after Varispeed). The engine computes each block's `TransportContext`
    from it.
  - Effects, MIDI FX and instruments sync to it; Phase 4's derived tempo goes.
- **MIDI FX:**
  - They run on the audio thread, in slot order, on the block's MIDI (regions, live input and step input) before the
    instrument.
  - `IMidiEffect.Process(in TransportContext, ReadOnlySpan<MidiEvent> input, MidiEventBuffer output)`: allocation-free,
    at most 1,024 events a block.
  - Bypassed slots pass through. Their parameters automate like insert parameters.
- **Pattern regions:**
  - The server compiles them to note events, unrolled over the region's length.
  - **Chance** draws from a deterministic generator seeded by (region id, row, step, pass). The same project gives the
    same notes; a pattern's "reseed" command changes its seed.
  - **Ping-pong** does not repeat the end steps.
  - Rows with different lengths cycle independently.
  - **Swing** delays even 16ths by `swing` × half a 16th.
  - **Offset** is −50 … +50 % of a step.
  - **Repeats** split the gate evenly.
  - "Convert to MIDI region" replaces the pattern with exactly those notes for one pass of the region.
- **Drum Machine Designer:**
  - The DMD track is a stack. Each pad is a sub-track with an instrument, and the DMD's `drumMachine.pads` maps a note
    to a pad.
  - The compiler routes the DMD track's region and pattern notes to pad tracks by note.
  - Live MIDI to the DMD track is routed by the host through `NoteRoutes`.
  - A pad's instrument always plays its own sound: the note is passed as the pad's base note (Drum Synth ignores pitch;
    Quick Sampler plays at its root).
- **Smart Controls:**
  - A control's value is 0…1.
  - Each mapping scales it through its graph (piecewise linear, 2–16 points, default the line 0→1), then into
    `min … max` of its target parameter (inverted if `invert`).
  - The server applies a control change as the parameter changes, coalesced into one undo step.
  - Control automation compiles to automation of every mapped parameter, sampled as Phase 4's curves are.
  - **Automatic mapping** takes the plugins' macro hints in order (instrument first, then inserts), up to the layout's
    knobs.
  - **Compare** toggles between the controls' saved and edited values without an undo step.
- **Live Loops:**
  - Cells are compiled like regions (events and playlists) into per-cell slices of the segment.
  - The host launches and stops them sample-accurately at the next quantise boundary, computed from the tempo map.
  - A launch arriving within 1 ms of a boundary is late: it takes the next one.
  - A track whose `liveLoopsActive` is on plays its playing cell and not its Tracks-area regions.
  - Audio cells are loaded entirely into memory at model load (warped by the disk thread then; at most 60 s a cell). A
    launch never waits for disk.
  - **Play modes:** loop; one shot (stops at its end); retrigger (a launch restarts).
  - The host pushes `cellState` (queued, playing, stopping, stopped, with the sample).
  - **Performance recording:**
    - While recording, the server turns `cellState` into regions on the track at the samples they played, and Remix
      FX moves into automation.
    - This runs in one undo step at the end.
- **Loops:**
  - Adding an audio loop makes a region with `follow: on`, using the file's tempo from the sidecar, not analysis, and
    `transpose` = the interval from the loop's key to the project's (−6…+5 semitones).
  - A MIDI loop becomes a MIDI region with its notes transposed.
  - A pattern loop becomes a pattern region.
  - **One-shots:** a loop with no tempo is added unwarped.
  - **Preview** plays through the preview channel at the project tempo and key (`setPreviewSync`, rendered with the
    Stretch renderer on the disk thread), starting on the next bar while playing.
- **Packs:**
  - Installed under `<DataDirectory>/Library/Packs/<packId>/`; the loop index is
    `<DataDirectory>/Library/Loops/index.json`, rebuilt from packs and user loops.
  - Removing a pack removes its loops from the index. Projects that used them keep their copies: adding a loop copies
    its file into the project.
- **Remix FX:**
  - A built-in effect, `anode.remix-fx`, on the master by default when Live Loops is shown.
  - Two XY pads (filter/wobble/orbit; repeater/reverse/scratch/gater/downsampler/tape stop), synced to the transport
    context.
  - Its parameters automate.

### Contract notes

Where the committed code differs from, or adds to, the shapes planned for this commit, and the shapes chosen where
the plan left latitude:
- **Patterns** (`Patterns.cs`): `PatternRegion(id, trackId, name, startTick, lengthTicks, muted, pattern)`;
  `Pattern(rows, swing, seed, stepTicks = 240)`; `PatternRow(id, kind, note, name, length, stepTicks?, playMode,
  muted, steps)`; `PatternStep(on, velocity, gate, repeats, chance, offset)`; enums `PatternRowKind`
  (`note | chordDegree | pad`) and `PatternPlayMode` (`forward | reverse | pingPong | random`). A row's rate is
  `stepTicks` (ticks per step, so triplets fit; null: the pattern's). `gate` is in steps, 0…16: over 1 ties into the
  next steps. A `chordDegree` row plays a triad of the key's scale on degree `note` (1…7), its root in the octave from
  note 60. `DrumMachine(pads)` with `DrumPad(note, name, trackId)`, on `Track.drumMachine` (null elsewhere).
  Workstream B fixes the chance generator and the unrolling in `PatternVectors.json`.
- **MIDI FX:** `ChannelStrip.midiEffects` (the `Insert` shape, never a sidechain). Rather than five new commands, the
  Phase 4 insert commands (`addInsert`, `removeInsert`, `moveInsert`, `setInsert`, `setInsertParameter`) take
  `chain: inserts | midiEffects` (`InsertChain`, default inserts): a slot has the same shape, ids, validation,
  parameter coalescing and automation recording as an insert, and the editor's slot UI is the same. Automation and
  Smart Control targets address a slot's parameter as `midiEffect:{slotId}:{parameterId}`. MIDI FX slots travel to
  the engine in `EngineStrip.midiEffects`, not in the segment (the table's "per-track `MidiEffects`").
- **Smart Controls** (`SmartControls.cs`): `SmartControls(layout, controls, arpeggiator?, comparing)`;
  `SmartControl(id, name, kind, value, savedValue?, mappings)`; `SmartMapping(target, min, max, invert, graph)`;
  `GraphPoint(x, y)`; `SmartControlsLayout` (`knobs8 | knobs12`); `SmartControlKind` (`knob | switch | pad`: the
  scope's XY pad is two controls, `padX` and `padY`). Control ids are fixed per layout (`knob1`…, `switch1`,
  `switch2`, `padX`, `padY`) and `controls` lists all of them. Added `comparing`, so Compare is a state the editor
  shows (`compareSmartControls(trackId, on)` swaps values and saved values, without an undo step). Targets are the
  automation lanes' addresses; a control's own lane is `smartControl:{controlId}`. `Track.smartControls` is null until
  set up (the first Smart Controls command sets it up), so tracks need no defaults.
- **Live Loops** (`LiveLoops.cs`): `LiveLoopsGrid(scenes, cells, quantize, shown)`, a new project's empty, `bar`,
  hidden; `Scene(id, name)`; `Cell(id, trackId, sceneId, kind, name, lengthTicks, playMode, quantize?, content)`;
  `CellContent(audio?, midi?, pattern?)`: three nullable snapshots, exactly one set (the cell's `kind`), its `id` the
  cell's, its `startTick` 0. Enums `CellKind`, `CellPlayMode` (`loop | oneShot | retrigger`), `CellState` (`queued |
  playing | stopping | stopped`) and **`LaunchQuantize`** (`off | sixteenth | eighth | quarter | half | bar | twoBars
  | fourBars`), renamed from `Quantize`, which clashes with `Anode.Model.Editing.Quantize`. Remix FX is only the
  master insert `anode.remix-fx` (documented on `LiveLoopsGrid`).
- **Deltas:** `patternRegionUpserted(region)`, `patternRegionRemoved(id)`, `liveLoopsChanged(liveLoops)` (the whole
  grid). `/ws` event `cell(cellId, state, sample)`.
- **Loops** (`Loops.cs`): `LoopKind`; `LoopScale` (`major | minor | neither | both`; a loop's key is a tonic `int?`
  rather than a `KeyEvent`, which has a tick, and its scale needs `neither` and `both`); `LoopInfo(id, name, kind,
  packId?, path, tempo?, beats?, key?, scale, tags, oneShot, favourite)`; `LoopQuery(text?, tags?, kind?, key?, scale?,
  favouritesOnly?, offset?, limit?)` for the server's index, which `GET /library/loops` takes as camelCase query
  parameters (`kind` and `scale` as strings: query binding does not parse contract enum names; the stub parses them);
  `LoopPage(items, total)`; `Pack(id, name, version, author, loopCount, size, installed)`; `InstallPackRequest(path)`;
  `AddLoopRequest(loopId, trackId?, tick, cellSceneId?)`; `AddToLoopLibraryRequest(regionId, name, tags, key?, scale?,
  oneShot)`; `LaunchRequest(cellIds?, sceneId?)`; `StopRequest(trackIds?)`; `LiveRecordRequest(on, trackId?,
  sceneId?)` (a slot: record into that cell; none: a performance recording); `LoopSidecar(version, kind, name?,
  tempo?, beats?, key?, scale, tags, oneShot, transients?, pattern?)`: a pattern loop's sidecar holds its pattern.
  Added `LoopFavouriteRequest(favourite)` and `PUT /library/loops/{id}/favourite`, as favourites are in scope.
- **Commands** (22, plus the `chain` field): `addPatternRegion`, `setPatternRegion`, `setPattern`,
  `convertPatternToMidi`, `reseedPattern`; `createDrumMachine`, `addDrumPad`, `setDrumPad` (rename, instrument,
  `toNote`), `removeDrumPad`; `setSmartControls`, `setSmartControl`, `setSmartMappings`, `autoMapSmartControls`,
  `compareSmartControls`; `addScene`, `removeScene`, `setScene`, `setCell` (by track and scene; `gridQuantize: true`
  clears a cell's own quantise), `deleteCells`, `moveCell`, `setLiveLoops`, `setTrackActivation` (several tracks).
  `moveRegion`, `copyRegions` and `deleteRegions` take pattern regions. `addPatternRegion.pattern` and
  `setCell.content` are nullable references, which the OpenAPI document lists as required (as `setTrack.input`):
  clients send null.
- **REST** (`LoopsApi.cs`, `Pending.Phase6`): `QueryLoops`, `PreviewLoop` (`POST /library/loops/{id}/preview`;
  `POST /files/preview` with a null path stops it), `SetLoopFavourite`, `AddToLoopLibrary`, `ListPacks`, `InstallPack`,
  `RemovePack`, `AddLoop` (answers a `CommandResult`), `LaunchCells`, `StopCells`, `RecordLiveLoops`.
- **Segment format 4** (`Layout.cs`): `TempoMapPoint` (renamed from `TempoPoint`, which `Anode.Model.Time` has) is 48
  bytes: `Sample`, `Quarter`, `Bpm` (playback tempo, Varispeed folded in), `Slope` (bpm per quarter note: positions
  between points are exact, an exponential in time), and the signature (`BarQuarter`, `Numerator`, `Denominator`),
  so the context's bar start needs no second array; shaped ramps are sampled into linear pieces. `CellEntry` (56
  bytes: `Index`, `TrackId`, event and playlist slices, `Length`, `Mode` as `CellMode`): cells' content lives in the
  shared event, playlist, warp and pitch arrays after the tracks', positions relative to the cell's start.
  `SegmentWriter.Write`/`Size` take optional `tempo` and `cells` (`CellModel`); `MappedSegment` has `Tempo`,
  `TempoCount`, `CellCount` and `Cell(i)` (a `CellView`), validates cell ranges and checks tempo order in `Verify`.
  A per-track segment carries no tempo points and its tracks' cells. Note routes are `EngineTrack.noteRoutes`.
- **IPC:** `EngineStrip.MidiEffects`; `EngineTrack.NoteRoutes` (`EngineNoteRoute(note, trackId)`) and
  `LiveLoopsActive`; `LaunchCellsMessage(cells: CellLaunch(cell, quantize), stops?: CellStop(trackId, quantize))`
  (the server resolves scenes and quantise overrides, so one message is one decision), `StopCellsMessage(stops)`,
  `CellStateMessage(cell, state, sample)`, `SetPreviewSyncMessage(tempoRatio, transposeSemitones, startOnBar)`; enums
  `EngineQuantize` and `EngineCellState`; `EngineParameter.Macro`; automation target kind `midiEffect`.
- **DSP:** `TransportContext` also carries `Frames` (a MIDI effect needs the block's length; the `Process` signature
  stays as planned), with `QuartersPerSample`, `BarQuarters`, `OffsetOf` and `Stopped`. `IAudioEffect` and
  `IInstrument` get `SetTransport(in TransportContext)` as an empty default interface member (NativeAOT supports
  them; implementations compile unchanged). `IMidiEffect` with `MidiEffectDescriptor`; `MidiEventBuffer` (`Add`
  returns false when full; `Sort` is a stable insertion sort); `BuiltinMidiEffects` (empty; `ArpeggiatorId`).
  Macro hints: `InstrumentParameter.Macro` (`int?`, ascending order), `EffectParameters.Macro(index, order)`,
  `ParameterInfo.macro`. A MIDI effect's category is `MIDI Effect` (`EffectCategories.MidiEffect`), a display string
  like `Instrument`, not `midiEffect`; both catalogues list `BuiltinMidiEffects.All`.
- **Interim:** schema-5 packages read through `Phase6Fields.Fill` (strips' `midiEffects` empty, `liveLoopsActive`
  false), which workstream B replaces with schema 6 and its migration. The model refuses the new commands and
  `chain: midiEffects` (422); the identity property skips `RandomProjects.PendingPhase6`; the editor's fake Server
  answers them 422.

## Workstreams

Five, in parallel, each on its own branch; they meet only at the contracts above.

### A — Engine (`Anode.Ipc`, `Anode.Engine`, `Anode.EngineHost`, `Anode.Host`)

- **Segment format 4:**
  - Tempo points and the per-block `TransportContext`, replacing the derived tempo.
  - MIDI FX slots.
  - Cell slices; note routes.
- **MIDI FX:** the chain per instrument track (built-ins from `BuiltinMidiEffects`, against `IMidiEffect`), on region,
  live and step MIDI; parameters and automation.
- **Drum Machine Designer:** live MIDI routing by note to pad tracks.
- **Live Loops launcher:**
  - Quantised sample-accurate launch and stop; scenes; play modes; track activation.
  - Audio cells in memory, warped at load; `cellState` pushes.
  - Allocation-free and lock-free on the callback (test).
- **Loop preview in sync:** the preview channel through the Stretch renderer at a tempo ratio and transpose, starting on
  the next bar while playing.
- **Pass the transport context** to effects and instruments; built-ins that synced to tempo use it.
- **Tests:**
  - Launch accuracy at every quantise (within 1 sample of the boundary).
  - Scene changes; one-shot ends.
  - MIDI FX ordering and bypass; DMD routing.
  - Transport context across tempo ramps and cycle jumps; preview sync.
  - No allocations.
  - Callback cost with 32 tracks of MIDI FX and 16 playing cells.

### B — Server domain (`Anode.Model`, `Anode.Server`, a content builder)

- **Model:**
  - Every new command with validation and inverses; pattern regions, DMD, MIDI FX slots, Smart Controls, Live Loops,
    track activation.
  - Remove the `PendingPhase6` placeholders.
  - Package schema 6 with a migration from 5.
- **Compiler:**
  - Tempo points; pattern unrolling (chance seeds, ping-pong, swing, offset, repeats, per-row lengths).
  - DMD routing of notes to pads.
  - Smart Control automation expansion; cells; MIDI FX slots.
- **Server:**
  - Loops: sidecar and ACID/iXML reading; the loop index and query; adding loops (key and tempo following); "Add to
    Loop Library".
  - Packs: install, list, remove.
  - Smart Control application and automatic mapping.
  - Live Loops launch, stop and record, and performance recording into regions and automation.
  - Loop preview control.
- **Content builder:**
  - A small tool, `engine/tools/Anode.ContentBuilder`, renders a pack offline from Anode's own instruments (Drum Synth
    kits, synth and bass MIDI loops, a few audio loops at several tempi and keys) with sidecars and a manifest.
  - `scripts/build-content.mjs` writes `engine/artifacts/content/AnodeEssentials.anodepack`.
  - The e2e uses a small test pack built the same way.
  - Until workstream E's instruments land, it renders with the T1 synth and sampler.
- **`FakeHost`:** the new messages; cells; MIDI FX as pass-through.
- **Tests:**
  - Properties for every command.
  - Pattern compilation vectors (shared with the editor in `PatternVectors.json`).
  - Smart Control maths; loop key and tempo following; ACID parsing.
  - Pack install and remove; performance recording.
  - Migration; API and host-link tests.

### C — Editor (`editor/`; built with `editor/DESIGN.md`)

- **Loop Browser:** keyword buttons, columns, search, key and scale filter, favourites, in-sync preview, drag to the
  Tracks area and to cells.
- **Sound Library window:** packs (install from file, remove).
- **Step Sequencer:**
  - The pattern editor as Logic's: rows with their own lengths and rates, per-step edit modes (on/off, velocity, gate,
    repeats, chance, offset).
  - Row play modes; swing; randomise and templates; step and live record.
  - Pattern regions drawn in the Tracks area; convert to MIDI.
- **Drum Machine Designer:** the pad grid, pad instrument editors (Drum Synth, Quick Sampler), kits from the Library,
  pad channel strips in the Mixer.
- **MIDI FX slots** in the channel strip, with generic editors.
- **Smart Controls pane:**
  - Layouts; the Smart Controls inspector with mappings and a mapping-graph editor.
  - Automatic mapping, Compare, the arpeggiator button; automation lanes for controls.
- **Live Loops grid:**
  - Scenes and cells; quantise; cell play modes.
  - Track activation buttons; record into cells; the cell editor.
  - The Remix FX pads; performance recording.
  - Side by side with the Tracks area, as Logic's.
- **Generic editors** for the T2 plugins, plus custom panels where a generic one is unusable: the drawbars, the FM
  algorithm picker, the Delay Designer taps, the Multipressor bands.
- **Fake server:** every new command, endpoint and event; remove the placeholders.
- **Tests:**
  - Pattern vectors parity.
  - Smart Control maths.
  - Grid launch and quantise state machine.
  - Stories with axe for the new audio-ui parts (step cell, pattern row, Live Loops cell, XY pad, drawbar, mapping
    graph).
  - The golden-path spec, `test.fixme` until integration.

### D — T2 effects and Remix FX (`Anode.Dsp/Effects`)

- **The effects in scope**, against `IAudioEffect` with the transport context:
  - Convolution reverb: uniformly partitioned FFT convolution, zero latency through a direct head; IRs from WAV.
  - Linear Phase EQ: FIR, with its latency reported.
  - Multipressor, Enveloper, Exciter, SubBass, Delay Designer, Pitch Shifter.
  - Pitch Correction: low-latency YIN detection and a PSOLA shift to the scale.
  - Ensemble, Rotor Cabinet, Phat FX, Step FX, Beat Breaker, Remix FX.
- **Macro hints** for Smart Controls on every built-in effect (T1 too).
- **Tests:**
  - Per effect, in the style of Phase 4's: null and impulse tests; latency matching its report.
  - Tempo sync against the transport context.
  - Pitch Correction landing within 10 ¢ on a detuned tone.
  - Convolution equal to direct convolution within 1e-5.
  - No allocations.
  - A CPU table.
  - Opt-in demos.

### E — T2 instruments and MIDI FX (`Anode.Dsp/Instruments`, `Anode.Dsp/MidiEffects`)

- **The instruments in scope** against `IInstrument` (with the transport context): Drum Synth, FM synth, drawbar organ
  with rotary, electric piano, wavetable synth (with its wavetables generated in code), Quick Sampler.
- **The eight MIDI FX** against `IMidiEffect`, registered in `BuiltinMidiEffects`.
- **Macro hints** on every built-in instrument (T1 too).
- **Tests:**
  - Each instrument: renders, is silent at rest, follows pitch (within 2 ¢ where pitched), polyphony and voice
    stealing, state round trip.
  - Each MIDI FX: event-exact outputs (the arpeggiator's timing to the sample at several tempi; Chord Trigger's chords;
    Transposer to a scale).
  - No allocations.
  - A CPU table (voices at 64 frames).
  - Opt-in demos.

## Integration

1. Merge in this order: D, E, A, B, then C.
2. Build the test pack and Anode Essentials with the real instruments.
3. Run the two-process tests: launch accuracy, MIDI FX, DMD, preview sync.
4. Run the Playwright golden path for the exit criterion.
5. Fix the seams.

## Status (2026-10-29)

**Exit criterion met on macOS.** The Playwright golden path `loops-performance-golden-path.spec.ts` passes on the real
desktop app with the null device and the test pack, with all nine steps. All seven e2e specs pass, twice in a row.

| Criterion | Measured | Limit |
|---|---|---|
| Drum loop (100 → 110 bpm): worst onset off the 16th grid | 0.28 ms | 5 ms |
| Bass loop transposed C → D: first note off D | 2.0 ¢ | — |
| Pattern hits off where the pattern says (16 of 16 found) | 0.21 ms | 1 ms |
| Converted MIDI against the pattern | 0.00 ms | 1 ms |
| Arpeggiator: worst onset off the 16th grid | 0.24 ms | 2 ms |
| Scene-2 cell launched on the bar, and its performed region | 0.03 ms off the bar | 1 ms |

| Check | Result |
|---|---|
| Engine tests (`dotnet test --solution engine/Anode.slnx --max-parallel-test-modules 2`) | 1753 pass, 17 skipped (opt-in) |
| Engine | Live Loops launches within 1 sample of every quantise boundary, through tempo ramps; 32 tracks with 3 MIDI FX each and 16 playing cells: 50 µs mean of the 1,333 µs callback; no allocations |
| T2 effects ([docs/effects/T2.md](effects/T2.md)) | Pitch Correction within 0.07 ¢; convolution within 1.9e-6 of direct; Multipressor flat within ±0.05 dB; Step FX and Beat Breaker sample-exact through tempo changes; heaviest, the convolution reverb, 20× real time |
| T2 instruments and MIDI FX ([docs/instruments/T2.md](instruments/T2.md)) | pitch within 0.03 ¢ (0.14 ¢ Quick Sampler at note 96); organ with rotary 4 % of a 64-frame block for 32 voices; Arpeggiator sample-exact at 90–174 bpm and across a ramp |
| Content | Anode Essentials (22 files, 6 MB) and the test pack built from Anode's own instruments by `scripts/build-content.mjs` |
| Editor (`nx run-many -t typecheck lint test build`) | 5 projects pass; Storybook + axe 119 tests |
| e2e (`nx e2e desktop`) | loops and performance, advanced audio, mixing, arranging, MIDI and Phase 1 golden paths, smoke; twice in a row |
| Skala, Prettier, generated client | clean |

**Contract changes and decisions at integration:**
- MIDI FX use the Phase 4 insert commands with `chain: midiEffects`; a Drum Machine Designer's stack takes none (its
  notes are routed to the pads before any instrument), only its pads.
- Pad instruments receive the DMD note unchanged: the Drum Synth ignores pitch, and a Quick Sampler pad has
  `keyFollow` off.
- `loadInsertFile` and `Insert.file` load an impulse response into the convolution reverb (`IFileEffect`); an IR from
  outside the project is copied into its `Impulse Responses` folder.
- Kits are library patches of kind stack, with pads; pack paths are made absolute inside the installed pack.
- Launching a cell while stopped starts playback, as in Logic; the server activates the cell's track first.
- `setSmartControl` drags coalesce into one undo step; saving clears the values Compare returns to.
- Record into Cell record-enables the track and starts recording; the take becomes the cell, in whole bars.
- A track added among a stack's members goes after them, so a DMD's pads stay together.
- The Processing dialog's effect menu scrolls (the T2 effects made it taller than the window).

**Open:**
- Loop preview plays audio loops only; MIDI and pattern loops answer 422.
- Track alternatives do not carry pattern regions.
- Moving a track, or adding an aux, can still place it among a stack's members (adding a track no longer does).
- Kits and loops that point into a removed pack keep their paths into it (loop files themselves are copied into the
  project).
- A `stopping` cell event carries only the stop boundary, so an editor that connects while a cell is stopping cannot
  draw its progress.
- The Multipressor's per-band gain readings are not sent to the editor.
- The phase 1 golden path failed once on a `toHaveAttribute` assertion in a full run and did not reproduce in ten.
- The editor's initial bundle is 1.13 MB against a 1.00 MB budget (a build warning).
- Linux and Windows.

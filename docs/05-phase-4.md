# 05 — Phase 4: mixing, automation, export

From [the plan, §12](01-implementation-plan.md#phase-4-mixing-automation-export-10-weeks).

**Exit criterion: beta.** The full production workflow runs from record to export. The Playwright golden path covers
it on the null device:
1. Record and edit (Phases 1–3).
2. Mix:
   - inserts of built-in effects (EQ, compressor, reverb) and a sandboxed third-party plugin (a test VST3 or CLAP
     effect);
   - sends to an aux reverb bus;
   - output routing;
   - a mixer group;
   - a VCA.
3. Automate volume and a plugin parameter, touch-recording a fader during playback.
4. Freeze an instrument track.
5. Save a patch and apply it.
6. Bounce the mix and stems to WAV and AIFF, with dither and loudness normalisation; the result is checked against the
   target.
7. Reopen the project, with everything intact.

The contracts below are committed code. Each workstream builds against them and changes them only with a note in its
report. A REST change means regenerating `engine/src/Anode.Server/openapi.json`
(`ANODE_UPDATE_OPENAPI=1 dotnet test`) and the editor client (`nx run api-client:generate`).

## Scope

**In:**
- **Channel strips:** insert slots (built-in, CLAP, VST3, AU), sends (pre/post, pan), output routing (master,
  buses 1–64, device output pairs), aux tracks fed by buses, mixer groups with Logic's linked properties, VCA tracks.
- **Signal processing across the mixer:** sidechain inputs, plugin delay compensation (all / tracks / off),
  low-latency mode.
- **Automation:**
  - Track automation of volume, pan, mute, sends, insert and instrument parameters.
  - Modes: off, read, touch, latch, write.
  - Curves; sample-accurate playback; recording from control moves.
- **Built-in effects (T1 suite):** see workstream D.
- **Plugin hosting:**
  - Third-party effects and instruments in grouped sandboxes (ADR-0003, spike S4's design).
  - Plugin editor windows in the desktop app (spike S6 answered here).
  - Crash isolation.
- **Patches:** channel-strip patches (save, apply).
- **I/O labels.**
- **Freeze.**
- **Bounce and export:**
  - Offline or real-time rendering of the mix or stems.
  - WAV and AIFF at 16, 24 or 32-bit float, with sample-rate conversion.
  - TPDF or noise-shaped dither.
  - Peak or loudness (EBU R128) normalisation with a true-peak ceiling.
- **Mixer views:** all, tracks, buses and outputs, with filters.

**Out:**
- Region automation.
- Surround.
- MP3, AAC and FLAC export (Phase 9).
- Bounce in place, Smart Controls, MIDI FX, T2 and T3 effects.

## Contracts

| What | Where |
|---|---|
| Channel strip (inserts, sends, output, groups, VCA); track kinds `aux`, `vca`; `inputBus`, `automationMode`, `frozenFileId`; project groups and automation lanes; mixer settings | `engine/src/Anode.Contracts/Project.cs`, `Mixer.cs` |
| Inserts, sends, groups, automation lanes and modes, PDC, I/O labels, patches, plugin editor | `engine/src/Anode.Contracts/Mixer.cs` |
| Jobs, bounce request, formats, dither, normalisation | `engine/src/Anode.Contracts/Jobs.cs` |
| Plugin formats `vst3`, `au`; plugin category, sidechain, editor | `engine/src/Anode.Contracts/Plugins.cs` |
| 20 new commands | `engine/src/Anode.Contracts/Commands.cs` |
| Deltas for automation lanes and groups | `engine/src/Anode.Contracts/Delta.cs` |
| `/ws` `job` and `pluginProblem` events | `engine/src/Anode.Contracts/Realtime.cs`, mirrored in `editor/libs/api-client/src/lib/models.ts` |
| REST (stubbed): `/project/bounce`, `/project/tracks/{id}/freeze`, `/jobs/{id}`, `/jobs/{id}/cancel`, `/library/patches…`, `/settings/io-labels`, `/plugins/editor` | `engine/src/Anode.Server/Api/MixApi.cs` → `openapi.json` → `@anode/api-client` |
| IPC: `EngineStrip` inserts, sends and output; `EngineTrack.InputBus` and `FreezeFile`; automation targets; `setChannel`, `setInsertParameter`, `setSendLevel`, `setMixer`, `render…`, `openPluginEditor`, `parameterChanged`, `pluginProblem` | `engine/src/Anode.Ipc/IpcMessage.cs` |
| Automation events in segments: `PlaybackEventKind.Automation`, `PlaybackEvent.Target` | `engine/src/Anode.Ipc/Segments/Layout.cs` |
| Effect interface | `engine/src/Anode.Dsp/Effects/IAudioEffect.cs`, `BuiltinEffects.cs` |

### Rules the contracts imply

- **Graph:**
  - Each track is source → inserts (in order, bypassed ones skipped) → fader and pan → sends and output.
  - Pre-fader sends tap after the inserts and before the fader.
  - A bus sums every send and output routed to it; an aux track plays its input bus through its own strip.
  - The master sums everything routed to it, then its inserts, then its fader.
  - Routing loops are refused by the server: a bus may not feed, directly or indirectly, an aux whose output reaches
    that bus.
  - Device output pairs bypass the master.
- **Sidechain:** an insert's sidechain is another track's post-fader signal, or a bus. A source processed later in the
  same cycle is taken from the previous cycle, one block late; the host documents this.
- **Plugin delay compensation:**
  - The host computes each path's latency from `IAudioEffect.LatencySamples` (and plugins' reported latencies), and
    delays the shorter parallel paths into every summing point.
  - `tracks` compensates only up to buses and aux tracks; `off` does none.
  - Low-latency mode bypasses, on armed or monitored paths, inserts whose latency exceeds the limit.
- **VCA and groups:**
  - The server folds a VCA's volume into its members' `EngineStrip.Gain`; the VCA's mute and solo apply to them.
  - Groups are applied by the server when a member is edited: volume keeps the members' offsets.
- **Automation:**
  - The server samples curved segments into linear automation events in the segment (at most every 10 ms, finer where
    the curve bends), and steps at mute points.
  - The host interpolates linearly between events of a target, per sample, for volume, pan and sends, and per 32-sample
    sub-block for plugin parameters.
  - In `read`, `touch` and `latch` a held control overrides playback in the UI. The server records touch, latch and
    write moves into the lane when the edit command comes during playback, coalesced into one undo step per pass.
  - `off` sends no automation events.
- **Freeze:**
  - The host renders the track's source and inserts, pre-fader, from the project start to the end of its last region
    plus 2 s of tail.
  - The server adds the file as an audio file, sets `frozenFileId` in one undoable step, and sends `FreezeFile`.
  - A frozen track keeps its fader, sends and automation of volume, pan and sends.
- **Bounce:**
  - The host renders 32-bit float WAV.
  - The server post-processes:
    - sample-rate conversion (windowed-sinc);
    - normalisation: peak on 4× oversampled true peak, or loudness (EBU R128 gating) and then limiting to the ceiling;
    - dither and word length;
    - format: WAV or AIFF.
  - It reports through `job` events and writes to `directory` (default `<project>/Bounces`).
  - `addToProject` imports the result.
- **Patches:** saved in `<DataDirectory>/Library/Patches/<id>.json` (instrument, inserts with parameters and state,
  sends).
- **I/O labels:** saved in `<DataDirectory>/settings/io-labels.json`.
- **Plugin states:**
  - On save, the host answers `requestInstrumentStates` for inserts too: states keyed by track and insert id.
  - The server writes them to `Plugin States/<trackId>.<insertId>.bin`.
  - They are passed back as `EngineInsert.State`.
  - Extending `InstrumentState` with an optional insert id is allowed and must be listed.
- **Sandboxes (ADR-0003, S4):**
  - Grouped per vendor; cross-process wake on a shared address; real-time sandbox threads joined to the device's
    workgroup; deadline at 80 % of the period; bypass on a miss.
  - A crash bypasses the plugins in that sandbox and reports `pluginProblem`; reloading means re-adding the insert.
  - Built-in effects and instruments always run in the host.

## Workstreams

Five, in parallel, each on its own branch; they meet only at the contracts above.

### A — Engine graph (`Anode.Audio`, `Anode.Engine`, `Anode.EngineHost`, `Anode.Host`)

- **The mixer graph per the rules:**
  - Inserts through `IAudioEffect` (built-ins from `BuiltinEffects`; hosted plugins from workstream E's factory,
    against an interface you define with E's needs in mind — see E).
  - Sends (pre/post), buses, aux tracks, output routing including device output pairs, sidechain, VCA-folded gains,
    PDC (all / tracks / off), low-latency mode.
  - Structure changes through `setChannel`, keeping instances by insert id.
- **Automation playback** from segment events: per sample for strip and send gains and pan, per sub-block for
  parameters.
- **Rendering:** offline (`render`: master, stems, freeze; with progress, cancel and finish) and real-time.
- **Messages:** `parameterChanged` and `pluginProblem` plumbing.
- **Constraints:**
  - Zero allocations and no locks on the callback (test).
  - Callback cost measured with 64 tracks, each with 4 built-in inserts and 2 sends, 4 aux buses and master inserts.
  - Bit-exact offline render tests of routing, sends, PDC (a null test of a latent insert on one of two parallel
    paths) and automation ramps.

### B — Server domain (`Anode.Model`, `Anode.Server`)

- **Model:**
  - All 20 commands with validation (routing loops, bus range, group 1–32) and inverses.
  - Remove `RandomProjects.PendingPhase4` and the placeholder generators.
  - Group linking.
  - Automation recording in touch, latch and write during playback.
  - Package schema 4 with a migration from 3.
- **Compiler:** engine strips with inserts, sends and outputs, VCA folding, aux input buses, freeze files, and
  automation targets and events (curve sampling).
- **Server:**
  - Implement every `Pending.Phase4` stub.
  - Jobs (queue, progress, cancel) driving `render`, with the bounce post-processing above (SRC, true peak, R128,
    limiter, dither, AIFF writer) and freeze.
  - Patches; I/O labels; insert states on save and open; the plugin editor relay; `parameterChanged` applied as a
    coalesced `setInsertParameter`; `pluginProblem` relayed.
- **`FakeHost`:**
  - Accepts the new messages.
  - Renders a test signal for `render` (a sine per track, so post-processing is testable).
  - Reports progress.
- **Tests:**
  - Properties for every command.
  - Loudness (EBU R128 test vectors), true peak, dither and SRC tests.
  - Package migration.
  - API and host-link tests.

### C — Editor (`editor/`; built with `editor/DESIGN.md`)

- **Mixer like Logic's:**
  - Insert slots (plugin picker by category, bypass, drag to reorder, open the editor: native window on desktop,
    generic editor otherwise).
  - Sends (bus picker, level knob, pre/post), output picker with I/O labels, group slot, VCA assignment.
  - Aux and VCA strips; the master strip with inserts.
  - Views (all, tracks, buses and outputs) and filters.
  - Signal-flow highlight of the selected strip.
- **Generic plugin editor** for effects, with readings (compressor gain reduction).
- **Groups:** a Group Settings window with linked properties.
- **Automation:**
  - Show automation (A); per-track lanes of any target; draw, drag and curve points; select and delete ranges; modes
    per track header.
  - Touch, latch and write while playing: faders, knobs and plugin controls send their changes during playback.
- **Freeze:** button on track headers, with job progress.
- **Patches:** save and load in the Library pane.
- **I/O Labels** window.
- **Export:** a Bounce dialog like Logic's (range, mix or stems, format, bit depth, rate, dither, normalise, real-time,
  add to project), with job progress and a finished toast that reveals the file.
- **Fake server:** implements every new command, endpoint, job and event; remove the placeholders.
- **Tests:** stores, automation maths (curves, hit-testing), routing-loop prevention in pickers, and stories with axe
  for the new audio-ui parts (insert slot, send knob, automation lane parts).
- **Golden path:** extend it per the exit criterion as `test.fixme` until integration.

### D — Effects DSP (`Anode.Dsp`, `engine/tests/Anode.Dsp.Tests`)

The T1 suite, behind `IAudioEffect`, in `BuiltinEffects`:
- **EQ:** Channel EQ (8 bands: HPF, LPF, low and high shelf, 4 peaks; analyzer reading).
- **Dynamics:**
  - Compressor: VCA, FET and Opto characters; threshold, ratio, knee, attack, release, makeup, auto gain,
    sidechain-capable with a sidechain filter; gain-reduction reading.
  - Limiter: lookahead, true peak, ceiling; reports latency.
  - Adaptive Limiter.
  - Noise Gate (sidechain-capable).
  - DeEsser.
- **Utility:** Gain (with phase invert and mono).
- **Delays:** Stereo Delay (tempo-sync note values from a host tempo parameter), Tape Delay (wow, flutter, saturation).
- **Reverb:** an algorithmic plate/room/hall (ChromaVerb-style, with size, decay, damping, predelay and mix).
- **Modulation:** Chorus, Flanger, Phaser, Tremolo.
- **Distortion:** Overdrive, Distortion, Bitcrusher.
- **Filter:** AutoFilter.
- **Metering:** Level Meter, Loudness Meter (momentary and short-term LUFS readings), MultiMeter-lite (correlation).

**Requirements:**
- Stable ids `anode.<name>` and categories.
- Parameters smoothed.
- Exact latency reporting.
- Zero allocations in `Process`, `SetParameter` and `Reset`.
- State round-trips.

**Tests:**
- Frequency responses within tolerance.
- Compressor static curve and timing.
- Limiter ceiling (no overs at 4× oversampling).
- Delay times.
- Reverb tail decay (RT60 against setting).
- Zero allocation.
- Golden renders (null residual, as Phase 2).
- CPU per instance at 48 kHz and 64 frames.

### E — Plugin hosting (new `Anode.Plugins` and `Anode.PluginSandbox` projects, `engine/native/an_plug`, Electron main for editor windows)

- **`an_plug` native shim:** a C ABI over the VST3 SDK (MIT, pinned and fetched as `cth_nam` is) and AudioUnits
  (AudioToolbox, macOS), for scan, instantiate, process, parameters, state, latency, bus layout, and editor views.
- **CLAP effects** too: workstream A of Phase 2 hosts CLAP instruments in-process; move CLAP into the sandbox as well,
  or keep it in-host behind the same factory, and document the choice.
- **`anode-plugin` sandbox executable** (NativeAOT):
  - Hosts plugins for one group.
  - Shared-memory planar buffers and the wake primitive and real-time setup from `an_ipcwake` (spike S4; promote it
    from the spike into the product).
  - Watchdog; crash detection; restart.
- **Out-of-process scanner** with a blocklist and a cache in `<DataDirectory>`. It adds VST3 and AU entries (effects
  and instruments, category, sidechain, editor) to the host's plugin catalogue.
- **A factory the engine calls:** `IAudioEffect` and `IInstrument` adapters that marshal to a sandbox, in-cycle with a
  deadline, bypassing on a miss.
- **Plugin editor windows (spike S6):**
  - The sandbox opens the plugin's native view in a top-level window.
  - On macOS, position it as a child of the Electron window (via its native handle, through the host and server) or
    as a floating utility window.
  - Write `docs/spikes/S6-plugin-windows.md` with what works.
- **A test VST3 effect** built in-repo (a gain or filter with two parameters, state and a minimal editor), used by the
  tests and the golden path.
- **Tests:**
  - Scan, load, process in a sandbox bit-exact against in-process, state, latency.
  - Crash isolation: a deliberately crashing plugin is bypassed and the engine keeps playing.
  - Deadline misses.

## Integration

1. Merge in this order: D, A, E, B, then C.
2. Run the two-process tests: routing, automation, freeze and bounce through the real host, with a sandboxed test VST3.
3. Run the Playwright golden path for the beta exit criterion.
4. Fix the seams.

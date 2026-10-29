# 01 — Implementation plan

> A Logic Pro–class digital audio workstation built on the Cathode stack:
> Electron 45 + Angular 22 (zoneless, signals) + Tailwind 4.3 + xUI 3.x front end,
> .NET 10 back end (NativeAOT real-time host + ASP.NET Core server), servable to any browser on the local network.
>
> Naming: `Anode.*` for .NET projects, `anode-*` for executables, `@anode/*` for TS libs and `an` as the Angular component prefix. Licence: GPL-3.0-or-later.
>
> Feature research source: *Logic Pro User Guide for Mac* (Apple, 028-00847, 1,324 pp., covers Logic Pro 12.3). Instruments and effects are documented in Apple's separate *Logic Pro Instruments* / *Effects* guides; the inventory in §11 uses general knowledge of those.

---

## 0. Executive summary

Logic Pro is about 30 years of product work. Matching it feature for feature is a multi-year, multi-team effort. This plan therefore:

1. **Reuses Cathode's architecture wholesale** where it fits: the two-process GC isolation, the C audio shim, the server-owned state, REST plus WebSocket with committed OpenAPI, LAN serving with token pairing, the Electron sidecar supervision, the build and packaging pipeline and the CI layout.
2. **Calls out where a DAW must differ from Cathode.** A DAW adds a timeline, sequencing, disk streaming, recording, sample-accurate automation, plugin delay compensation, third-party plugin hosting, large binary data (MIDI, peaks) and delta-based UI sync.
3. **Sequences the work into 12 phases.** Each phase has an exit criterion. A usable multitrack recorder/sequencer ships at the end of Phase 3, and everything after that is additive.
4. **Maps every Logic feature area to a phase and a priority** (§11), so scope decisions are explicit rather than accidental.

---

## 1. Goals and non-goals

### Goals
- Desktop DAW for macOS (arm64), Windows (x64) and Linux (x64), with the same platform matrix as Cathode.
- The full editing UI is also served by the .NET server to browsers and tablets on the LAN, using the same Angular build. It works as a "Logic Remote" equivalent and as a second-screen editor or mixer.
- A headless Linux server mode (systemd), as in Cathode, for studio racks and Raspberry Pi–class boxes. This mode is for playback and recording only, with plugins limited to CLAP and VST3.
- Professional real-time performance:
  - ≤ 3 ms round-trip at 64 frames / 48 kHz on CoreAudio.
  - 128 audio tracks streaming from disk.
  - 200+ plugin instances.
  - Zero allocations on the audio thread.
- Third-party plugin hosting: CLAP and VST3 on all platforms, and AU (v2/v3) on macOS.

### Non-goals (explicitly out)
- Apple-proprietary content and brands: Apple Loops content, Drummer and Session Player sample libraries, Alchemy presets, Dolby-licensed renderer code, Apple Intelligence or Continuity integration, iCloud, the Music app and AirDrop sharing. Each gets an open equivalent or is dropped (see §11).
- iPad/iOS native app. The LAN web UI covers this.
- Byte-for-byte compatibility with the Logic project format. We provide import and export of **AAF**, **MusicXML**, **Standard MIDI File**, **ADM BWF** and **stem bounces** instead.

---

## 2. Stack and conventions (inherited from Cathode)

| Concern | Cathode today | Anode decision |
|---|---|---|
| .NET | net10.0, SDK 10.0.401 via `global.json`, `.slnx`, central package management, `TreatWarningsAsErrors`, `IsAotCompatible`, Skala lint | **Same.** Copy `Directory.Build.props/targets`, `Directory.Packages.props`, `.editorconfig`, `skala.jsonc`, `dotnet-tools.json`. |
| Process model | `cathode-host` (NativeAOT, GC-free RT engine) plus `cathode-server` (ASP.NET Core, owns domain); UDS with 4-byte LE-length JSON frames; mmap seqlock telemetry | **Same core**, plus a third process type: `anode-plugin` sandbox (§3.4). |
| Native | `engine/native/cth_audio` C shim (CoreAudio, WASAPI, ALSA, JACK, CoreMIDI, WinMM, ALSA seq); `[LibraryImport]` and `[UnmanagedCallersOnly]` callbacks | **Reuse `cth_audio`** by extracting it to a shared repo or package (§10). Add `an_plug` (VST3/AU bridge), `an_stretch` (time stretch), `an_codec` (FLAC, MP3, AAC decode) and `an_onnx` (ONNX Runtime). |
| API | Minimal APIs under `/api/v1`, `TypedResults`, ProblemDetails, ETag/If-Match revision, STJ source-gen, committed `openapi.json` with a drift test | **Same.** Add a command endpoint and delta events (§4). |
| Realtime | Raw WebSocket `/ws` with 3 prioritised outboxes and JSON frames | **Same hub**, plus **binary frames** (meters, playhead, peaks). Cathode already planned these. |
| Persistence | `JsonFileStore`, atomic temp-then-rename, migrations, no DB | **Same** for library and settings. Projects are a **package directory** (§5.6). |
| Frontend | Angular ~22.2 standalone, zoneless, OnPush, signal stores, hash routing, `app://` scheme | **Same.** |
| Styling | Tailwind 4.3 CSS-first; `@import '@xui/core/styles/theme.css'`; semantic tokens only | **Same.** Add DAW-specific tokens (track colours, region colours, velocity ramp) as an extra CSS layer. |
| UI kit | `@xui/*` ^3 from npm; Cathode's own `libs/audio-ui` (knob, meter, scope…) | `@xui/*` plus an extracted **`@xui/knob` and `@xui/meter`** (upstream Cathode's), plus new **`@anode/timeline`** canvas engine (§6.3). |
| API client | `openapi-typescript` → `libs/api-client/src/generated/schema.ts`, `openapi-fetch`, `check-generated` CI | **Same.** |
| Electron | 45, esbuild main/preload, sandboxed renderer, `contextBridge`, validated IPC, sidecar supervisor with stdin lifeline, random loopback port, per-run token | **Same.** Add native plugin-editor windows (§3.4) and file-dialog IPC. |
| Tests | xUnit v3 + CsCheck, golden WAVs, zero-alloc tests, soak; Vitest 4, Storybook 10 + axe + Chromatic, Playwright `_electron` e2e on null audio | **Same**, plus render-determinism tests for whole projects (§9). |
| Packaging | electron-builder 26 programmatic; NativeAOT host plus self-contained single-file server; headless tarball and systemd | **Same.** |
| Workspace | `engine/` (.NET), `editor/` (Nx + pnpm), `website/`, `packaging/`, `docs/` + ADRs | **Same layout.** |

### Repository layout

```
anode/
├─ engine/
│  ├─ Anode.slnx  global.json  Directory.*.props/targets  nuget.config
│  ├─ native/                     # CMake: cth_audio (shared), an_plug, an_stretch, an_codec, an_onnx
│  ├─ src/
│  │  ├─ Anode.Dsp/               # DSP primitives: filters, oscillators, envelopes, FFT, SIMD helpers
│  │  ├─ Anode.Dsp.Plugins/       # built-in instruments and effects (§7)
│  │  ├─ Anode.Engine/            # RT graph, scheduler, transport, PDC, automation playback, disk streamer, recorder
│  │  ├─ Anode.Audio/             # P/Invoke to cth_audio, device and MIDI I/O
│  │  ├─ Anode.Plugins.Hosting/   # CLAP (pure C ABI from C#), VST3/AU via an_plug, sandbox protocol
│  │  ├─ Anode.Contracts/         # DTOs, STJ source-gen context, blittable IPC structs
│  │  ├─ Anode.Ipc/               # framing, shared-memory segments, seqlock regions
│  │  ├─ Anode.Model/             # domain model: project, tracks, regions, tempo map, commands, undo
│  │  ├─ Anode.Analysis/          # peaks, transient/pitch/tempo detection, chord ID, loudness, stem split
│  │  ├─ Anode.Notation/          # MIDI→notation quantisation, MusicXML import/export
│  │  ├─ Anode.Interchange/       # SMF, AAF, MusicXML, ADM BWF, stems
│  │  ├─ Anode.EngineHost/        # host composition root
│  │  ├─ Anode.Host/              # exe anode-host (NativeAOT)
│  │  ├─ Anode.PluginSandbox/     # exe anode-plugin (NativeAOT)
│  │  └─ Anode.Server/            # exe anode-server (ASP.NET Core)
│  ├─ tests/   tools/ (Bench, Render, Soak)   spikes/
├─ editor/                        # Nx + pnpm
│  ├─ apps/editor/                # Angular app (desktop and LAN)
│  ├─ apps/desktop/               # Electron main and preload
│  ├─ libs/api-client/            # generated OpenAPI client
│  ├─ libs/audio-ui/              # knob, fader, meter, pan, scope, tuner (upstream to xUI)
│  ├─ libs/timeline/              # canvas/WebGL2 timeline engine (arrange, piano roll, automation, waveform)
│  └─ libs/notation/              # score renderer wrapper (Verovio WASM in a worker)
├─ packaging/headless/   docs/ (01..NN + adr/)   website/
```

---

## 3. Runtime architecture

```
 ┌──────────── Electron (desktop) ─────────────┐        ┌── LAN browser / tablet ──┐
 │ main: supervisor, menus, dialogs, plugin-   │        │ same Angular build served │
 │       editor window broker, app:// + CSP    │        │ by anode-server, token    │
 │ renderer: Angular editor (sandboxed)        │        │ paired via QR             │
 └───────────┬─────────────────────────────────┘        └────────────┬──────────────┘
             │ REST /api/v1 + WS /ws (JSON + binary)                 │
             ▼                                                       ▼
 ┌──────────────────────────── anode-server (ASP.NET Core) ─────────────────────────┐
 │ Project model + command/undo · library/media · analysis jobs · compiler          │
 │ (project → PlaybackModel + RenderGraph) · EventHub · MIDI router (non-RT)        │
 └───────────┬───────────────────────────────────────────────────────┬──────────────┘
   UDS JSON control frames + shared-memory bulk segments      jobs (bounce, stem split…)
             ▼                                                       ▼
 ┌────────────── anode-host (NativeAOT, RT) ───────────────┐   offline render = same
 │ device callback (cth_audio) → graph scheduler (N worker │   engine in-process,
 │ threads) → tracks/buses/master · transport · automation │   faster than real time
 │ · disk streamer (prefetch thread) · recorder (writer    │
 │ thread) · MIDI in/out · telemetry seqlock region        │
 └────────┬─────────────────────────────────────────────────┘
          │ shared-memory audio buffers + futex/semaphore wake (same cycle)
          ▼
 ┌── anode-plugin (one per sandbox group) ──┐
 │ VST3 / AU / CLAP instances + native      │
 │ editor windows                           │
 └──────────────────────────────────────────┘
```

### 3.1 ADRs to write in Phase 0 (mirroring Cathode's 0001–0004)
- **ADR-0001 Process model.** Keep host/server separation. The server owns the domain, and the host runs compiled graphs plus compiled playback data.
- **ADR-0002 Audio I/O shim.** Reuse `cth_audio`. Add multichannel I/O beyond stereo, per-device channel maps, aggregate devices on macOS, and MIDI clock/MTC out.
- **ADR-0003 Plugin hosting and sandboxing** (§3.4).
- **ADR-0004 State ownership and sync.** Server-authoritative. Clients receive **deltas**, not a refetch (this departs from Cathode, §4.3).
- **ADR-0005 Time base.** Ticks (960 PPQ) for musical positions, samples for absolute positions, and a tempo map for conversion (§5.1).
- **ADR-0006 Bulk IPC.** Shared-memory blittable arrays for playback data, peaks and recording; JSON stays for control.
- **ADR-0007 UI rendering.** A canvas/WebGL2 timeline engine; the DOM is reserved for chrome.
- **ADR-0008 Third-party DSP and licensing** (§10.2).

### 3.2 Real-time engine (`Anode.Engine`, `anode-host`)
- **Graph.** A DAG of nodes: track input → MIDI FX → instrument → inserts → sends → bus/aux → VCA-free summing → output → master.
  - The server compiles the project into an immutable `RenderGraph`. The host swaps graphs atomically (RCU), cross-fading where topology changes mid-play.
  - Parameter changes go through lock-free SPSC queues, as in Cathode's `setParams`.
- **Multicore scheduling.** A topologically sorted job list. Worker threads are pinned with RT priority (macOS `os_workgroup` joined to the device's IOProc workgroup, Windows MMCSS "Pro Audio", Linux SCHED_FIFO). Work is shared through a lock-free atomic counter per level.
- **Plugin delay compensation (PDC).** Compute per-path latency at compile time and insert delay lines on parallel paths. Support the Logic modes "All", "Audio and Software Instrument tracks" and "Off". The **low-latency mode** bypasses high-latency plugins on the record-armed path.
- **Transport.**
  - Play, stop, record, cycle (loop), skip cycle, Autopunch, replace, pause, chase of MIDI events (notes, CC, program changes).
  - Count-in and pre-roll, metronome (audible and MIDI), and varispeed.
  - **Sample-accurate** scheduling: events carry `samplePos` relative to the buffer.
- **Disk streaming.** One prefetch thread per disk.
  - Region playlist → per-track ring buffers holding about 2 s ahead. Cache hits come from a shared block cache.
  - On seek or cycle, the streamer pre-reads the cycle start. File formats: WAV/BWF, AIFF/CAF, RF64/W64, FLAC; compressed formats are decoded into a cache file.
- **Recording.** The input callback copies into lock-free ring buffers, and a writer thread writes CAF/RF64 (avoids the 4 GB limit). Supports:
  - Take folders and auto-comping.
  - Punch in/out.
  - **Flashback Capture**, built on a continuous circular buffer of the armed inputs, plus MIDI "capture last take" from a MIDI history buffer.
  - Free-tempo recording, analysed later by Smart Tempo.
- **Automation.**
  - Breakpoint curves with curve shapes, rendered to per-buffer ramps (sub-block interpolation every 32 samples).
  - Modes: Read, Touch, Latch, Write, Trim and Relative.
  - Writing goes from the host back to the server as a coalesced event stream, and the server owns the curve.
- **Freeze.** Render the track pre-fader to `Freeze Files/` and swap the subgraph for a file player.
- **Offline render (bounce).** The same engine runs in-process in the server's job runner (Cathode already has an in-process engine mode) with a null device and a faster-than-real-time pull. It is deterministic, which enables golden tests.
- **GC rules.** Copy Cathode's: NativeAOT host, workstation non-concurrent GC, large gen0, preallocated pools, and CI tests that assert zero allocations in the callback.

### 3.3 Server (`anode-server`)
- The domain model lives in `Anode.Model`. It is pure C#, deterministic and unit-testable.
- **Command bus and undo.**
  - Every mutation is an `EditCommand` (typed and serialisable) applied to the immutable project snapshot, producing a new snapshot, an inverse command and a delta.
  - Multiple undo stacks, matching Logic: project edits, mixer and plug-in edits (Logic keeps a separate mixer undo), and per-editor history.
  - An undo history window (`/api/v1/projects/{id}/history`).
- **Compiler.** Project snapshot → `PlaybackModel` (per-track sorted event arrays already converted to sample positions, audio playlists, automation ramps) plus `RenderGraph`.
  - Incremental: only dirty tracks recompile.
  - Results are pushed to the host as shared-memory segments, with a JSON frame referencing `{segmentId, offset, length, version}`.
- **Jobs** (Cathode's 202-plus-jobs pattern): peak generation, Smart Tempo or transient analysis, stem split, bounce/export, freeze, Flex Pitch analysis, chord ID and loudness analysis. Progress reaches clients over WS.
- **Media/library.** A sound library index (samples, loops, patches, channel-strip settings, plug-in presets) in a JSON index plus a file tree; SQLite is unnecessary at first. A tagged-loop metadata scheme (§11, Loops).
- **MIDI router (non-RT)** for control surfaces, MIDI learn and MCU/HUI/OSC (§8).

### 3.4 Third-party plugin hosting (new relative to Cathode)
- **CLAP.** A pure C ABI, so it can be hosted directly from C# with function pointers. This is the reference format in tests.
- **VST3.** The C++ SDK sits behind the `an_plug` native shim, which exposes a flat C ABI (instance create, process, params, state, editor attach). The VST3 SDK is MIT-licensed as of 3.8, which simplifies this.
- **AU v2/v3 (macOS).** AudioToolbox goes behind the same `an_plug` C ABI. AUv3 is out-of-process by design.
- **Sandbox modes** (per plugin, with a global default):
  1. *In-host*: lowest overhead, but a crash takes down the engine. Opt-in, for trusted plugins.
  2. *Grouped sandbox* (default): one `anode-plugin` process per vendor or group. Audio is processed **in the same cycle** with shared-memory buffers and futex/semaphore wake, as Bitwig does, so no extra buffer of latency is added.
  3. *Per-instance sandbox*: maximum isolation.
  - A crashed sandbox bypasses its slots, notifies the UI, and lets the user reload.
- **Scanning.** An out-of-process scanner with a blocklist. The scan cache is stored in the library.
- **Plugin editors.** Native views cannot be embedded in the Chromium renderer, so:
  - Electron main asks the sandbox to open a **native top-level window**. The sandbox owns the window and Electron positions it as a child window via the native handle IPC.
  - LAN/browser clients always get the **generic parameter editor** (an xUI-rendered list of knobs and sliders from the plugin's parameter info).
- **State.** Plugin chunks are stored as files in the project package (`Plugin States/<uuid>.bin`) and referenced from JSON.

---

## 4. API and synchronisation

### 4.1 REST (`/api/v1`, minimal APIs; one static `XxxApi.Map(group)` class per area, as in Cathode)
| Area | Examples |
|---|---|
| `SystemApi` | version, capabilities, audio devices, settings |
| `ProjectsApi` | list, create (from template), open, save, save-as, alternatives, backups, close, properties |
| `CommandsApi` | `POST /projects/{id}/commands`: a batch of `EditCommand`s, one undo group, with `If-Match` revision. Also `POST /undo`, `/redo`, `GET /history` |
| `TracksApi`, `RegionsApi`, `NotesApi` | read endpoints (paged and windowed by time range); writes go through commands |
| `MixerApi` | channel strips, sends, groups, I/O labels; high-rate fader and knob drags use the optimistic path (§4.4) |
| `TransportApi` | play, stop, locate, cycle, record arm (also mirrored as low-latency WS commands) |
| `MediaApi` | import, file browser, Project Audio, peaks (binary, `application/octet-stream`), waveform tiles |
| `LibraryApi` | patches, presets, loops, sample browser, search |
| `PluginsApi` | scan, list, instantiate, parameter info, state, editor open (desktop only) |
| `AnalysisApi` | jobs: tempo, transient, pitch, chord, loudness, stems; and `/ws` |
| `ExportApi` | bounce, export tracks, stems, MIDI, MusicXML, AAF, ADM BWF |
| `ControlApi` | control surfaces, MIDI learn mappings, key commands |

All of this keeps Cathode's conventions: `.WithName()` operationIds, committed `openapi.json` with a drift test, ProblemDetails errors, and `openapi-typescript` regeneration checked in CI.

### 4.2 WebSocket `/ws`
- Keeps Cathode's three outboxes (events never dropped, MIDI activity, latest-only frames), plus binary frame kinds:
  - `meters`: Float16 peak and RMS per channel strip, about 30–60 Hz. Covers 300+ strips in under 2 KB.
  - `transport`: `{samplePos, tickPos, hostNanos, rate, state}`. Clients extrapolate the playhead with `performance.now()` and a clock offset estimated from the ping RTT, so the playhead moves smoothly at the display refresh rate without 60 Hz traffic.
  - `analysis`: spectrum, scope, correlation, loudness.
  - `recording-peaks`: live waveform growth while recording.

### 4.3 Delta sync (departure from Cathode)
- Cathode refetches `LiveState` after events. A DAW project can hold hundreds of thousands of notes, so it cannot do that.
- Instead, each command produces a **domain delta**, `{revision, ops:[{op:'upsert'|'remove', entity:'note'|'region'|…, id, data}]}`, broadcast as an event.
- Stores apply deltas to normalised signal maps (`Map<id, signal<T>>`).
- If a client misses a revision (it got Cathode's `resync`), it does a windowed refetch.

### 4.4 Optimistic interaction
Same pattern as Cathode's knob drags, generalised:
- Faders, knobs and pans: local override, one in-flight PATCH per control, about 30 Hz.
- Drags in the timeline (move or resize of regions or notes): purely local preview while dragging, then a single command on drop. On rejection (409 revision), roll back and refetch the window.

---

## 5. Domain model (`Anode.Model`)

### 5.1 Time
- `Tick` is a long at 960 PPQ. `SamplePos` is a long. `TempoMap` is piecewise, with linear or curved tempo ramps. Conversion is cached, giving O(log n) lookups.
- Each region has a time base: **musical** (moves with tempo) or **absolute/SMPTE-locked** (Logic's "lock SMPTE position"). Time and key signature tracks are included.
- Tempo, marker and signature **sets**, as in Logic, so one project can hold alternative maps.

### 5.2 Core entities
`Project` · `Track` (audio, software instrument, Drummer/Session Player, external MIDI, aux, bus, VCA, output, master, folder, track stack [folder/summing], Live Loops lane, pattern) · `ChannelStrip` (input, MIDI FX[], instrument, inserts[], sends[], output, pan/balance/surround panner, volume, mute/solo, group membership) · `Region` (audio, MIDI, pattern, Session Player, folder, take-folder) · `Take`/`Comp` · `AudioFile` (with tempo and transient/pitch analysis caches) · `NoteEvent`/`CC`/`PitchBend`/`Aftertouch`/`MPE per-note expressions`/`ArticulationID` · `AutomationLane` (track vs region automation) · `Marker`/`ArrangementMarker` · `ChordTrackEvent` · `TransposeEvent` · `GrooveTemplate` · `Screenset` (UI layouts, client-side, per project) · `ProjectAlternative` · `LiveLoopsCell`/`Scene` · `ScoreSet`/`StaffStyle`.

### 5.3 Region parameters (non-destructive, as in Logic's Region inspector)
Loop, quantise (plus Q-Swing, Q-Strength, Q-Range, Q-Flam, Q-Velocity, Q-Length), transpose, velocity, dynamics, gate time, delay, Flex on/off, Smart Tempo mode, file tempo, gain, fades, and fade curve.

### 5.4 Track parameters
Transpose, velocity offset, key limit, velocity limit, delay, no-transpose, no-reset, staff style, articulation set, and **groove track** membership (follow the groove).

### 5.5 Commands
About 150 typed `EditCommand`s in families: TrackOps, RegionOps, NoteOps, AutomationOps, MixerOps, GlobalOps (tempo/signature/markers), FlexOps, CompOps, LiveLoopsOps, ScoreOps.

Every command is property-tested (CsCheck): `apply ∘ inverse = identity` and the serialisation round-trips.

### 5.6 Project package on disk
```
MySong.anode/                      # a directory package (a folder on macOS)
  project.json                     # model root (schemaVersion, migrations as in Cathode PresetMigrations)
  tracks/<id>.json                 # one file per track and its regions; small diffs and atomic writes
  midi/<regionId>.anmidi           # compact binary event arrays (fast load, small)
  Audio Files/ Freeze Files/ Bounces/ Samples/ Movie Files/ Plugin States/ Peaks/ Analysis/
  Alternatives/<name>/             # project alternatives (Logic feature)
  Backups/<n>/                     # rolling auto-backups (Logic "Revert to backup")
```
- All writes use Cathode's `AtomicRename`. Autosave is incremental.
- A "consolidate and copy media" option on Save As covers Logic's "copy files into project".

---

## 6. Frontend (`editor/apps/editor`)

### 6.1 Shell and layout
- **Main window regions, matching Logic's layout:**
  - Control bar (transport, LCD, mode buttons, customisable).
  - Toolbar.
  - Library (left).
  - Inspector (region, track, channel strips).
  - Tracks area (centre).
  - Editors (bottom: Audio Track Editor, Piano Roll, Score, Step Sequencer, Smart Controls, Mixer, Session Player).
  - List editors (right: Event, Marker, Tempo, Signature).
  - Note Pads.
  - Loop and file browsers.
- **Built on `@xui/dock-manager`**, whose serialisable layout tree gives Logic's **screensets** for free: numbered layouts recalled with keys 1–9 and locked or unlocked.
  - In Electron, extra windows ("Open Mixer as window", ⌘2, and so on) are popped-out floating panes or second `BrowserWindow`s on the same route.
- `@xui/menubar` provides in-window menus for LAN clients, while Electron uses native menus that forward to the renderer as Cathode's Settings menu already does.
- **Key commands** use `@xui/hotkeys` plus a remappable key-command registry. Logic defines about 1,500 commands, so the registry is a data table with a searchable Key Commands editor (xUI `data-table` + `omnibar`). Ship a Logic-like default set.
- **Routes** (hash location, lazy): `project` (main), `mixer` (full screen), `score`, `remote` (touch control surface for tablets), `library`, `settings`, `projects` (project chooser and templates).

### 6.2 State (Cathode's hand-rolled signal stores)
- **Stores:**
  - `project.store`: normalised maps and revision.
  - `transport.store`: an extrapolated clock signal plus a non-signal `latest` for rAF readers.
  - `selection.store`, `mixer.store`, `editor-view.store` (zoom, scroll and snap per editor), `library.store`, `jobs.store`, `preferences.store` (localStorage), `screensets.store`, `keycommands.store`.
- **Telemetry**, as in Cathode, bypasses change detection: canvas meters, the playhead and recording waveforms are drawn from `EventStream.latest` in rAF.

### 6.3 `@anode/timeline`: canvas rendering engine (main new frontend investment)
xUI has no timeline, piano roll or waveform component, and DOM-per-region does not scale (Logic projects reach 10k+ regions and 100k+ notes). So:
- **Renderer.** WebGL2 (instanced quads for regions and notes, textures for waveform tiles), with a Canvas2D fallback. Text overlay comes from a glyph atlas.
  - Rendering runs in an **OffscreenCanvas worker**, fed with transferable typed arrays from the store.
- **Virtualisation.** Spatial index (interval tree per lane). Only visible tiles render. Level-of-detail switches between peak tiles and min/max columns.
- **Shared kernel.** Coordinate system (tick ↔ px with zoom), grid/snap (bar, beat, division, ticks, frames, samples; "Smart" snap), selection marquee, drag/resize/trim handles, the tool system (pointer, pencil, eraser, text, scissors, glue, solo, mute, zoom, fade, automation select/curve, marquee, Flex, brush), ruler, playhead, cycle area, locators, markers lane.
- **Views built on the kernel:**
  - Tracks area: regions, waveform previews, MIDI miniatures, take folders, automation lanes, Flex markers, track headers in DOM on the left.
  - Audio Track Editor (waveform plus Flex Time and Pitch overlays).
  - Piano Roll (notes, velocity colours, MIDI draw/automation area, Brush tool, scale quantise, chord-split, multiple regions with colour coding).
  - Step Editor (lanes of beams).
  - Audio File Editor (destructive editing, overview).
  - Automation editing.
  - Live Loops grid (cells; DOM is acceptable here, since there are only hundreds of cells).
  - Step Sequencer (DOM grid; at most 64 steps × rows).
- **Accessibility.** A parallel off-screen ARIA tree for the focused lane and regions, plus keyboard navigation of regions and notes. This matches Cathode's axe-in-Storybook bar.
- **Peaks.** The server generates multi-resolution peak files (`Peaks/<fileId>.anpk`, mip levels 32…65536 samples/px, min/max int8 or int16). The client fetches tiles over HTTP binary with an `ETag` cache and stores them in IndexedDB on LAN clients.

### 6.4 Other UI libraries
- `libs/audio-ui`, extracted from Cathode and upstreamed to xUI: knob, vertical fader with dB taper, pan knob or balance, surround panner (2D puck), 3D object panner, peak/RMS/LUFS meters, goniometer, spectrum, tuner, LCD transport display, MIDI keyboard (on-screen "Musical Typing"), and an XY pad.
- `libs/notation`: Verovio (LGPL-3, WASM) in a worker renders the server-generated MEI or MusicXML to SVG. An interaction layer maps SVG elements back to note IDs for editing, and a Part box palette provides symbols. Alternative: VexFlow (MIT) with in-house layout. **Spike in Phase 9.**
- `@xui/node-graph` for the **Environment** (MIDI object patching) and a routing overview of the signal flow.
- `@xui/data-table` (virtualised) for Event List, Marker, Tempo and Signature lists, Project Audio Browser, and Key Commands.
- `@xui/tree` and `@xui/omnibar` for the Library, All Files Browser and Loop Browser.
- `@xui/echarts` for analysis (EQ curves fall back to custom canvas for interactivity, loudness history).

### 6.5 LAN and remote clients
- The UI is the same Angular build. `anode-server` serves it via a copy of Cathode's `EditorHosting.cs`, with the same token pairing, QR code and CORS rules.
- **Capability detection.** In a browser there is no native plugin editor (the generic editor is used instead), no file dialogs (server-side file browser instead), and no local audio. Optional **listen-in**: the server encodes the master bus to Opus and sends it over WebRTC or a WebSocket to the remote client. This is useful for a tablet in the live room. Phase 10.
- **`remote` route** is the Logic Remote equivalent for touch: transport, mixer, Smart Controls, Live Loops grid, key-command pads, a touch keyboard and drum pads (sending MIDI to the server, which routes it to the host), and a Step Sequencer.
- **Discovery.** Cathode has none; add mDNS/DNS-SD advertising (`_anode._tcp`) from the server so remotes can find studios.

---

## 7. Built-in instruments and effects

DSP lives in `Anode.Dsp` and `Anode.Dsp.Plugins`. Everything is pure C# SIMD (`Vector128/256`, `TensorPrimitives`), NativeAOT-compatible and allocation-free. Each plugin declares its parameters for automation and Smart Controls.

Ship in tiers so that every phase has usable sound.

| Tier | Instruments | Effects |
|---|---|---|
| T1 (Phase 2–4) | **Sampler** (multisample, zones/groups, round robin, articulation switching; SFZ import), **Quick Sampler** (one-shot, classic, slice, recorder), **subtractive synth** (≈Retro Synth/ES2-lite), **drum kit sampler** + **Drum Machine Designer** (pad → per-pad channel strips, track stack) | Channel EQ, Compressor (several circuit models), Limiter/Adaptive Limiter, Noise Gate, DeEsser, Gain, Stereo Delay, Tape Delay, algorithmic Reverb (≈ChromaVerb), Chorus, Flanger, Phaser, Tremolo, Overdrive/Distortion/Bitcrusher, AutoFilter, Level/Loudness/Multi meters, Correlation, Tuner, I/O (external hardware insert with latency ping) |
| T2 (Phase 6–7) | Wavetable/granular synth (≈Alchemy-lite, with granular sync), FM synth (≈EFM1/ES-E), drawbar organ + rotary (≈Vintage B3), electric piano model (≈Vintage EP), clav, **Drum Synth**, Studio-instrument style multisample players (content-dependent) | Convolution reverb (≈Space Designer; IR import), Linear Phase EQ, Match EQ, Multipressor, Enveloper, Exciter, SubBass, Spectral Gate, Ringshifter, Delay Designer (multi-tap), Pitch Shifter, **Pitch Correction**, Vocal Transformer, Amp/Bass Amp sim + Pedalboard (reuse Cathode's amp/cab/NAM chain), Rotor Cabinet, Ensemble, Phat FX, **Step FX / Remix FX / Beat Breaker** (slice effects with randomise), vocoder (≈EVOC 20) |
| T3 (Phase 11) | Physical modelling (≈Sculpture), Mellotron-style, sample-based "Sample Alchemy" | **Mastering Assistant** (analysis → EQ/dynamics/loudness target per character), saturation (≈ChromaGlow), surround-aware variants of T1 effects, Binaural post-processing, Down Mixer |

**MIDI FX** (Phase 6): Arpeggiator (the Smart Controls arp too), Chord Trigger, Modifier, Modulator, Note Repeater, Randomizer, Transposer (scale-aware), Velocity Processor, and **Scripter**. Scripter runs JavaScript via embedded QuickJS-ng in the host (`an_script`, [ADR-0009](adr/0009-scripter.md)), on a MIDI-only path with a fixed arena, a strict time budget and a sandbox.

**Cathode reuse:** Cathode's DSP blocks (amp sims, cabinet IRs, NAM via `cth_nam`, modulation and delay blocks) are the basis for the guitar and effects tier. Extract the shared `Rikarin.Audio.Dsp` package (§10).

---

## 8. Hardware, control and sync

- **Audio devices.**
  - Input/output selection, sample rate (44.1–192 kHz) and buffer size.
  - I/O safety buffer, process buffer range, multithreading and record-delay compensation.
  - Cathode's existing latency test and IR capture tooling is reusable. Device settings live in the desktop's `settings.json` (Cathode's pattern) and in server settings for headless mode.
- **MIDI.**
  - Ports and virtual ports.
  - MIDI 2.0 / UMP where the OS supports it: CoreMIDI UMP, Windows MIDI Services.
  - MPE, MIDI learn, MIDI thru to the selected track, and per-track input filtering.
- **Control surfaces.**
  - Mackie Control/MCU and HUI protocols, including scribble strips, motor faders and banking.
  - Generic MIDI controller assignment (Logic's "Controller Assignments": Learn mode, zones, modes).
  - **OSC** server for custom touch surfaces.
- **Sync.**
  - MIDI Clock and MTC (send and receive), LTC (later).
  - **Ableton Link** via the Link SDK. The SDK is GPLv2-or-later, compatible with Anode's GPL-3.0-or-later (§10.2).
  - Video sync through the transport (§11, Video).
- **Logic Remote equivalent:** the LAN `remote` route (§6.5).

---

## 9. Quality strategy (extends Cathode's `03-testing-strategy.md`)

- **Engine.**
  - Zero-alloc callback tests, plus golden WAV renders per DSP block and **per whole demo project** (render determinism: same project gives the same bits at the same buffer size).
  - PDC correctness tests: a null test of parallel paths with latent plugins.
  - Sample-accuracy tests for automation and MIDI.
  - Soak runs nightly on a null device and on the self-hosted macOS audio runner, with 128 tracks and 200 plugins for 8 hours with zero xruns.
- **Model.** CsCheck properties for commands, undo, serialisation, the tempo map (`tick → sample → tick` round trip) and migrations.
- **API.** `WebApplicationFactory` tests, the OpenAPI drift test, and two-process tests (server plus real host), as in Cathode's `TwoProcessTests`.
- **Plugins.** A CLAP reference test plugin built in-repo, the `pluginval` and `clap-validator` suites in CI, and crash-isolation tests (a deliberately crashing plugin).
- **Frontend.**
  - Vitest unit tests for stores and the delta applier.
  - Storybook plus axe for every audio-ui and timeline view.
  - Chromatic visual tests.
  - Playwright `_electron` e2e on the null audio backend: record → edit → mix → bounce → compare the golden.
- **Performance budgets** (`tools/Anode.Bench`):
  - Timeline at 60 fps with 2,000 visible regions or 20,000 visible notes.
  - Project open under 2 s for a 200-track project.
  - Command round-trip under 30 ms on the LAN.
  - Meter frames under 4 KB.
- **CI.** Copy Cathode's workflows (`engine`, `editor`, `desktop`, `release`, `skala`, `soak`, `baselines`, `website`) and add `plugins.yml` (validators).

---

## 10. Shared code and licensing

### 10.1 Extract from Cathode before starting (Phase 0)
| Asset | Destination |
|---|---|
| `engine/native/cth_audio` + `Cathode.Audio` P/Invoke | `Rikarin.Audio.Native` (git submodule or NuGet package with native assets) |
| `libs/audio-ui` knob/meter/scope/tuner | Upstream to xUI as `@xui/knob`, `@xui/meter`, `@xui/fader`, `@xui/scope` (already planned in Cathode docs) |
| `Cathode.Ipc` framing + seqlock telemetry | `Rikarin.Ipc` |
| Sidecar supervisor, `app://` + CSP, LAN pairing (`network.ts`, `EditorHosting.cs`, `Access.cs`) | Copy (small), or shared `@rikarin/electron-sidecar` |
| DSP blocks, NAM | `Rikarin.Audio.Dsp` |
| CI composite actions, packaging scripts | Copy |

### 10.2 Third-party dependencies (decide in ADR-0008)
| Need | Option | Licence note |
|---|---|---|
| Time stretch / pitch shift (Flex, Smart Tempo, Varispeed, loops) | **Signalsmith Stretch** | MIT. Preferred. Rubber Band is GPL or commercial. |
| VST3 | Steinberg VST3 SDK | MIT since 3.8 |
| CLAP | CLAP headers | MIT |
| Stem splitting | HTDemucs/Demucs exported to ONNX + ONNX Runtime | MIT code and weights. Verify the weights' licence at the time of use. |
| Pitch detection (Flex Pitch) | In-house pYIN/YIN plus note segmentation | — |
| Chord ID | Chroma features plus HMM/CRF in-house, or a small ONNX model | — |
| Notation | Verovio | LGPL-3. Dynamic linking via WASM is fine. |
| Audio decode | libFLAC (BSD), minimp3 (CC0), OS decoders for AAC/ALAC (AudioToolbox, Media Foundation), FFmpeg (LGPL build) for video and audio extraction | — |
| Ableton Link | Link SDK | GPLv2-or-later, compatible with Anode's GPL-3.0-or-later. **Decided: use it.** |
| Dolby Atmos | Do **not** use Dolby SDKs. Implement object panning plus **ADM BWF** (ITU-R BS.2076) export and in-house binaural (SOFA HRTF, e.g. SADIE II) | **Decided.** Avoid "Dolby" trademarks in the UI |
| JS for Scripter | QuickJS | MIT |
| mDNS | Platform APIs (dns_sd on macOS, Windows DNS-SD) or `Makaretu.Dns` | — |

---

## 11. Logic Pro feature inventory → phase mapping

Priority: **P0** = MVP-critical, **P1** = expected by pros, **P2** = differentiators or nice to have, **✗** = out of scope or replaced.

### Basics, windows, projects
| Logic feature | Anode | Phase | Pri |
|---|---|---|---|
| Main window with control bar, toolbar, inspector, Library, editors, list editors, Note Pads, browsers | Dock-manager shell | 1→3 | P0 |
| Multiple main windows, separate Mixer/Piano Roll/Score windows (⌘1–⌘8) | Pop-out panes / BrowserWindows | 3 | P1 |
| Tools (left- and ⌘-click tool, ~15 tools) | Timeline tool system | 3 | P0 |
| Undo/redo, undo history, separate mixer undo | Command bus | 1/3 | P0 |
| Project chooser, templates, create/open/save/save-as/close, project info, backups, alternatives, "consolidate" | ProjectsApi + package format | 1/3 | P0 |
| Project settings (sync, metronome, recording, tuning, audio, MIDI, scores, video, assets, Smart Tempo) | Settings UI | 1→ | P0/P1 |
| Sound Library download/manage packs | Library + content packs (own content) | 6 | P1 |
| Logic Remote | LAN `remote` route | 10 | P1 |
| Apple Creator Studio, Apple Intelligence Writing Tools, Continuity camera | — | — | ✗ |
| Note Pads (project and track notes with images) | Rich text via `@xui/rich-text-editor` | 3 | P2 |

### Hardware
| Connect audio and MIDI devices, virtual MIDI, I/O labels | `cth_audio` + I/O Labels window | 1/4 | P0 |

### Tracks
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Track types: audio, software instrument, Drummer/Session Player, external MIDI, aux, bus, output, master, VCA | Model + graph | 1→4 | P0 |
| Track header controls (mute, solo, record, input monitoring, freeze, lock, hide, on/off, track protect) | Track headers | 1/3 | P0 |
| Patches in the Library (channel-strip patches, merge, save) | LibraryApi + patch format | 4 | P1 |
| Track stacks: folder and summing; convert folder → summing | Model + graph | 3/4 | P1 |
| Groove track | Track param + quantise engine | 7 | P2 |
| Articulations / articulation sets | Model + Sampler support | 7 | P1 |
| Bounce in place, selection-based processing, export tracks/regions | Offline engine jobs | 5 | P1 |
| Track alternatives | Model | 5 | P1 |
| Hide tracks, track zoom, track colours/icons | UI | 3 | P1 |

### Recording
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Record audio (mono/stereo/multichannel, multiple tracks) | Recorder | 1 | P0 |
| Record software instruments; merge/replace/take modes; MIDI overdub | Recorder + MIDI | 2 | P0 |
| Metronome (audible, MIDI, count-in, settings) | Engine | 1 | P0 |
| Cycle recording → take folders; Quick Swipe comping; flatten/merge; take colours | Comp model + timeline | 3 | P0 |
| Punch on the fly, Autopunch, replace | Transport | 3 | P1 |
| Flashback Capture | Circular buffers | 3 | P1 |
| Free tempo recording | Smart Tempo | 5 | P2 |
| Step input (keyboard, MIDI) | Piano Roll/Score | 7 | P2 |
| Low Latency Monitoring mode | PDC bypass | 4 | P1 |

### Loops and media
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Apple Loops (tempo/key-following, Loop Browser with keyword buttons, columns, sound-effect view) | **Own tagged-loop format**: BWF `iXML`/`acid` chunk + JSON sidecar (key, tempo, beats, tags, transients); import ACID WAVs; loop browser | 6 | P1 |
| All Files Browser, bookmarks, Project Audio Browser | Media browsers | 3 | P0/P1 |
| Import compressed audio (MP3/AAC/ALAC/FLAC), MIDI files, MusicXML, AAF | `an_codec`, Interchange | 3/9 | P1 |

### Arranging
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Tracks area: select, move, copy, loop, resize, split, join, nudge, shuffle modes, snap/drag modes, marquee | Timeline | 3 | P0 |
| Chord track and Chord ID (analyse audio/MIDI chords), Pitch Source | Analysis + global track | 11 | P2 |
| Fades and crossfades (curves, speed-up/slow-down fades) | Engine + UI | 3 | P0 |
| **Stem Splitter** (vocals, drums, bass, guitar, piano, other) | ONNX job | 8 | P2 |
| Remove silent passages (Strip Silence) | Analysis job | 5 | P1 |
| Folders (packing) | Model | 3 | P1 |
| Groove templates (from region, apply) | Quantise engine | 7 | P1 |
| Region parameters (Region inspector) | Inspector | 3 | P0 |
| Arrangement markers (drag sections, rearrange song) | Global track | 3 | P1 |

### Editors
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Audio Track Editor (non-destructive, Flex view, gain) | Timeline view | 3/5 | P0 |
| Piano Roll (notes, velocity, scale quantise, Brush tool, MIDI Draw, multi-region, split chords, Time Handles, collapse to scale) | Timeline view | 2/3 | P0 |
| Session Player Editor | Generative | 11 | P2 |
| Score Editor | Notation | 9 | P2 |
| Step Editor (lane sets) | Timeline view | 7 | P2 |
| Event List | `data-table` | 7 | P1 |
| Audio File Editor (destructive: normalise, gain, fades, reverse, silence, sample rate convert, loop search, groove extraction) | Timeline + jobs | 7 | P2 |
| MIDI Transform window (presets: humanise, quantise, crescendo, etc.) | Rule engine | 7 | P2 |

### Session Players and Drummer
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Drummer, Bass Player, Keyboard Player (incl. Synth Players), styles/presets, complexity/intensity XY pad, fills, swing, follow chord track/rhythm, regenerate, convert to MIDI/pattern, multi-channel kits | **Rule-based pattern generator** first: an own-authored style library of MIDI phrase banks + variation grammar + humanisation conditioned on chord track and section markers. A **small ONNX sequence model** is a later option. Own sample content for kits. | 11 | P2 |

### Flex Time, Pitch and Varispeed
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Flex Time modes: Monophonic, Slicing, Rhythmic, Polyphonic, Speed (FX), Tempophone (FX); flex markers, transient markers, quantise audio | Signalsmith Stretch + slicer + transient detector (spectral flux) | 5 | P1 |
| Flex Pitch (note segmentation, pitch drift, vibrato, formant, gain per note; pitch correct; Flex Pitch → MIDI) | pYIN + PSOLA/Stretch with formant preservation | 5 | P1 |
| Varispeed (speed only / speed + pitch, MIDI follow) | Global playback rate | 5 | P2 |
| Smart Tempo (adapt project tempo / keep; analysis editor; multitrack; File Tempo; loop detection) | Beat tracker (onset + DP/HMM tempo tracker) | 5 | P1 |
| Beat mapping | Tempo map editing UI | 5 | P2 |

### Mixing
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Channel strips (volume, pan/balance, mute/solo, input format, meters, sends pre/post/post-pan, I/O) | Mixer | 1/4 | P0 |
| Plug-in slots (insert, reorder, bypass, copy, sidechain inputs), plug-in manager | Graph + plugin hosting | 4 | P0 |
| Channel strip settings (.cst-equivalent) | Library | 4 | P1 |
| Mixer groups (properties per group), VCA | Model | 4 | P1 |
| Surround and binaural panning | Panner nodes | 10 | P2 |
| MIDI channel strips | Model | 4 | P2 |
| Mixer views/filters (Single/Tracks/All), customise | UI | 4 | P1 |
| Undo for mixer and plug-in adjustments | Separate undo stack | 4 | P1 |

### Automation
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Track and region automation, modes (Read/Touch/Latch/Write/Trim/Relative), curves, snap, Automation Event List, controller-written automation | Engine + timeline | 4 | P0 |

### Smart Controls
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Layouts, automatic mapping, manual mapping, parameter mapping graphs, hardware assignment, arpeggiator, articulation IDs, compare | Mapping layer over plugin params | 6 | P1 |

### Live Loops and Step Sequencer
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Live Loops grid: cells, scenes, quantised start, record to cells, cell editor, play modes, Remix FX, performance recording to the Tracks area | Engine clip launcher + DOM grid | 6 | P2 |
| Step Sequencer: patterns, rows for pitch/chord degrees, per-step params (velocity, gate, repeats, chance, offset), randomise, play modes, 5/4 and 7/8 lengths, live/step record, Drum Machine Designer integration | Pattern regions | 6 | P1 |

### Global editing
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Global tracks: marker, arrangement, tempo, signature, key, transposition, chord, movie, beat mapping | Timeline | 3/5 | P0/P1 |
| Markers, marker sets, Marker List | Model + list | 3 | P1 |
| Tempo: tempo track, curves, tempo sets, Tempo List, tempo operations, interpreter | Tempo map | 3 | P0 |
| Project volume/loudness | Master | 4 | P1 |

### Score
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Notation display, Part box, edit notes/symbols, lyrics/text, chord grids, staff styles, score sets/parts, layout, print/export PDF, MusicXML | Anode.Notation + Verovio | 9 | P2 |

### Sharing and export
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Bounce (WAV/AIFF/CAF/FLAC/MP3/AAC, dither, normalise, realtime or offline, cycle range, sample rate/bit depth, loudness normalise) | Export jobs | 4 | P0 |
| Export tracks, stems, MIDI, AAF, MusicXML, GarageBand/iPad project | Interchange (no GarageBand/iPad) | 9 | P1 |
| Music app, AirDrop, Mail, Voice Memos | OS share sheet via Electron where available | — | ✗/P2 |

### Surround and Spatial Audio
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Surround formats (quad through 7.1.4), Surround Panner, 3D Object Panner, beds + objects, binaural monitoring, multichannel effects, ADM BWF export, re-render; "Dolby Atmos" plug-in equivalent | Object renderer + ADM BWF + binaural | 10 | P2 |

### Video and sync
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Movie track/window, thumbnails, import/export audio to movie, scene markers, sync to video hit point | Electron `<video>` with transport-locked seeking (frame-stepping via `requestVideoFrameCallback`), FFmpeg for audio extraction and remux | 10 | P2 |
| MIDI Clock, MTC, Ableton Link | §8 | 8 | P1/P2 |

### Settings, key commands, surfaces
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Settings panes (General, Audio, MIDI, Display, Score, Movie, Automation, Control Surfaces, My Info) | Settings route | 1→ | P0 |
| Key Commands editor, pointer shortcuts, presets | Registry | 3 | P0 |
| Touch Bar | — | — | ✗ (deprecated hardware) |
| Control surfaces (MCU/HUI/EuCon-like), controller assignments, MIDI learn | §8 | 8 | P1 |

### Environment
| Feature | Anode | Phase | Pri |
|---|---|---|---|
| Environment layers, objects (instruments, faders, transformers, delay lines, arpeggiator, chord memorisers, cables, monitors, keyboards), MIDI signal path | `@xui/node-graph` editor + MIDI object graph in the host | 11 | P2 |

---

## 12. Phased roadmap

Durations assume about 3–4 senior engineers (1–2 engine/DSP, 1–2 frontend) and are indicative only.

### Phase 0: Foundations and spikes (6 weeks)
- Create the repo from the Cathode skeleton: copy build props, CI, lint, Nx workspace, Electron supervisor, LAN hosting and the pairing flow. Extract the shared packages (§10.1).
- Write ADRs 0001–0008.
- **Spikes** (go/no-go, each with a measured result written in `docs/`):
  1. Timeline renderer at 60 fps with 20k notes in an OffscreenCanvas/WebGL2 worker inside Electron 45 and Safari/iPadOS.
  2. Disk streaming of 128 × 48 kHz/24-bit tracks with a cycle loop at 64 frames.
  3. Bulk IPC: shared-memory segment swap of a 1M-event playback model in under 10 ms.
  4. Sandboxed CLAP/VST3 in-cycle processing at 64 frames with 50 instances.
  5. Signalsmith Stretch quality and CPU across Flex modes.
  6. Native plugin editor window parented to an Electron window on macOS, Windows and Linux (X11).

**Exit:** all spikes green or alternatives chosen; CI pipeline green on empty projects.

### Phase 1: Engine core and multitrack audio (10 weeks)
- Transport, tempo map (constant plus changes), metronome, audio tracks, disk streaming, recording (mono/stereo), basic mixer (volume, pan, mute, solo, master), project package save/load, command bus with undo, delta WS sync, peaks, Tracks area (move, trim, split) and a basic control bar.

**Exit:** record 16 tracks, edit, play back glitch-free, reopen the project. The e2e golden passes.

### Phase 2: MIDI and instruments (8 weeks)
- Software instrument tracks, MIDI input and recording (merge/replace), Piano Roll v1, quantise, T1 Sampler and synth, CLAP hosting, MIDI output to external instruments.

**Exit:** a song made with MIDI plus audio.

### Phase 3: Arranging and editing depth (10 weeks)
- The full tool set, snap/drag modes, fades and crossfades, take folders and Quick Swipe comping, punch and Autopunch, Flashback Capture, markers and arrangement markers, global tracks (tempo/signature/key), folders and folder stacks, screensets, Key Commands editor, Library/All Files/Project Audio browsers, Region and Track inspectors, Note Pads, project alternatives and backups.

**Exit: MVP / alpha.** A pro can track and edit a session.

### Phase 4: Mixing, automation, export (10 weeks)
- Full channel strips: inserts, sends, aux/bus, outputs, groups, VCA, sidechain, PDC, low-latency mode.
- VST3 and AU hosting with sandboxing, the T1 effects suite, channel-strip settings and patches, the full automation system, Mixer views, freeze, bounce/export with dither and loudness, I/O Labels.

**Exit: beta.** Full production workflow: record → mix → master → export.

### Phase 5: Advanced audio (10 weeks)
- Flex Time (all modes), Flex Pitch, Smart Tempo and its editor, beat mapping, Varispeed, Strip Silence, bounce in place, selection-based processing, track alternatives, tempo curves, and quantise of audio.

### Phase 6: Loops, patterns and performance (8 weeks)
- Tagged-loop format and Loop Browser, content-pack system, Step Sequencer and pattern regions, Drum Machine Designer, Live Loops plus Remix FX, MIDI FX suite (Arpeggiator, Chord Trigger, etc.), Smart Controls, and the T2 instruments/effects.

### Phase 7: Advanced MIDI editing (6 weeks)
- Event List, Step Editor, MIDI Transform, Audio File Editor (destructive), groove templates and the groove track, articulation sets, step input, Scripter (QuickJS).

### Phase 8: Control and sync (6 weeks)
- MCU/HUI, controller assignments and MIDI learn, OSC, MIDI Clock and MTC in/out, Ableton Link (if licensed), and Stem Splitter (an ONNX job; sits here because it reuses the job and ONNX infrastructure built alongside it).

### Phase 9: Notation and interchange (10 weeks)
- Anode.Notation (display quantise, voices, beaming), Verovio integration, Part box editing, lyrics and chords, score sets and parts, PDF print, MusicXML import/export, AAF import/export, SMF.

### Phase 10: Remote, video, surround (10 weeks)
- The `remote` touch route, mDNS discovery, Opus listen-in, video track and sync, surround formats, object panner, binaural renderer, ADM BWF export.

### Phase 11: Generative and analysis (12 weeks)
- Chord track and Chord ID, Session Players (Drummer, Bass, Keys) as a rule-based generator plus own content, Mastering Assistant, the T3 instruments, Environment.

### Phase 12: Hardening and 1.0 (ongoing, 8 weeks)
- Performance passes against the §9 budgets, accessibility audit, documentation site (reuse the Cathode `website/` stack), signing and notarisation, auto-update, crash reporting, migration tests, and the release train (Cathode's `10-releases.md`).

**Rough total:** about 24 months to 1.0 with the team above. The MVP (end of Phase 3) is at about 8 months, and the beta (end of Phase 4) at about 10–11 months.

---

## 13. Top risks and mitigations

| Risk | Mitigation |
|---|---|
| GC or JIT jitter on the audio path | NativeAOT host, Cathode's proven GC configuration, zero-alloc CI tests, and native shims for hot third-party DSP |
| Timeline UI performance in Chromium, especially remote iPad Safari | WebGL2 in a worker, LOD peaks, and the Phase 0 spike with a hard 60 fps budget. Fallback: reduced-detail mode for remotes |
| Plugin crashes and compatibility | Sandboxed by default, validators in CI, blocklist, and the crash-isolation test |
| Native plugin editors in Electron | Phase 0 spike; fallback is free-floating native windows owned by the sandbox |
| Delta sync complexity and multi-client conflicts | Server-authoritative commands with revisions, a property-tested delta applier, and `resync` fallback |
| Scope creep (Logic parity) | §11 priorities are binding; P2 items need an ADR or product sign-off to move earlier |
| Licensing (stretch, Link, Atmos, content) | ADR-0008 before Phase 1. Prefer MIT/BSD, and no Apple or Dolby content or trademarks |
| Content (samples, loops, kits, patches) is a product of its own | Separate content workstream from Phase 4; CC0/own recordings; SFZ import so users bring their own libraries |

---

## 14. Immediate next steps
**Decided (2026-10-27):** codename Anode, repository `~/Projects/Anode`, licence GPL-3.0-or-later (as Cathode), Atmos via
ADM BWF plus in-house binaural, Ableton Link allowed. `cth_audio` is vendored into `engine/native/cth_audio` until the
shared package exists.

1. ~~Confirm the codename and repo location; create the repo from the Cathode skeleton.~~ Done (bootstrap).
2. Extract `cth_audio`, IPC and audio-ui into shared packages (§10.1). Coordinate with the Cathode roadmap, since its knob and meter upstreaming to xUI is already planned.
3. ~~Write ADR-0001 through ADR-0008.~~ Done (proposed ones await the Phase 0 spikes).
4. Run the six Phase 0 spikes in parallel and record the results in `docs/`. S1–S5 done (macOS), see
   [docs/spikes](spikes/README.md); S6 done in Phase 4 (macOS); the Linux/Windows runs remain.
5. ~~Start Phase 1.~~ Phase 1 done on macOS: see [docs/02-phase-1.md](02-phase-1.md#status-2026-10-28). Phase 2 done
   on macOS: see [docs/03-phase-2.md](03-phase-2.md#status-2026-10-28). Phase 3 (alpha) done on macOS: see
   [docs/04-phase-3.md](04-phase-3.md#status-2026-10-28). Phase 4 (beta) done on macOS: see
   [docs/05-phase-4.md](05-phase-4.md#status-2026-10-28). Phase 5 (advanced audio) done on macOS: see
   [docs/06-phase-5.md](06-phase-5.md#status-2026-10-28). Phase 6 (loops, patterns and performance) done on macOS:
   see [docs/07-phase-6.md](07-phase-6.md#status-2026-10-29). Phase 7 (advanced MIDI editing) done on macOS: see
   [docs/08-phase-7.md](08-phase-7.md#status-2026-10-29).

# 02 — Phase 1: engine core and multitrack audio

From [the plan, §12](01-implementation-plan.md#phase-1-engine-core-and-multitrack-audio-10-weeks).

**Exit criterion:** record 16 tracks, edit them, play them back glitch-free, close and reopen the project. The Playwright
e2e golden path passes (null audio).

The contracts below are committed code. Each workstream builds against them and does not change them without a note in
its report; a change to the REST contract means regenerating `engine/src/Anode.Server/openapi.json`
(`ANODE_UPDATE_OPENAPI=1 dotnet test`) and the editor's client (`nx run api-client:generate`).

## Scope

In: audio tracks (mono/stereo), one tempo and one time signature, metronome with count-in, cycle, disk streaming,
recording (one take per armed track per pass, written as WAV), import of WAV files at the project's sample rate, region
move / trim / split / delete / gain / fades, track add / remove / reorder / rename / colour / arm / monitor / input,
channel strips (volume, pan, mute, solo) and the master, project create / open / save / close / recent, undo and redo of
every edit, peaks, delta sync to every client, transport and meters on `/ws`, the Tracks area, control bar and a basic
Mixer in the editor.

Out (later phases): MIDI and instruments, plugins, sends and buses, automation, take folders and comping, tempo changes,
sample-rate conversion on import, compressed formats, Flex, bounce/export.

## Contracts

| What | Where |
|---|---|
| Domain model (project, tracks, regions, files, settings) | `engine/src/Anode.Contracts/Project.cs` |
| Edit commands, batches, undo history | `engine/src/Anode.Contracts/Commands.cs` |
| Deltas | `engine/src/Anode.Contracts/Delta.cs` |
| Transport | `engine/src/Anode.Contracts/Transport.cs` |
| Peaks binary format | `engine/src/Anode.Contracts/Peaks.cs` |
| `/ws` events | `engine/src/Anode.Contracts/Realtime.cs` (mirrored by hand in `editor/libs/api-client/src/lib/models.ts`) |
| REST endpoints (stubbed) | `engine/src/Anode.Server/Api/{ProjectsApi,ProjectApi,TransportApi}.cs` → `openapi.json` → `@anode/api-client` |
| Server ↔ host messages | `engine/src/Anode.Ipc/IpcMessage.cs` |
| Playback model segments | `engine/src/Anode.Ipc/Segments/` (layout, writer, mapped reader; from spike S3, ADR-0006) |
| Telemetry region | `engine/src/Anode.Ipc/Telemetry/TelemetryLayout.cs` |

### Rules the contracts imply

- **Ids.** Domain ids are strings (`trk_…`, `rgn_…`, `aud_…`). The server assigns each track and file a stable `int`
  engine id for IPC and segments; `TrackEntry.MetronomeTrack` (−2) carries the clicks, `-1` is the master strip.
- **Time.** Region positions are ticks (960 PPQ); file offsets, lengths and fades are frames. The server compiles ticks
  to samples with `TempoMap` (ADR-0005) and writes `AudioPlaylistEntry.Start` in samples; a tempo change recompiles every
  track. The metronome's clicks are compiled into the model (`PlaybackEventKind.Click`, `Data1 = 1` on a downbeat) from
  0 to the project end and over the cycle; count-in clicks are rendered by the host before the record position.
- **Revision.** Every applied batch, undo and redo increments `revision` by one and produces exactly one
  `ProjectChangedEvent`. A finished recording is applied as a batch ("Record") and is undoable.
- **Undo.** One stack; consecutive `setStrip` batches on the same control within 1 s coalesce into one step.
- **Save.** `project.json` plus `tracks/<id>.json`, written atomically (temp then rename, as Cathode's `AtomicRename`);
  audio in `Audio Files/`, peaks in `Peaks/`. Recent projects live in `<DataDirectory>/recent.json`.
- **Model swaps.** Whole-model swaps reuse a pool of two segments; edits that touch only some tracks send per-track
  segments (`Replace = false`). The host answers `modelLoaded` and the server then disposes the segment (ADR-0006).
- **Telemetry.** The host writes the region named in its hello about 60 times a second; the server reads it and
  publishes `transport` (30 Hz while moving, once on stop) and `meters` (30 Hz) on `/ws`.
- **Recording.** The host writes 24-bit WAV takes into `LoadModelMessage.RecordDirectory`
  (`<project>/Audio Files`), named `<engine track id>-<UTC timestamp>.wav`, and reports them in `recordingFinished`.

## Workstreams

Three, in parallel, each in its own branch; they meet only at the contracts above.

### A — Engine host (`Anode.Audio`, `Anode.Engine`, `Anode.EngineHost`, `Anode.Host`, `engine/native`)

- An `Anode.Audio` stream session over `cth_audio` (open / start / stop / stats / status), and a null device with a
  real-time-paced clock (spike S2 needed a Mach time-constraint thread).
- Engine: transport (play / stop / record / locate, cycle wrap sample-exact), published model (RCU root over
  `MappedSegment`s, retired on the control thread), disk streamer (spike S2's design: 2 s rings, 96 KiB page-aligned
  reads with `F_NOCACHE` set in the native shim, one disk thread, refill below two thirds), per-track gain / pan / mute /
  solo and master, metronome click synthesis and count-in, input monitoring, recorder (lock-free ring → writer thread →
  WAV), meters, telemetry region writer.
- Host: answers every server → host message in `IpcMessage.cs`; `--serve --socket --device --rate --buffer
  --null-audio --watch-stdin` as the desktop app starts it; exit code 3 when the device is lost or changes.
- Zero allocations on the audio callback (test it, as Cathode's `HostAllocationTests`), no locks on it.
- Tests: transport and cycle arithmetic, playlist rendering against a reference (bit-exact sums of known files),
  recorder writes what it was fed, metronome positions, an offline render of a small model.

### B — Server domain (`Anode.Model`, `Anode.Server`)

- Model: immutable project snapshot, every `EditCommand` with validation and its inverse, the undo stack, deltas,
  serialisation of the package format with a schema version and migrations hook, WAV reading (header, format checks),
  peak generation (multi-level, `PeakFormat`) as a background job on import and after recording.
- Compiler: snapshot → per-track `TrackModel`s (playlists in samples, clicks), incremental by dirty track, engine id map.
- Server: replace every `Pending.Phase1` stub; drive the host (load model on open / edit / reconnect, strips, track IO,
  transport, cycle, metronome); read telemetry and publish `transport` / `meters`; apply `recordingFinished` as an
  undoable batch; ETag / `If-Match` on the project and commands; ProblemDetails for 404 / 409 / 422; in-process engine
  mode keeps working with a fake host for tests.
- Tests: CsCheck properties (apply ∘ inverse = identity for every command, serialisation round trip, delta replay
  reproduces the snapshot), API tests per endpoint, a two-process test that opens a project, imports a file, plays it
  and records on the null device.

### C — Editor (`editor/`)

- Project chooser (new, open recent; desktop: native folder dialog through a new validated `anode:` IPC channel;
  browser: server-side path field) and save / close; unsaved-changes guard on quit (exists in Cathode's main).
- Stores: `project.store` (normalised signal maps of tracks, regions, files; applies `projectChanged` deltas in order;
  refetches on a revision gap or `resync`), `transport.store` (extrapolated playhead from `transport` events), meters
  outside change detection (rAF), `history.store`.
- Tracks area: track headers (DOM, xUI: name, colour, mute, solo, record arm, input monitoring, volume) and the
  `@anode/timeline` renderer (ADR-0007) drawing regions with waveforms from peaks, ruler in bars and beats, playhead,
  cycle area, project end; tools: pointer (select, move, trim edges, fade handles), scissors (split), with snap to the
  grid; delete; zoom and scroll.
- Control bar: transport wired to the API with Space / R / C / Return keys, LCD (bars and beats or time, tempo and
  signature editable), metronome and count-in toggles; ⌘Z / ⇧⌘Z undo and redo; ⌘S save.
- Import: drag WAV files onto a track (desktop: paths through IPC, `ImportAudioFile`; browser: `UploadAudioFile`), then
  `addRegion`.
- Mixer pane: a channel strip per track and the master (fader, pan, mute, solo, meter), using `@anode/audio-ui`.
- Tests: store delta application, timeline hit-testing and snapping, Storybook stories with axe for new audio-ui parts,
  and the Playwright e2e golden path against the real app on null audio.

## Integration (after the workstreams)

Merge A, B, C; run the two-process tests and the Playwright golden path (create project → add tracks → import → record
on the null device → edit → undo/redo → save → reopen → play); fix what the seams reveal.

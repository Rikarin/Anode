# 06 — Phase 5: advanced audio

From [the plan, §12](01-implementation-plan.md#phase-5-advanced-audio-10-weeks) and
[§11, Flex Time, Pitch and Varispeed](01-implementation-plan.md#flex-time-pitch-and-varispeed). Spike S5
([docs/spikes/S5-time-stretch.md](spikes/S5-time-stretch.md)) chose Signalsmith Stretch (`engine/native/an_stretch`).

**Exit criterion.** Audio is as editable in time and pitch as MIDI. The Playwright golden path covers it on the null
device, with WAV fixtures the spec writes itself (a 100 bpm drum loop of synthesized kicks and hats, a humanised version
of it, a melody of sine-like notes each a few cents off, and a phrase with silences between its notes):
1. **Smart Tempo.** In a new project whose tempo mode is *adapt*, import the 100 bpm loop: the project tempo becomes
   100 ± 0.5 bpm. Open the Smart Tempo Editor and move a downbeat.
2. **Follow tempo.** Set the tempo to 120 bpm: the region (Flex & Follow on) follows, and a bounce has its onsets on the
   120 bpm grid within 5 ms.
3. **Flex Time.** Turn Flex on for the track (Polyphonic), drag a flex marker, then switch to Slicing: in a bounce the
   marked onset moved by the drag, within 2 ms.
4. **Quantise audio.** Quantise the humanised loop to 1/16: its onsets land on the grid within 5 ms.
5. **Flex Pitch.** On the melody, set Flex Pitch, correct every note's pitch to 100 %, transpose one note by +2
   semitones, and halve another's vibrato; a bounce, re-analysed, is within 10 cents of the targets. Then **Flex Pitch
   to MIDI** makes a MIDI region with the same notes.
6. **Strip Silence** on the phrase splits it into one region per note.
7. **Tempo curve and beat mapping.** Give a tempo ramp a curved shape; map a beat in the Beat Mapping track onto a
   transient of the loop.
8. **Varispeed.** At 90 %, speed only: a bounce is 1/0.9 as long and its pitch unchanged (within 5 cents); speed and
   pitch: the pitch falls by 1.82 semitones (within 5 cents).
9. **Track alternatives.** Make an alternative of the drum track, delete a region in it, switch back: the region is
   there; switch again: it is not.
10. **Bounce in place** the melody track to a new track (effects included, source muted), and **selection-based
    processing**: apply Gain +6 dB and Channel EQ to half of a region, which becomes a new file and region.
11. Save, close and reopen: everything is intact.

The contracts below are committed code. Each workstream builds against them and changes them only with a note in its
report. A REST change means regenerating `engine/src/Anode.Server/openapi.json`
(`ANODE_UPDATE_OPENAPI=1 dotnet test`) and the editor client (`nx run api-client:generate`).

## Scope

**In:**
- **Flex Time**, all modes:
  - Polyphonic and Monophonic (Signalsmith Stretch; monophonic with a shorter block).
  - Slicing (slices at markers, played at original speed, no stretch; tiny fades).
  - Rhythmic (slices, with each slice's gap filled by looping its tail, crossfaded).
  - Speed (resampled: pitch follows speed).
  - Tempophone (granular).
  - Flex markers (add, drag, delete), transient markers, quantise audio.
- **Flex Pitch:**
  - Notes detected with pYIN and segmented.
  - Per note: pitch, pitch drift, vibrato, formant shift, gain; split and join notes; pitch correction to the scale or
    chromatic grid.
  - Flex Pitch to MIDI.
- **Smart Tempo:**
  - Beat and downbeat tracking, File Tempo, and the project tempo modes (keep, adapt, auto).
  - Flex & Follow per region (off, on, bars, bars and beats).
  - The Smart Tempo Editor: edit beats and downbeats, double or halve the tempo.
  - Adapt the project tempo to a region.
- **Beat mapping:** map ticks to sample positions in a Beat Mapping global track; tempo events follow.
- **Tempo curves:** a ramp's curve shape.
- **Varispeed:** speed only; speed and pitch; speed and pitch including MIDI. 50–150 %, shown as percent, resulting
  tempo or detune.
- **Strip Silence**, **bounce in place** (to a new track or replacing; source left, muted or deleted; with or without
  effects, tails and volume/pan), and **selection-based processing** with the built-in effects.
- **Track alternatives.**
- **Audio region transpose and fine tune**, which need Flex or Follow, as in Logic.

**Out:**
- Selection-based processing with third-party plugins (built-in effects only).
- A PSOLA path for monophonic Flex Pitch (S5 recommends it; Stretch is used for now, and quality is noted).
- Live input through any flex algorithm (the stretcher's latency rules it out, S5).
- Flex on take folders' takes before flattening (Flex works on regions; comps play unflexed).
- Apple Loops metadata import (Phase 6's tagged-loop format).

## Contracts

| What | Where |
|---|---|
| Track `flexOn`, `flexMode`, `alternatives`, `alternativeId`; region `follow`, `flexMarkers`, `transpose`, `fineTune`, `pitchNotes`; file `tempo`; settings `smartTempo`, `varispeed`; tempo event `shape` | `engine/src/Anode.Contracts/Project.cs`, `Arrangement.cs`, `Flex.cs` |
| Flex modes, follow modes, flex markers, pitch notes, file tempo, Smart Tempo and Varispeed settings, track alternatives, beat mappings | `engine/src/Anode.Contracts/Flex.cs` |
| File analysis (transients, beats, pitch curve, notes) | `engine/src/Anode.Contracts/Flex.cs` |
| Jobs `analyze`, `bounceInPlace`, `process`; their requests | `engine/src/Anode.Contracts/Jobs.cs` |
| 16 new commands | `engine/src/Anode.Contracts/Commands.cs` |
| `/ws` `analysis` event | `engine/src/Anode.Contracts/Realtime.cs`, mirrored in `editor/libs/api-client/src/lib/models.ts` |
| REST (stubbed): `/project/files/{id}/analysis` (GET), `/project/files/{id}/analyze` (POST, a job), `/project/bounce-in-place`, `/project/process` | `engine/src/Anode.Server/Api/FlexApi.cs` → `openapi.json` → `@anode/api-client` |
| Segment format 3: playlist entries with warp and pitch ranges and an algorithm; `WarpPoint`, `PitchPoint` arrays | `engine/src/Anode.Ipc/Segments/Layout.cs` |
| IPC: `RenderMessage` with `IncludeVolumePan` and `BypassInserts` (bounce in place) | `engine/src/Anode.Ipc/IpcMessage.cs` |
| Flex renderer interface, source, region view; a reference (linear-interpolation) renderer | `engine/src/Anode.Dsp/Flex/` |
| Analysis entry points (stubs that throw) | `engine/src/Anode.Dsp/Analysis/` |

### Rules the contracts imply

- **Region timing (one helper, `RegionTiming`, in the Model and mirrored in the editor):**
  - A region is *warped* when its track's flex is on, or its `follow` is not `off`, or it has a transpose or pitch
    notes (these need Flex or Follow; the server refuses them otherwise).
  - An unwarped region plays its file 1:1 in time, as before: file frame `fileOffset + n` at the time of `startTick`
    plus n frames.
  - The **base map** of a warped region maps file frames to project positions:
    - `follow: off`: time, as unwarped.
    - `follow: on`: the file's average tempo (`tempo.bpm`) is the project tempo: the file's beat length maps to a
      quarter note, linear in ticks.
    - `follow: bars` / `barsAndBeats`: the file's downbeats (or beats too) land on the project's bars (or beats);
      linear in ticks between them.
  - **Flex markers** move a file frame to a tick (relative to `startTick`). Between neighbouring markers, and between
    the outermost markers and the region's ends, the base map is scaled linearly; the region's start and end stay
    where the base map puts them, as in Logic.
  - A region's end tick is where its last file frame lands; its length in ticks is derived, never stored.
- **Compilation (the server):**
  - Each warped region's playlist entry carries warp points (project sample offset → fractional file frame, at least
    one per marker and beat, and at most every 10 ms where the tempo ramps) and the track's algorithm.
  - Pitch points come from `transpose`, `fineTune` and the pitch notes: per note, cents = (target − detected) plus
    drift and vibrato scaling of the analysed curve around the note's own trend; formant cents and gain likewise.
    They are sampled at the analysis hop (about 5 ms).
  - Fades and gain are in project samples, as before.
  - **Varispeed** is compiled: every position is scaled by 1/speed (MIDI, clicks, automation, regions).
    - Speed only: audio regions are stretched (Polyphonic for unflexed tracks; a track's own algorithm otherwise).
    - Speed and pitch: every audio region is resampled (Speed).
    - With MIDI too: MIDI notes are also transposed by round(12·log2(speed)) semitones.
    - The transport and telemetry report compiled samples; the editor maps them to ticks through the tempo map and
      then multiplies by the speed.
  - **Tempo shape:** a ramp's tempo at fraction x of the way is bpm₀ + (bpm₁ − bpm₀)·x^(2^(−2·shape)), the fade
    formula; `shape: 0` is the linear ramp of Phase 3.
- **Engine (the disk thread renders flex):**
  - All warping and pitch shifting run on the disk thread, in `PlaylistRenderer`, through `IFlexRenderer`. The audio
    callback is unchanged.
  - A fill that starts at an offset primes the renderer (the stretcher's input latency), so a locate costs disk-thread
    time, not a gap.
  - Renders must not depend on where a fill starts beyond 1e-4 after priming (Stretch's own split computation changes
    samples by 6e-7).
  - No allocations on the disk thread after load (test).
- **Flex algorithms (`IFlexRenderer`):**
  - Polyphonic and Monophonic: Stretch at the warp's local rate, the pitch points' transpose, and the formant
    compensated for Pitch mode.
  - Slicing: each slice (between warp points) starts at its warped position and plays at original speed, cut at the
    next slice with a 2 ms fade; gaps are silent.
  - Rhythmic: as slicing, gaps filled by looping the slice's last 30 ms with crossfades.
  - Speed: variable-ratio windowed-sinc resampling.
  - Tempophone: granular, 60 ms grains with Hann crossfades.
  - Pitch (Flex Pitch): Stretch with the per-note transpose curve and formant compensation.
- **Analysis (`Anode.Dsp.Analysis`):**
  - Transients: spectral flux with adaptive threshold, a sensitivity 0–1.
  - Beats: onset envelope → tempo estimate → dynamic-programming beat tracker, plus a downbeat estimate; it reports
    whether the tempo varies.
  - Pitch: pYIN at a 5 ms hop, with voicing; notes by segmenting stable voiced runs.
  - Silence: RMS gate with threshold, minimum time, pre-attack and post-release.
  - Deterministic, and about 50× real time or better for transients and silence on one core.
- **Server:**
  - Analysis is cached in the package as `Analysis/<fileId>.json`; it is rebuilt on demand, not undoable.
  - Commands that need transients or beats (quantise audio, strip silence, adapt tempo, follow) run that analysis
    synchronously; pitch analysis runs as an `analyze` job.
  - Imports are analysed when `smartTempo.analyzeImports` is on.
  - With tempo mode `adapt` (or `auto` in a project with no MIDI regions and a single tempo event), the first audio
    import or recording sets the tempo events from its beats and makes the region play unwarped.
  - **Beat mapping:** each mapping (tick → sample) sets tempo events so that the tick lands on the sample, keeping
    earlier mappings and the positions before them.
  - **Track alternatives:**
    - A track's live regions, MIDI regions and take folders are its active alternative.
    - The others are stored as snapshots in `alternatives`.
    - Selecting one swaps them in one undoable step; ids stay unique project-wide.
  - **Bounce in place:**
    - The host renders the tracks' (or regions') range through `render`: inserts unless bypassed, with or without
      volume and pan, plus a tail.
    - The server adds the files, then in one undo step adds a new track (or replaces the regions) and leaves, mutes or
      deletes the source.
  - **Selection-based processing:**
    - Runs the chain of built-in effects in-process (`Anode.Dsp`) over the regions' audio in the range.
    - Writes a new file and replaces that part with a new region in one undo step.
  - **Package schema 5**, with a migration from 4.

### Contract notes

Where the committed code differs from, or adds to, the shapes planned for this commit:
- `FileAnalysis.pitch` is `float?` per hop: null where unvoiced (JSON has no NaN). `Anode.Dsp.Analysis.PitchTrack`
  keeps NaN.
- `Anode.Dsp` references nothing, so `Anode.Dsp.Flex` mirrors the segment types: `FlexKind` (the values of
  `FlexAlgorithm`, named apart so code using both namespaces needs no alias), `FlexWarpPoint` and `FlexPitchPoint`
  (the layouts of `WarpPoint` and `PitchPoint`; a test checks sizes and values). The engine casts and
  `MemoryMarshal.Cast`s.
- Segment format 3: `AudioPlaylistEntry` is 72 bytes (`Algorithm`, then `Reserved0` byte, `Reserved1` short,
  `Reserved2` int). `TrackModel` gains optional `Warp` and `Pitch` arrays; an entry's `WarpStart`/`PitchStart` index
  its track's own arrays there, and `SegmentWriter` rebases them into the segment's. `TrackView` exposes the
  segment's whole warp and pitch arrays. `MappedSegment.Verify` checks every entry's ranges.
- `RenderMessage`: bounce in place renders as a freeze (`Freeze: true`, several tracks allowed); `IncludeVolumePan`
  and `BypassInserts` apply only then.
- The file routes take `{fileId}`, as `/files/{fileId}/peaks` does. Operation ids: `GetFileAnalysis`, `AnalyzeFile`,
  `BounceInPlace`, `ProcessRegions`.
- `TempoEvent.shape` defaults to 0 when JSON lacks it, but the OpenAPI document (and so the client) lists it as
  required, as it does every property; clients send it.
- The analysis stubs throw `NotImplementedException("Phase 5 workstream D")` through `Pending.WorkstreamD()` (Skala
  SK7090), as the server's stubs do through `Pending.Phase5`. `Downmix.ToMono` (implemented) makes the mono input.
- Schema-4 packages read through an interim `Phase5Fields.Fill` after migration (the defaults above, no schema bump);
  workstream B replaces it with schema 5 and its migration.

## Workstreams

Five, in parallel, each on its own branch; they meet only at the contracts above.

### A — Engine (`Anode.Ipc`, `Anode.Engine`, `Anode.EngineHost`, `Anode.Host`)

- **Segment format 3:** reading and validating warp and pitch ranges; refusing format 2.
- **Flex in `PlaylistRenderer`:**
  - Warped entries render through an `IFlexRenderer` per lane, from `FlexRenderers.Create`.
  - The reference renderer is used until workstream E lands.
  - Unwarped entries take the existing path, bit-exact with Phase 4.
  - Priming at fill starts; crossfades between warped entries.
  - Pooled renderers, so a load allocates and the disk thread does not.
- **Rendering:** `RenderMessage.IncludeVolumePan` and `BypassInserts` for bounce in place; tracks rendered isolated,
  with their tails.
- **Tests:**
  - Unwarped bit-exactness against Phase 4 renders.
  - A warp map's onsets landing where it says (with the reference renderer, then E's).
  - Fill-start independence.
  - Crossfades between warped and unwarped regions.
  - No disk-thread allocations.
  - Cycle loops over warped regions.
  - Disk-thread cost with 64 warped stereo tracks (Polyphonic), against the 2 s read-ahead.

### B — Server domain (`Anode.Model`, `Anode.Server`)

- **Model:**
  - All 16 commands with validation and inverses.
  - `RegionTiming`; region length in ticks everywhere it was derived from frames (move, resize, split, join, copy,
    sections, takes, crossfade checks).
  - Track alternatives; tempo shape in `TempoMap`; beat mapping maths.
  - Quantise audio from transients; Strip Silence; adapt tempo; Flex Pitch to MIDI.
  - Remove `RandomProjects.PendingPhase5` and the placeholder generators.
  - Package schema 5 with a migration from 4.
- **Compiler:** warp and pitch points, algorithms, Varispeed scaling, MIDI transposition, tempo shapes.
- **Server:**
  - Every `Pending.Phase5` stub; the analysis cache and the `analysis` event.
  - Jobs `analyze`, `bounceInPlace`, `process`.
  - Import analysis and the tempo modes.
- **`FakeHost`:** accepts the extended `render`; renders warped regions with the reference renderer.
- **Tests:**
  - Properties for every command.
  - `RegionTiming` against hand-worked maps (follow modes, markers, tempo ramps with shapes).
  - Beat mapping, quantise audio, Strip Silence and alternatives.
  - Package migration; API and host-link tests.

### C — Editor (`editor/`; built with `editor/DESIGN.md`)

- **Flex in the Tracks area:**
  - Show/Hide Flex (⌘F); a flex button and mode menu per track header.
  - Transient markers drawn faintly; flex markers drawn as Logic does.
  - Click to add a marker, drag to move it (with Logic's three upper-half drags: marker, marker plus neighbours,
    transient), double-click to delete.
  - Waveforms drawn warped.
  - Quantise audio in the Region inspector (Q), with strength and swing.
- **Region inspector:** Flex & Follow, transpose, fine tune.
- **Audio Track Editor:**
  - Flex Pitch: note blobs over the pitch curve.
  - Drag a note's pitch; hotspots for drift, vibrato, formant and gain.
  - Split and join notes; "Set to perfect pitch" with a percentage; the scale quantise menu.
  - Flex Pitch to MIDI.
- **Smart Tempo Editor:**
  - The file's beats and downbeats over its waveform.
  - Drag and add beats; double or halve; preview; apply (adapt) or keep.
- **Global tracks:**
  - The Beat Mapping track (drag a beat onto a transient).
  - Tempo curve handles on ramps (drag to shape).
- **Control bar:**
  - The project tempo mode (keep, adapt, auto) in the tempo display's menu, as Logic's.
  - A Varispeed display and control, its mode menu, and the shown unit (percent, tempo, detune).
- **Functions:**
  - Strip Silence window (threshold, minimum time, pre-attack, post-release, with a live preview of the regions).
  - Bounce in Place dialog.
  - Selection-Based Processing window (a chain of built-in effects, preview, apply).
- **Track alternatives** menu in the track header (new, duplicate, rename, delete, select).
- **Fake server:** every new command, endpoint, job and event; analysis from tiny synthetic data; remove the
  placeholders.
- **Tests:**
  - `RegionTiming` parity with the Model (shared vectors).
  - Flex marker hit-testing and drags.
  - Pitch-note maths.
  - The beat-mapping gesture.
  - Stories with axe for the new audio-ui parts (flex marker, pitch note blob, Varispeed display).

### D — Analysis DSP (`Anode.Dsp/Analysis`)

- Transient detection, beat and downbeat tracking with a tempo-variability flag, pYIN pitch with note segmentation,
  silence detection: implement the stub entry points.
- **Test material:**
  - Synthesized: clicks, kick and hat loops at known tempi, with and without humanisation and tempo ramps.
  - Sines and harmonic tones with vibrato and glides at known pitches; noise; silence; a chord for the polyphonic
    flag.
- **Accuracy targets:**
  - Transients within 3 ms.
  - Tempo within 0.5 bpm on steady loops, and the octave error rate reported.
  - Beats within 10 ms.
  - Pitch within 5 cents on voiced frames, with a voicing F1 reported.
  - Note boundaries within 20 ms.
- **Speed:** a table of × real time per analysis, single core.
- Deterministic results (the same input gives bit-identical output).

### E — Flex algorithms (`Anode.Dsp/Flex`, `engine/native/an_stretch`)

- **The seven algorithms behind `IFlexRenderer`, per the rules:**
  - Stretch through the `an_stretch` shim (P/Invoke, the library built by `engine/native/build.sh`), with split
    computation off (the disk thread has time) and formant compensation for Pitch mode.
  - Monophonic as Stretch with a shorter block (measure it).
  - Slicing, Rhythmic, Speed (windowed sinc) and Tempophone in C#.
- **Seeking:** priming for a start at any offset; output independent of the fill start within 1e-4.
- **Tests:**
  - Onset positions against the warp map (≤ 1 ms median, ≤ 3 ms worst).
  - Pitch of transposed tones (Stretch: ≤ 20 ¢ above 200 Hz; Speed: exact ratio).
  - Formant preservation measured on a synthesized vowel.
  - Gaps in Slicing; continuity in Rhythmic.
  - Allocation-free renders.
  - A CPU table per algorithm (frames per second, stereo, one core).
- **Golden renders:** short WAVs of each algorithm on the drum loop and a melody, opt-in, as Phase 4's effect demos.

## Integration

1. Merge in this order: D, E, A, B, then C.
2. Run the two-process tests through the real host: warped playback, Varispeed, bounce in place.
3. Run the Playwright golden path for the exit criterion.
4. Fix the seams.

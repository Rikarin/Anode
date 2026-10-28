# 04 — Phase 3: arranging and editing depth, and the Logic-style UI

From [the plan, §12](01-implementation-plan.md#phase-3-arranging-and-editing-depth-10-weeks), plus a visual redesign.

**Exit criterion: alpha.** A professional can track and edit a session. The Playwright golden path covers these steps
on the null device, in an editor that looks and feels like Logic Pro on a current macOS:
- cycle-record three takes into a take folder;
- comp them with Quick Swipe;
- punch in with autopunch;
- capture a performance with Flashback Capture;
- add a tempo ramp and a signature change;
- lay out arrangement sections and move one;
- crossfade two regions;
- stack tracks in a folder;
- switch screensets;
- rebind a key command;
- import from the All Files browser;
- save an alternative and revert to a backup.

The contracts below are committed code. Each workstream builds against them and changes them only with a note in its
report. A REST change means regenerating `engine/src/Anode.Server/openapi.json`
(`ANODE_UPDATE_OPENAPI=1 dotnet test`) and the editor client (`nx run api-client:generate`).

## Scope

**In:**
- **Global tracks:**
  - Tempo track: steps and linear ramps.
  - Time signature changes on bar lines.
  - Key track.
- **Markers:** markers and the Marker List; arrangement sections with move, copy and delete with content.
- **Takes and comping:**
  - Take folders from cycle recording, or by packing regions.
  - Quick Swipe comping with crossfaded boundaries.
  - Select take, delete take, flatten.
- **Recording modes:**
  - Cycle record mode (take folder or regions) and replace mode.
  - Autopunch with punch locators: records and monitors only inside the range.
  - Flashback Capture of audio and MIDI played while not recording.
- **Regions:**
  - Crossfades: regions may overlap as crossfades, and fades have curves.
  - Region mute, glue (join) and copy.
- **Folder track stacks:** collapse; the stack's volume offsets its members; mute and solo apply to the whole stack.
- **Notes:** project and track Note Pads.
- **Project management:** alternatives and backups.
- **Editor workspace:**
  - Screensets 1–9.
  - Key Commands editor with Logic-like defaults.
  - All Files browser with bookmarks and preview.
  - Project Audio browser.
  - Standard MIDI File import.
- **Editor tools:**
  - The full tool set: pointer, pencil, eraser, text, scissors, glue, solo, mute, zoom, fade, marquee.
  - Snap modes: smart, bar, beat, division, ticks, frames, samples.
  - Drag modes: overlap, no overlap, crossfade, shuffle right, shuffle left.
- **The Logic-style visual redesign** (workstream D).

**Out:**
- Summing stacks and buses (Phase 4 mixing).
- MIDI take folders: MIDI cycle recording merges, or makes a region per pass.
- Flex, Varispeed, beat mapping (Phase 5).
- Region looping.
- Audio join that needs a bounce.

## Contracts

| What | Where |
|---|---|
| Tempo, signature and key events; markers; arrangement sections; recording settings; take folders, takes, comps; screensets | `engine/src/Anode.Contracts/Arrangement.cs` |
| Project: take folders, markers, arrangement, notes; settings: tempo/signature/key tracks, recording; track kind `stack`, `parentId`, `collapsed`, `notes`; audio region fade curves and mute; MIDI region mute | `engine/src/Anode.Contracts/Project.cs`, `Midi.cs` |
| 23 new edit commands; `setTrack`, `setRegion`, `setMidiRegion` extended | `engine/src/Anode.Contracts/Commands.cs` |
| Deltas for take folders, markers, sections, notes | `engine/src/Anode.Contracts/Delta.cs` |
| Alternatives, backups, key commands, bookmarks, file browser, preview | `engine/src/Anode.Contracts/Library.cs` |
| REST (stubbed): `/project/alternatives…`, `/project/backups…`, `/project/screensets…`, `/project/files/import-midi`, `/settings/key-commands`, `/settings/bookmarks`, `/files`, `/files/preview`, `/transport/capture` | `engine/src/Anode.Server/Api/LibraryApi.cs`, `TransportApi.cs` → `openapi.json` → `@anode/api-client` |
| IPC: `setPunch`, `flashbackCapture`, `previewFile`; `RecordedTake.Passes`; `RecordingFinishedMessage.Flashback` | `engine/src/Anode.Ipc/IpcMessage.cs` |
| Segment format 2: playlist entries are 48 bytes with fade curves and may overlap as crossfades | `engine/src/Anode.Ipc/Segments/Layout.cs`, `MappedSegment.cs` |

### Rules the contracts imply

- **Tempo map:**
  - `TempoMap` (ADR-0005) gains ramps: BPM is linear in ticks between a ramp event and the next, and seconds are its
    exact integral.
  - `settings.tempo` and `settings.signature` always equal the first events.
  - Bars and beats follow the signature track.
  - The server recompiles every track on any tempo change, and the clicks follow the signature and tempo tracks.
- **Fade curves:**
  - A curve `c` in −1…1 maps fade position `x` in 0…1 to gain `x^(2^(−2c))` for a fade-in (c = 0 is linear), and the
    mirror for a fade-out.
  - The host evaluates it per sample from the segment entry's `FadeInCurve` / `FadeOutCurve`.
- **Crossfades:**
  - Regions on one track may overlap only where the earlier fades out and the later fades in. The server validates
    this; the host mixes both entries over the overlap, and never more than two at once (`MappedSegment.Verify`
    enforces it).
  - The editor's crossfade drag mode creates crossfades by extending fades over the overlap.
  - Other overlaps keep Phase 1's rule: the later region wins.
- **Take folders:**
  - Ids are `tkf_…` and take ids `tak_…`.
  - The compiler turns the comp into playlist entries, with `crossfade` frames of equal-power-ish crossfade (curve 0 on
    both) centred on each boundary.
  - `moveRegion`, `deleteRegions` and `copyRegions` take folders; `splitRegion` does not.
- **Cycle recording:**
  - The host writes one file per armed track for the whole recording and reports its passes in
    `RecordedTake.Passes`.
  - With `cycleMode = takeFolder` the server makes one take per pass and a comp of the last pass.
  - With `regions` it makes a region per pass.
  - `replace` removes or trims what the recording covers on its track.
- **Autopunch:**
  - The server sends `setPunch` in samples whenever the settings, the tempo or the project change.
  - Recording keeps only the punch range (the host records only inside it and monitors input there); outside it the
    track plays back.
- **Flashback Capture:**
  - The host keeps the last 10 minutes of every armed audio track's input, and of live MIDI on armed or thru tracks,
    while playing.
  - `flashbackCapture` writes what it kept since playback last started.
  - The server applies it as an undoable "Flashback Capture" step: regions where the material was played, with
    cycle passes becoming takes.
- **Stacks:**
  - The server sends members' effective strips: volume dB plus the stack's, mute OR, and solo on a stack solos its
    members.
  - A stack track has no audio of its own and no engine track. Stacks do not nest.
- **Arrangement:**
  - Sections never overlap.
  - `moveSection` inserts at a section boundary (or the end of the last section), shifts later material, and closes
    the gap it leaves on a move. Regions, take folders and markers that start in the section go with it.
- **Alternatives and backups:**
  - An alternative is a full package under `Alternatives/<id>/`; the current one is the package root.
  - Every save first copies the previous `project.json` and track files to `Backups/<timestamp>/`, keeping ten.
- **Screensets, key commands, bookmarks:**
  - Screensets are saved in the project (they mark it unsaved, but are not undo steps).
  - Key commands and bookmarks are per user, in `<DataDirectory>/settings/`.
- **Files and MIDI import:**
  - `/files` lists the server's disk, starting at the user's home.
  - Preview plays through a dedicated engine channel that bypasses the tracks and goes to the master.
  - MIDI import (SMF type 0/1) makes one instrument track per MIDI track with the default synth, plus the file's
    tempo and signature events, in one step.

## Workstreams

A, B and D run in parallel. C (editor features) starts when D has merged, so the features are built on the new design
system rather than restyled afterwards.

### A — Engine host (`Anode.Audio`, `Anode.Engine`, `Anode.EngineHost`, `Anode.Host`, `engine/native`)

- **Segment format 2:**
  - Fade curves.
  - Crossfades: two concurrent playlist entries per track, which means a second stream per track in the disk
    streamer (spike S2's design, per entry); and a sample-exact mix.
- **Cycle recording:** one file per track across wraps, with `Passes`.
- **Autopunch:** record and monitor only inside the range, and play back outside it, sample-exact at both edges.
- **Flashback Capture:**
  - A 10-minute circular buffer per armed audio track, kept in native memory and written to WAV on capture.
  - A MIDI history.
  - Positions mapped through cycle wraps.
- **Preview channel:** stream a file to the master, stoppable.
- **Constraints:**
  - Zero allocations and no locks on the callback in all of the above (test).
  - Callback cost measured with 128 tracks, 32 of them crossfading.

### B — Server domain (`Anode.Model`, `Anode.Server`)

- **Model:**
  - Every new command, with validation and inverses.
  - Remove `RandomProjects.PendingPhase3` and the placeholder generators, and write generators that apply.
  - `TempoMap` with ramps and the signature track.
  - Bar/beat helpers.
  - Package schema 3 with a migration from schema 2.
  - Take folders, markers, sections, stacks and notes in `ToProject` and the deltas.
- **Compiler:** tempo ramps, the signature track's clicks, fade curves, crossfades, comps, mute, and stack strips.
- **Server:**
  - Implement every `Pending.Phase3` stub.
  - Recording: `Passes` → take folders or regions per `cycleMode`; replace mode; autopunch sync; Flashback Capture.
  - Alternatives and backups.
  - Screensets.
  - Key commands and bookmarks.
  - The `/files` listing (format probing for audio).
  - Preview.
  - SMF import.
- **`FakeHost`:** passes, punch, flashback and preview.
- **Tests:**
  - Properties for every command (no skips).
  - `TempoMap` ramp properties (round trip, monotonic, matches numeric integration).
  - Package migration.
  - Compiler tests for each rule.
  - API tests for every endpoint.
  - Host-link tests.

### D — Visual redesign (`editor/`: theme, shell, `libs/audio-ui`, `libs/timeline` rendering)

Make Anode look and feel like Logic Pro on a current macOS. Load the `impeccable` skill first and follow it.
- **Design tokens** (a DAW layer over xUI's theme, in CSS variables, semantic only):
  - graphite surfaces with subtle vertical gradients;
  - hairline separators;
  - an accent blue;
  - Logic's track and region palette;
  - meters in green, yellow and red;
  - an LCD palette;
  - light and dark themes;
  - the system font stack (SF Pro, then system-ui), with tabular numerals for times.
- **Shell:**
  - **Control bar:** grouped transport buttons with gradient bezels, and a glossy LCD showing bars/beats, time, tempo,
    signature and key.
  - **Toolbar and window chrome:** translucent, using backdrop blur.
  - **Panels:** the Library, Inspector, Editors, Mixer and Browsers panes styled like Logic's.
  - **Focus:** focus rings in the accent.
- **Track headers:** a Logic-like look.
- **Regions** (timeline renderer):
  - rounded corners, a gradient body and a darker name bar;
  - waveforms drawn in a lighter tint of the region colour;
  - MIDI notes as small bars;
  - a selected state;
  - a muted (greyed) state.
- **Ruler:** a Logic-like ruler.
- **Cycle area:** yellow.
- **Playhead.**
- **`libs/audio-ui`:**
  - Faders with a metal cap and a scale.
  - Knobs with an arc and a highlight.
  - LED-style segmented peak meters with a peak hold.
  - Channel strips like Logic's Mixer.
  - Every Storybook story updated.
- **Constraints:**
  - Accessible contrast, so axe stays clean.
  - `prefers-reduced-motion` respected.
  - No new runtime dependency without a reason.
  - No change to behaviour or to the stores.
  - Screenshots before and after, in fake mode.

### C — Editor features (`editor/`, after D has merged)

- **Global tracks lane:** marker, arrangement, tempo (drag points, ramps), signature, key.
- **Marker List.**
- **Arrangement sections:** drag to move, ⌥-drag to copy.
- **Tools, snap and drag:** the full tool set, snap modes and drag modes, as listed in Scope.
- **Take folders:**
  - Takes shown in an expanded folder.
  - Quick Swipe comping: swipe on a take to select that range for the comp.
  - Take menu: select, delete, flatten.
  - Pack regions into a folder.
- **Recording controls in the control bar:**
  - Cycle mode.
  - Replace.
  - Autopunch with punch locators on the ruler.
  - A Flashback Capture button (⇧R).
- **Track stacks:** create from the selection (⇧⌘D), collapse, and stack header controls.
- **Note Pads:** project and track notes.
- **Screensets:** keys 1–9; save, rename and lock in a Screenset menu.
- **Key Commands editor:**
  - Searchable.
  - Logic-like defaults as a command registry.
  - Rebinding, saved through `/settings/key-commands`.
- **Browsers:**
  - All Files: bookmarks, preview, and drag to import (audio and MIDI files).
  - Project Audio.
- **Inspectors:**
  - Region fade curves, mute and gain.
  - Track notes and stack membership.
- **Project management:** alternatives and backups in the project menu.
- **Fake server:** implements every new command and endpoint; remove the placeholders.
- **Tests:** stores, tools and drag-mode maths, comp maths, and key command resolution.
- **Golden path:** extend it per the exit criterion as a `test.fixme` spec until integration.

## Integration

1. Merge A, B, D, then C.
2. Run the two-process tests: cycle-record takes, punch and flashback on the null device with the real host.
3. Run the Playwright golden path for the exit criterion.
4. Take screenshots of the new UI.
5. Fix the seams.

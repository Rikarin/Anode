# ADR-0008: Licence and third-party code

- **Status:** accepted
- **Date:** 2026-10-27

## Decision

Anode is **GPL-3.0-or-later**, as Cathode. Third-party code must be GPL-3-compatible:

| Need | Choice | Licence |
|---|---|---|
| Time stretch / pitch shift | Signalsmith Stretch, pinned `88061fd` (1.4.0 + 1), with Signalsmith Linear 0.6.6 ([S5](../spikes/S5-time-stretch.md)) | MIT |
| VST3 | Steinberg VST3 SDK | MIT (3.8+) |
| CLAP | CLAP headers | MIT |
| Ableton Link | Link SDK, pinned `9c90912` (Link 4.1), through its `abl_link` C extension in `an_link`, with the standalone Asio its submodule pins (`8806a68`) | GPL-2.0-or-later (Asio: BSL-1.0) |
| Stem splitting | Demucs 4.0.1 code (export only, `scripts/export-demucs.py`); ONNX Runtime (`Microsoft.ML.OnnxRuntime` 1.31.0) | MIT |
| Stem splitting model | HTDemucs pretrained weights (`htdemucs`, `htdemucs_6s`), exported to ONNX ([ADR-0010](0010-stem-splitter.md)) | **Not MIT: "provided only for scientific purposes"** per the author (demucs issues #327, #508); no licence file covers them. Not published until settled |
| Notation | Verovio 6.4.0 (`verovio` npm, pinned exactly; its `verovio-module.mjs` with the WebAssembly embedded and `verovio.mjs`, copied unmodified as separate files the Score Editor's worker loads, [ADR-0011](0011-notation-rendering.md)) | LGPL-3.0 |
| Decoding | libFLAC, minimp3, OS AAC/ALAC decoders, FFmpeg (LGPL build) | BSD / CC0 / LGPL |
| Movie media (probe, audio extraction, thumbnails, scene detection, remux, the test movie) | FFmpeg 9.0.2 `ffmpeg` and `ffprobe`, LGPL-only builds pinned by SHA-256 in `scripts/ffmpeg.json` (`scripts/fetch-ffmpeg.mjs`): macOS from conda-forge (`ffmpeg 9.0.2 lgpl_*` and the libraries it loads), Windows and Linux from BtbN/FFmpeg-Builds (`lgpl` static, auto-build 2026-10-31); run as separate processes, never linked, shipped unmodified beside the server with each package's licence files ([ADR-0013](0013-video-and-listen-in.md), [docs/media](../media/README.md)) | LGPL-3.0-or-later (`--enable-version3`, no `--enable-gpl` or `--enable-nonfree`; checked by the fetch script and a test). The libraries in the macOS folder: BSD, MIT, Zlib, LGPL, FTL (FreeType), Apache-2.0 |
| Listen-in Opus | Concentus 2.2.2 (NuGet), the managed C# port of libopus; its managed encoder only (no native library) | BSD-3-Clause |
| Scripter | QuickJS-ng, pinned `6d46d07` (0.17.0), in `an_script` ([ADR-0009](0009-scripter.md)) | MIT |
| Script Editor | CodeMirror 6 (`@codemirror/state`, `view`, `commands`, `language`, `lang-javascript`), `@lezer/highlight` | MIT |
| AAF container (Compound File Binary, [MS-CFB]) | Anode's own reader and writer (`Anode.Interchange/Aaf/Cfb`) | GPL-3.0-or-later (ours) |
| AAF object model | The standard class and type definitions, generated from pyaaf2 1.7.1's model tables (`scripts/aaf-model.py` → `AafMetaModel.g.cs`, which carries pyaaf2's copyright and licence notice, "Copyright (c) 2017 Mark Reid", in its header) | MIT (pyaaf2) |
| Listen-in decoding (remote, browsers without WebCodecs Opus) | `opus-decoder` 0.7.12 (npm, pinned exactly; libopus compiled to WebAssembly, embedded), with `@wasm-audio-decoders/common` 9.0.7 and `simple-yenc` 1.0.4 (MIT) and `@eshaz/web-worker` 1.2.2 (Apache-2.0); loaded lazily by the remote only when `AudioDecoder` cannot decode Opus ([ADR-0013](0013-video-and-listen-in.md)) | MIT (libopus inside: BSD-3-Clause) |
| AAF verification | pyaaf2 1.7.1, an independent reader in opt-in tests and the writer of the import fixtures (`scripts/setup-pyaaf2.py`); not shipped | MIT |
| HRTFs (binaural monitoring) | SADIE II Database v2-2 (University of York), subject D2 (KEMAR), 48 kHz HRIRs, converted by `scripts/build-hrtf.py` into `engine/src/Anode.Dsp/Spatial/Hrtf/sadie2-d2-48k.anhrtf` (2 MB, committed with the licence and a `NOTICE` of the source and the changes) | Apache-2.0 (stated on the database's page and in the SOFA file's `License` attribute) |
| Object panning reference | The EBU ADM Renderer's point-source and extent panners (ITU-R BS.2127), ported to C# in `Anode.Dsp/Spatial/PointSourcePanner.cs` and `Vbap.cs` (the algorithm, from BS.2127 and `ear` 2.1.0's `point_source.py` and `extent.py`) | BSD-3-Clause-Clear ("Copyright (c) 2018-2019 EBU ADM Renderer Authors", kept with the licence in the port's header) |
| Session Player styles (`engine/src/Anode.Generative/Styles`) | Anode's own, written for it as data ([ADR-0014](0014-session-players-and-chord-id.md)); nothing transcribed from any product | GPL-3.0-or-later (ours) |
| Drummer kits | Anode's own, rendered by the content builder from Anode's Drum Synth (or its T1 synth) into patches of Anode Essentials | GPL-3.0-or-later (ours) |
| Tape sets (strings, flutes, choir) | Anode's own, rendered by the content builder from Anode's instruments through a tape model; no recordings from outside | GPL-3.0-or-later (ours) |
| Environment's graph editor | `@xui/node-graph` 3.0.1 (npm), bundled in the editor's lazy Environment window ([ADR-0015](0015-environment.md)), as the other `@xui` packages | Apache-2.0 |
| ADM verification | The EBU ADM Renderer (`ear`, pinned commit `5bb17b4`, 2.1.0), validating and rendering Anode's ADM BWF in opt-in tests (`scripts/setup-ear.py`); not shipped | BSD-3-Clause-Clear |

**AAF's compound file:** Anode writes and reads [MS-CFB] itself ([docs/interchange/aaf.md](../interchange/aaf.md)).
OpenMcdf (MPL-2.0, compatible with GPL-3.0) was the alternative. The reader and writer together are about 900 lines.
In-house, the writer streams each large essence stream from its WAV file in one sequential pass, with no temporary copy,
and adds no dependency to an AOT-compiled engine. The reader reads streams in place, sector by sector.

**Spatial audio:** no Dolby SDKs or trademarks. Object panning, ADM BWF (ITU-R BS.2076) export and in-house binaural
rendering from SOFA HRTFs ([ADR-0012](0012-spatial-audio.md), [docs/spatial](../spatial/README.md)). Apache-2.0 data
may be combined with GPL-3.0 code; redistributing it needs its licence, its notices and a statement of the changes,
which `Spatial/Hrtf/LICENSE` and `NOTICE` carry and anything that bundles the file must keep beside it. The
BSD-3-Clause-Clear port carries `ear`'s copyright in its header (the clause grants no patent rights; BS.2127 is an ITU
recommendation implemented by the EBU's reference code).

**Content:** no Apple content (loops, kits, Session Player libraries, presets). Anode ships its own or CC0 content and
imports SFZ and ACID-tagged WAV.

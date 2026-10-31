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
| Notation | Verovio (WASM) | LGPL-3.0 |
| Decoding | libFLAC, minimp3, OS AAC/ALAC decoders, FFmpeg (LGPL build) | BSD / CC0 / LGPL |
| Scripter | QuickJS-ng, pinned `6d46d07` (0.17.0), in `an_script` ([ADR-0009](0009-scripter.md)) | MIT |
| Script Editor | CodeMirror 6 (`@codemirror/state`, `view`, `commands`, `language`, `lang-javascript`), `@lezer/highlight` | MIT |

**Spatial audio:** no Dolby SDKs or trademarks. Object panning, ADM BWF (ITU-R BS.2076) export and in-house binaural
rendering from SOFA HRTFs.

**Content:** no Apple content (loops, kits, Session Player libraries, presets). Anode ships its own or CC0 content and
imports SFZ and ACID-tagged WAV.

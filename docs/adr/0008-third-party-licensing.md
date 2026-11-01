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
| Scripter | QuickJS-ng, pinned `6d46d07` (0.17.0), in `an_script` ([ADR-0009](0009-scripter.md)) | MIT |
| Script Editor | CodeMirror 6 (`@codemirror/state`, `view`, `commands`, `language`, `lang-javascript`), `@lezer/highlight` | MIT |
| AAF container (Compound File Binary, [MS-CFB]) | Anode's own reader and writer (`Anode.Interchange/Aaf/Cfb`) | GPL-3.0-or-later (ours) |
| AAF object model | The standard class and type definitions, generated from pyaaf2 1.7.1's model tables (`scripts/aaf-model.py` → `AafMetaModel.g.cs`, which carries pyaaf2's copyright and licence notice, "Copyright (c) 2017 Mark Reid", in its header) | MIT (pyaaf2) |
| AAF verification | pyaaf2 1.7.1, an independent reader in opt-in tests and the writer of the import fixtures (`scripts/setup-pyaaf2.py`); not shipped | MIT |

**AAF's compound file:** Anode writes and reads [MS-CFB] itself ([docs/interchange/aaf.md](../interchange/aaf.md)).
OpenMcdf (MPL-2.0, compatible with GPL-3.0) was the alternative. The reader and writer together are about 900 lines.
In-house, the writer streams each large essence stream from its WAV file in one sequential pass, with no temporary copy,
and adds no dependency to an AOT-compiled engine. The reader reads streams in place, sector by sector.

**Spatial audio:** no Dolby SDKs or trademarks. Object panning, ADM BWF (ITU-R BS.2076) export and in-house binaural
rendering from SOFA HRTFs.

**Content:** no Apple content (loops, kits, Session Player libraries, presets). Anode ships its own or CC0 content and
imports SFZ and ACID-tagged WAV.

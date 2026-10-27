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
| Ableton Link | Link SDK | GPL-2.0-or-later |
| Stem splitting | Demucs/HTDemucs via ONNX Runtime | MIT (verify weights at use) |
| Notation | Verovio (WASM) | LGPL-3.0 |
| Decoding | libFLAC, minimp3, OS AAC/ALAC decoders, FFmpeg (LGPL build) | BSD / CC0 / LGPL |
| Scripter | QuickJS | MIT |

**Spatial audio:** no Dolby SDKs or trademarks. Object panning, ADM BWF (ITU-R BS.2076) export and in-house binaural
rendering from SOFA HRTFs.

**Content:** no Apple content (loops, kits, Session Player libraries, presets). Anode ships its own or CC0 content and
imports SFZ and ACID-tagged WAV.

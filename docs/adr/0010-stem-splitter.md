# ADR-0010: Stem Splitter runs HTDemucs on ONNX Runtime in the server

- **Status:** accepted
- **Date:** 2026-10-31

## Context

Logic's Stem Splitter separates a recording into vocals, drums, bass and other, or six stems with guitar and piano.
The plan (§10.2) chose HTDemucs exported to ONNX, run by ONNX Runtime, and listed a native shim `an_onnx`. Separation
is an offline job: it never runs on the audio thread.

## Decision

- **Where it runs:** in `anode-server`, as a job, like analysis and the Audio File Editor's operations. No native shim:
  ONNX Runtime comes from the `Microsoft.ML.OnnxRuntime` NuGet package (MIT), which ships its native libraries for
  macOS, Windows and Linux. The server's single-file publish carries them beside it.
- **Model:** HTDemucs, four stems (`htdemucs`) and six (`htdemucs_6s`), exported to ONNX from the official weights by
  `scripts/export-demucs.py`. The STFT and iSTFT are not exported (they export badly); the server computes them in C#
  around the graph.
- **Delivery:** the model files (tens of MB each) are not in the repository. They are release assets with SHA-256
  checksums, fetched by `scripts/fetch-models.mjs` for development and tests, and by the server into the user library on
  first use, with progress on the job.
- **Execution:** the CPU provider by default; CoreML on macOS where it measures faster. Chunks of about 8 s with
  overlap-add, so memory stays bounded for any length.
- **Licence:** the Demucs code is MIT. The weights' licence is checked and recorded here and in ADR-0008 before the
  model is published.

## Consequences

- The server grows by ONNX Runtime's native library (about 20 MB per platform).
- A first split needs the network once, or a model installed from a file.
- The model can be swapped (another separation model with the same inputs and outputs) without code changes beyond
  its description.

# ADR-0010: Stem Splitter runs HTDemucs on ONNX Runtime in the server

- **Status:** accepted (the model's publication is blocked on the weights' licence, below)
- **Date:** 2026-10-31

## Context

Logic's Stem Splitter separates a recording into vocals, drums, bass and other, or six stems with guitar and piano.
The plan (§10.2) chose HTDemucs exported to ONNX, run by ONNX Runtime, and listed a native shim `an_onnx`. Separation
is an offline job: it never runs on the audio thread.

## Decision

- **Where it runs:** in `anode-server`, as a job, like analysis and the Audio File Editor's operations. No native shim:
  ONNX Runtime comes from the `Microsoft.ML.OnnxRuntime` NuGet package (MIT, 1.31.0), which ships its native libraries
  for macOS, Windows and Linux. The server's single-file publish carries them beside it. Only `Anode.Stems` references
  the package, and only the server references `Anode.Stems`: the NativeAOT engine host never loads ONNX Runtime.
- **Model:** HTDemucs, four stems (`htdemucs`: drums, bass, other, vocals) and six (`htdemucs_6s`: adding guitar and
  piano), exported to ONNX from the official weights of `demucs` 4.0.1 by `scripts/export-demucs.py` (torch 2.6.0,
  onnx 1.18.0, opset 17; the versions are pinned in its header). The STFT and iSTFT are not exported (they export
  badly); the graph is `HTDemucs.forward` between them, for one segment of the training length L = 343980 samples
  (7.8 s at 44.1 kHz):
  - inputs `mix` [1, 2, L] and `spec` [1, 4, 2048, 336] (the spectrogram as real channels: left re, left im, right re,
    right im); outputs `spec_out` [1, S, 4, 2048, 336] (the frequency branch, same layout) and `wave_out` [1, S, 2, L]
    (the time branch). A stem is `wave_out + iSTFT(spec_out)`. The normalisations stay in the graph.
  - metadata `anode.stems` (the output order), `anode.sample_rate`, `anode.segment_samples`; the runner reads them, so
    another model with the same inputs and outputs needs no code change.
  - The export checks itself: through ONNX Runtime and demucs' own transforms, the graph matches `model(mix)` to
    6.6·10⁻⁵ (four stems) and 2.4·10⁻⁵ (six) on a signal of RMS 0.04.
- **The transforms and chunking in C#** (`Anode.Stems`), exactly as demucs computes them:
  - STFT: periodic Hann window of 4096, hop 1024, normalised (÷ √4096), the Nyquist bin dropped; the segment
    reflect-padded by 1536 on the left and to whole hops plus 1536 on the right, keeping ⌈L / 1024⌉ frames. iSTFT: the
    inverse, divided by the full window envelope (1.5). As in HTDemucs the pair is not the identity in a segment's first
    and last 1536 samples; inside, it reconstructs to rounding. With these transforms the exported graph matches demucs
    on one segment to 6.5·10⁻⁵.
  - Chunking: demucs' `apply_model(shifts=1, split=True, overlap=0.25)` with its random shift fixed at half the
    maximum (0.25 s of leading silence), so separation is deterministic: segments of L every ⌊0.75 L⌋ samples, a
    shorter last one centred in a window of L filled from the signal before it, each weighted by a triangle (a linear
    cross-fade over each overlap) and divided by the summed weights. Memory beyond the input and output is one segment.
  - Rates other than 44.1 kHz are resampled in and out with the Phase 7 resampler; mono is separated as two equal
    channels and folded back.
- **Delivery:** the model files (174 MB and 115 MB) are not in the repository. They are listed with SHA-256 and size in
  `scripts/models.json`, to be release assets of `Rikarin/Anode` (tag `models-1`; `"published": false` until then),
  fetched by `scripts/fetch-models.mjs` (or copied from a folder with `--from`) into `models/` for development and
  tests, and by the server into the user library on first use (`ModelStore`, which embeds the same manifest), with
  progress on the job.
- **Execution:** the CPU provider. CoreML is an option (`StemExecutionProvider.CoreMl`) but measured slower, so it is
  never chosen by default (below).
- **Test content:** `scripts/build-content.mjs` (`--stems`, and with the test pack) renders a 20 s mix from four known
  stems played by Anode's own instruments (`scripts/build-stem-mix.cs`) into `engine/artifacts/content/stem-mix/`. Tests
  without the real model run a few-hundred-byte fixture model (`scripts/make-stem-fixture.py`, committed under
  `engine/tests/Anode.Stems.Tests/Fixtures/`) with the same inputs, outputs and metadata, which splits by fixed
  frequency bands.

## Measurements

On the test mix (20 s), Apple M1 Max, 10 cores, with other work on the machine; `RealModelTests`:

| Model | Vocals | Drums | Bass | Other | Guitar, piano |
|---|---|---|---|---|---|
| `htdemucs` (SDR, dB) | 12.05 | 14.31 | 11.91 | 5.24 | — |
| `htdemucs_6s` (SDR, dB) | 15.34 | 14.14 | 11.14 | 5.69 | not in the mix |

The brief's thresholds are 6 dB for drums and bass and 3 dB for vocals and other.

| Provider | Load | 20 s of audio | Per minute of audio |
|---|---|---|---|
| CPU (four stems) | 0.3 s | 5.7–7.4 s | 17–22 s |
| CPU (six stems) | 0.3 s | 7.0 s | 21 s |
| CoreML (four stems, ML Program) | 61 s (compiling) | 11.7 s | 35 s |

## Licence

- **Code:** Demucs is MIT ("Demucs is released under the MIT license as found in the LICENSE file", README;
  `LICENSE`: MIT, Copyright (c) Meta Platforms, Inc. and affiliates), github.com/facebookresearch/demucs. ONNX Runtime
  is MIT.
- **Weights: not MIT, and not clearly licensed at all.** The README and LICENSE do not mention the pretrained weights.
  The author and maintainer, Alexandre Défossez, has said in the repository that they are not covered by the MIT
  licence and are for research:
  - facebookresearch/demucs#327 (2022-05-23), asked whether the models may ship in a commercial product: "The model
    weights are not covered by the MIT license, and are provided only for scientific purposes."
  - facebookresearch/demucs#508 (2023-08-02), the same question for `htdemucs` v4: "Weights are provided for research
    purpose!"
  - facebookresearch/demucs#267 (2022-01-04): the MusDB dataset's licence limits the weights, which are "mostly usable
    for research purpose".
  - The training data is MUSDB18-HQ (non-commercial research licence) plus 800 internal songs (README).
  - The author's Hugging Face copy of the weights (huggingface.co/adefossez/HTDemucs) declares no licence in its
    model card as of 2026-10-31; an open, unanswered discussion there (#1) reports that it carried `license: mit`
    earlier and asks whether that was intended. That is not a grant.
- **Consequence:** Anode is GPL-3.0; research-only weights are not GPL-compatible to distribute and cannot be offered to
  users as part of the product. The exported files stay local and **must not be published** as release assets until
  the author or Meta grants a licence in writing, or a separation model with
  an explicit permissive licence is trained or chosen to replace them (the runner takes any model with the same inputs,
  outputs and metadata).

## Consequences

- The server grows by ONNX Runtime's native library (about 20 MB per platform).
- A first split needs the network once, or a model installed from a file.
- The model can be swapped (another separation model with the same inputs and outputs) without code changes beyond
  its description.
- Until the weights' licence is settled, the Stem Splitter works only with a model the user provides, and the golden
  path runs with locally exported models.

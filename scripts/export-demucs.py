#!/usr/bin/env python3
# Exports HTDemucs to ONNX for the Stem Splitter (docs/adr/0010-stem-splitter.md): the four-stem `htdemucs` and the
# six-stem `htdemucs_6s`, from the official `demucs` package's pretrained weights, into models/ (gitignored) as
# htdemucs.onnx and htdemucs_6s.onnx, and their SHA-256 and sizes into scripts/models.json.
#
# The STFT and iSTFT stay outside the graph (Anode.Stems computes them in C#, exactly as HTDemucs._spec/_ispec do).
# The graph is HTDemucs.forward between them, for one segment of the model's training length:
#
#   inputs   mix       float32 [1, 2, L]           the waveform (L = int(segment · 44100): 343980 for both models)
#            spec      float32 [1, 4, 2048, T]     HTDemucs._spec(mix) as real channels (T = ceil(L / 1024)):
#                                                  [left re, left im, right re, right im]
#   outputs  spec_out  float32 [1, S, 4, 2048, T]  the frequency branch per stem, denormalised, same channel layout
#            wave_out  float32 [1, S, 2, L]        the time branch per stem, denormalised
#
# A stem is wave_out + _ispec(spec_out). The metadata carries anode.stems (the output order), anode.sample_rate and
# anode.segment_samples.
#
# Pinned (Python 3.13, macOS arm64 / Linux x86_64):
#   pip install torch==2.6.0 torchaudio==2.6.0 demucs==4.0.1 onnx==1.18.0 onnxruntime==1.22.0
#
# Usage (in a venv; the weights download into $TORCH_HOME on first use):
#   python -I scripts/export-demucs.py [--out models] [--models htdemucs,htdemucs_6s] [--no-verify]
#
# Licence: the Demucs code is MIT, but its author states that the pretrained weights are not covered by the MIT
# licence and are provided for research purposes (docs/adr/0010-stem-splitter.md). Do not publish the exported files
# without settling that.
import argparse
import hashlib
import json
import math
import os
import sys
from pathlib import Path

import numpy as np
import onnx
import torch
from demucs.htdemucs import HTDemucs
from demucs.pretrained import get_model
from demucs.apply import BagOfModels

ROOT = Path(__file__).resolve().parent.parent


class Core(torch.nn.Module):
    """HTDemucs.forward from the normalisation after _spec to the denormalisation before _ispec (demucs 4.0.1)."""

    def __init__(self, model: HTDemucs):
        super().__init__()
        self.m = model

    def forward(self, mix, spec):
        m = self.m
        x = spec
        B, C, Fq, T = x.shape
        mean = x.mean(dim=(1, 2, 3), keepdim=True)
        std = x.std(dim=(1, 2, 3), keepdim=True)
        x = (x - mean) / (1e-5 + std)
        xt = mix
        meant = xt.mean(dim=(1, 2), keepdim=True)
        stdt = xt.std(dim=(1, 2), keepdim=True)
        xt = (xt - meant) / (1e-5 + stdt)

        saved, saved_t, lengths, lengths_t = [], [], [], []
        for idx, encode in enumerate(m.encoder):
            lengths.append(x.shape[-1])
            inject = None
            if idx < len(m.tencoder):
                lengths_t.append(xt.shape[-1])
                tenc = m.tencoder[idx]
                xt = tenc(xt)
                if not tenc.empty:
                    saved_t.append(xt)
                else:
                    inject = xt
            x = encode(x, inject)
            if idx == 0 and m.freq_emb is not None:
                frs = torch.arange(x.shape[-2], device=x.device)
                emb = m.freq_emb(frs).t()[None, :, :, None].expand_as(x)
                x = x + m.freq_emb_scale * emb
            saved.append(x)

        if m.crosstransformer:
            if m.bottom_channels:
                b, c, f, t = x.shape
                x = x.reshape(b, c, f * t)
                x = m.channel_upsampler(x)
                x = x.reshape(b, -1, f, t)
                xt = m.channel_upsampler_t(xt)
            x, xt = m.crosstransformer(x, xt)
            if m.bottom_channels:
                b, c, f, t = x.shape
                x = x.reshape(b, c, f * t)
                x = m.channel_downsampler(x)
                x = x.reshape(b, -1, f, t)
                xt = m.channel_downsampler_t(xt)

        for idx, decode in enumerate(m.decoder):
            skip = saved.pop(-1)
            x, pre = decode(x, skip, lengths.pop(-1))
            offset = m.depth - len(m.tdecoder)
            if idx >= offset:
                tdec = m.tdecoder[idx - offset]
                length_t = lengths_t.pop(-1)
                if tdec.empty:
                    pre = pre[:, :, 0]
                    xt, _ = tdec(pre, None, length_t)
                else:
                    skip = saved_t.pop(-1)
                    xt, _ = tdec(xt, skip, length_t)

        S = len(m.sources)
        x = x.view(B, S, -1, Fq, T)
        x = x * std[:, None] + mean[:, None]
        xt = xt.view(B, S, -1, mix.shape[-1])
        xt = xt * stdt[:, None] + meant[:, None]
        return x, xt


def spec_channels(model: HTDemucs, mix: torch.Tensor) -> torch.Tensor:
    return model._magnitude(model._spec(mix))


def full(model: HTDemucs, core, mix: torch.Tensor) -> torch.Tensor:
    """A separation through `core` (torch or ONNX) with demucs' own STFT, for checking against model(mix)."""
    spec = spec_channels(model, mix)
    x, xt = core(mix, spec)
    zout = model._mask(None, x)
    return xt + model._ispec(zout, mix.shape[-1])


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def export(name: str, out: Path, verify: bool) -> dict:
    loaded = get_model(name)
    model = loaded.models[0] if isinstance(loaded, BagOfModels) else loaded
    assert isinstance(model, HTDemucs), type(model)
    assert len(getattr(loaded, "models", [model])) == 1, "a bag of several models is not supported"
    model.eval()
    assert model.cac and model.nfft == 4096 and model.hop_length == 1024 and model.samplerate == 44100
    length = int(model.segment * model.samplerate)
    frames = math.ceil(length / model.hop_length)
    core = Core(model).eval()
    # The fused attention kernel (aten::_native_multi_head_attention) has no ONNX export; the plain path does.
    torch.backends.mha.set_fastpath_enabled(False)

    torch.manual_seed(0)
    mix = 0.1 * torch.randn(1, 2, length)
    spec = spec_channels(model, mix)
    assert tuple(spec.shape) == (1, 4, 2048, frames), spec.shape

    path = out / f"{name}.onnx"
    with torch.no_grad():
        torch.onnx.export(
            core, (mix, spec), str(path), opset_version=17, do_constant_folding=True,
            input_names=["mix", "spec"], output_names=["spec_out", "wave_out"], dynamo=False,
        )
    graph = onnx.load(str(path))
    for key, value in {
        "anode.stems": ",".join(model.sources),
        "anode.sample_rate": str(model.samplerate),
        "anode.segment_samples": str(length),
        "anode.source": f"demucs 4.0.1 pretrained '{name}'",
    }.items():
        entry = graph.metadata_props.add()
        entry.key, entry.value = key, value
    onnx.save(graph, str(path))
    onnx.checker.check_model(str(path))

    if verify:
        import onnxruntime as ort

        session = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])

        def run(mix_, spec_):
            a, b = session.run(None, {"mix": mix_.numpy(), "spec": spec_.numpy()})
            return torch.from_numpy(a), torch.from_numpy(b)

        with torch.no_grad():
            reference = model(mix)
            through_torch = full(model, core, mix)
            through_onnx = full(model, run, mix)
        print(f"{name}: wrapper vs model max |Δ| {float((through_torch - reference).abs().max()):.3g}, "
              f"onnx vs model max |Δ| {float((through_onnx - reference).abs().max()):.3g} "
              f"(signal rms {float(reference.pow(2).mean().sqrt()):.3g})")
        np.save(out / f"{name}.check-mix.npy", mix.numpy())
        np.save(out / f"{name}.check-out.npy", reference.numpy())

    return {
        "file": path.name, "sha256": sha256(path), "size": path.stat().st_size, "stems": list(model.sources),
        "sampleRate": model.samplerate, "segmentSamples": length,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default=str(ROOT / "models"))
    parser.add_argument("--models", default="htdemucs,htdemucs_6s")
    parser.add_argument("--no-verify", action="store_true")
    args = parser.parse_args()
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    manifest_path = ROOT / "scripts" / "models.json"
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else {"models": []}
    for name in args.models.split(","):
        entry = export(name, out, not args.no_verify)
        print(json.dumps(entry))
        models = [m for m in manifest["models"] if m["file"] != entry["file"]]
        previous = next((m for m in manifest["models"] if m["file"] == entry["file"]), {})
        entry["url"] = previous.get("url", "")
        models.append({**previous, **entry})
        manifest["models"] = sorted(models, key=lambda m: m["file"])
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
# Writes the Stem Splitter's fixture models (docs/adr/0010-stem-splitter.md) into
# engine/tests/Anode.Stems.Tests/Fixtures/: htdemucs-fixture.onnx (four stems) and htdemucs_6s-fixture.onnx (six), a
# few hundred bytes each, with the inputs, outputs and metadata of scripts/export-demucs.py's models, so tests run a
# split without the real model.
#
# What they compute: the frequency branch is the input spectrogram times fixed, complementary band masks (one band of
# STFT bins per stem, built in the graph from bin edges), and the time branch is silence. So every stem is the mix
# band-passed by brick-wall STFT masks, and the stems add up to the mix. The bands (bins of 44100/4096 Hz):
#
#   four stems   drums ≥ 557 (6 kHz up), bass < 23 (below 250 Hz), other 186–557 (2–6 kHz), vocals 23–186
#   six stems    drums ≥ 557, bass < 23, other 372–557 (4–6 kHz), vocals 93–186 (1–2 kHz), guitar 186–372 (2–4 kHz),
#                piano 23–93 (250 Hz–1 kHz)
#
# Pinned: pip install onnx==1.18.0 (Python 3.13). Usage: python -I scripts/make-stem-fixture.py
import math
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "engine" / "tests" / "Anode.Stems.Tests" / "Fixtures"
LENGTH = 343980  # int(7.8 · 44100), HTDemucs' training segment
FRAMES = math.ceil(LENGTH / 1024)
BINS = 2048

MODELS = {
    "htdemucs-fixture.onnx": [("drums", 557, BINS), ("bass", 0, 23), ("other", 186, 557), ("vocals", 23, 186)],
    "htdemucs_6s-fixture.onnx": [
        ("drums", 557, BINS), ("bass", 0, 23), ("other", 372, 557), ("vocals", 93, 186), ("guitar", 186, 372),
        ("piano", 23, 93),
    ],
}


def build(stems):
    count = len(stems)
    lo = np.array([s[1] for s in stems], np.float32).reshape(1, count, 1, 1, 1)
    hi = np.array([s[2] for s in stems], np.float32).reshape(1, count, 1, 1, 1)
    scalar = lambda name, value, dtype=np.float32: numpy_helper.from_array(np.array(value, dtype), name)
    nodes = [
        # bins [1, 1, 1, 2048, 1]
        helper.make_node("Range", ["zero", "bins_count", "one"], ["range"]),
        helper.make_node("Reshape", ["range", "bins_shape"], ["bins"]),
        helper.make_node("GreaterOrEqual", ["bins", "lo"], ["above"]),
        helper.make_node("Less", ["bins", "hi"], ["below"]),
        helper.make_node("And", ["above", "below"], ["inside"]),
        helper.make_node("Cast", ["inside"], ["mask"], to=TensorProto.FLOAT),
        helper.make_node("Unsqueeze", ["spec", "axis1"], ["spec1"]),
        helper.make_node("Mul", ["spec1", "mask"], ["spec_out"]),
        helper.make_node("Unsqueeze", ["mix", "axis1"], ["mix1"]),
        helper.make_node("Expand", ["mix1", "wave_shape"], ["mix_s"]),
        helper.make_node("Mul", ["mix_s", "zero"], ["wave_out"]),
    ]
    initializers = [
        scalar("zero", 0.0), scalar("one", 1.0), scalar("bins_count", float(BINS)),
        numpy_helper.from_array(np.array([1, 1, 1, BINS, 1], np.int64), "bins_shape"),
        numpy_helper.from_array(lo, "lo"), numpy_helper.from_array(hi, "hi"),
        numpy_helper.from_array(np.array([1], np.int64), "axis1"),
        numpy_helper.from_array(np.array([1, count, 2, LENGTH], np.int64), "wave_shape"),
    ]
    graph = helper.make_graph(
        nodes, "stem-fixture",
        [helper.make_tensor_value_info("mix", TensorProto.FLOAT, [1, 2, LENGTH]),
         helper.make_tensor_value_info("spec", TensorProto.FLOAT, [1, 4, BINS, FRAMES])],
        [helper.make_tensor_value_info("spec_out", TensorProto.FLOAT, [1, count, 4, BINS, FRAMES]),
         helper.make_tensor_value_info("wave_out", TensorProto.FLOAT, [1, count, 2, LENGTH])],
        initializers,
    )
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)], producer_name="anode")
    model.ir_version = 8
    for key, value in {
        "anode.stems": ",".join(s[0] for s in stems), "anode.sample_rate": "44100",
        "anode.segment_samples": str(LENGTH), "anode.source": "fixture: fixed band masks",
    }.items():
        entry = model.metadata_props.add()
        entry.key, entry.value = key, value
    onnx.checker.check_model(model)
    return model


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for name, stems in MODELS.items():
        path = OUT / name
        onnx.save(build(stems), str(path))
        print(path, path.stat().st_size, "bytes")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
# Builds Anode's HRTF file (docs/spatial/README.md, "Binaural") from the SADIE II database (University of York,
# Apache-2.0: https://www.york.ac.uk/sadie-project/database.html): subject D2 (KEMAR), the 48 kHz, 256-tap
# HRIRs in SOFA. The committed result is engine/src/Anode.Dsp/Spatial/Hrtf/sadie2-d2-48k.anhrtf; this script
# reproduces it.
#
#   1. Download D2_HRIR_SOFA.zip from Zenodo (record 12092466, SADIE II v2-2) and check its SHA-256.
#   2. Read D2_48K_24bit_256tap_FIR_SOFA.sofa (HDF5; SimpleFreeFieldHRIR: SourcePosition in degrees, azimuth
#      anticlockwise from the front as ADM's, Data.IR [M, 2, N]).
#   3. Keep the directions on a 2-degree azimuth grid on the rings at elevations -75 ... 75 in steps of 15, and the
#      two poles (1982 of the 8802).
#   4. Write them raw (the measured HRIR pairs, ITD included) as 16-bit integers with one scale, plus each ear's delay
#      (the peak of the response low-passed at 1.5 kHz without phase shift, to a fraction of a sample), which the
#      loader uses to align neighbours when it interpolates between directions. (SADIE II's responses are roughly
#      linear-phase, so a threshold onset is thrown by their pre-ringing; the low-passed peak is not.)
#
# Why D2 (KEMAR) rather than D1 (KU100): both are dummy heads (no individual's HRTF); D1's broadband interaural delay at
# 90 degrees is 0.79-0.81 ms, at the edge of the 0.5-0.8 ms the exit criterion asks for, D2's 0.71-0.73 ms.
#
# The .anhrtf format (little-endian):
#   0   "ANHR"
#   4   uint32 version (1)
#   8   uint32 sample rate
#   12  uint32 taps per impulse response
#   16  uint32 direction count
#   20  float32 scale (a sample is int16 * scale)
#   24  uint32 metadata length in bytes (UTF-8 text: source and licence)
#   28  metadata
#   then per direction: float32 azimuth, float32 elevation (degrees, ADM), float32 left delay, float32 right delay
#   then per direction: int16 left[taps], int16 right[taps]
#
# Usage (numpy, scipy and h5py; e.g. a scratch virtual environment):
#   python3 -m venv /tmp/hrtf && /tmp/hrtf/bin/pip install numpy scipy h5py
#   /tmp/hrtf/bin/python -I scripts/build-hrtf.py --work /tmp/hrtf-work
#   (or --sofa path/to/D2_48K_24bit_256tap_FIR_SOFA.sofa to skip the download)
import argparse
import hashlib
import struct
import sys
import urllib.request
import zipfile
from pathlib import Path

import h5py
import numpy as np
import scipy.signal

URL = "https://zenodo.org/records/12092466/files/D2_HRIR_SOFA.zip?download=1"
ZIP_SHA256 = "2561af2c09688c95f3909303cd2ac51cb8c15abb574903f17f635e1376cbce94"
MEMBER = "D2_HRIR_SOFA/D2_48K_24bit_256tap_FIR_SOFA.sofa"
ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / "engine" / "src" / "Anode.Dsp" / "Spatial" / "Hrtf" / "sadie2-d2-48k.anhrtf"
METADATA = (
    "SADIE II Database, subject D2 (KEMAR), 48 kHz 256-tap HRIRs (v2-2). "
    "Copyright 2018, University of York. Licensed under the Apache License, Version 2.0 "
    "(http://www.apache.org/licenses/LICENSE-2.0). Modified by Anode: a subset of the directions (2-degree azimuth "
    "grid at elevations -75..75 step 15, and the poles), quantised to 16 bits, with each ear's delay added "
    "(scripts/build-hrtf.py). Armstrong, Thresh, Murphy, Kearney: A Perceptual Evaluation of Individual and "
    "Non-Individual HRTFs: A Case Study of the SADIE II Database, Applied Sciences 8(11), 2018, "
    "doi:10.3390/app8112029."
)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def fetch(work):
    work.mkdir(parents=True, exist_ok=True)
    archive = work / "D2_HRIR_SOFA.zip"
    if not archive.exists() or sha256(archive) != ZIP_SHA256:
        print("downloading", URL)
        urllib.request.urlretrieve(URL, archive)
    if sha256(archive) != ZIP_SHA256:
        sys.exit(f"{archive}: SHA-256 mismatch (expected {ZIP_SHA256})")
    target = work / "sofa"
    with zipfile.ZipFile(archive) as z:
        z.extract(MEMBER, target)
    return target / MEMBER


def delays(ir, rate):
    b, a = scipy.signal.butter(4, 1500 / (rate / 2))
    low = np.abs(scipy.signal.filtfilt(b, a, ir, axis=-1))
    peak = np.argmax(low, axis=-1)
    at = np.clip(peak, 1, ir.shape[-1] - 2)
    y0 = np.take_along_axis(low, (at - 1)[..., None], -1)[..., 0]
    y1 = np.take_along_axis(low, at[..., None], -1)[..., 0]
    y2 = np.take_along_axis(low, (at + 1)[..., None], -1)[..., 0]
    denominator = y0 - 2 * y1 + y2
    offset = np.where(np.abs(denominator) > 1e-12, 0.5 * (y0 - y2) / np.where(denominator == 0, 1, denominator), 0)
    return (at + np.clip(offset, -0.5, 0.5)).astype(np.float32)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sofa", type=Path)
    parser.add_argument("--work", type=Path, default=Path("hrtf-work"))
    parser.add_argument("--output", type=Path, default=OUTPUT)
    args = parser.parse_args()
    sofa = args.sofa or fetch(args.work)

    with h5py.File(sofa, "r") as f:
        rate = int(f["Data.SamplingRate"][0])
        positions = f["SourcePosition"][:]
        irs = f["Data.IR"][:]
    azimuths = positions[:, 0]
    elevations = positions[:, 1]

    keep = []
    for i, (az, el) in enumerate(zip(azimuths, elevations)):
        el_r = round(el, 1)
        if abs(abs(el_r) - 90) < 1e-6:
            keep.append(i)
        elif abs(el_r / 15 - round(el_r / 15)) < 1e-6 and abs(el_r) <= 75 and abs(az / 2 - round(az / 2)) < 1e-6:
            keep.append(i)
    keep = np.array(sorted(set(keep), key=lambda i: (round(elevations[i], 1), azimuths[i] % 360)))
    az = ((azimuths[keep] + 180) % 360) - 180
    el = elevations[keep]
    data = irs[keep].astype(np.float64)
    taps = data.shape[-1]
    scale = np.abs(data).max() / 32767.0
    quantised = np.round(data / scale).astype("<i2")
    delay = delays(data, rate)

    meta = METADATA.encode("utf-8")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with open(args.output, "wb") as out:
        out.write(b"ANHR")
        out.write(struct.pack("<IIIIfI", 1, rate, taps, len(keep), scale, len(meta)))
        out.write(meta)
        table = np.stack([az, el, delay[:, 0], delay[:, 1]], axis=1).astype("<f4")
        out.write(table.tobytes())
        out.write(quantised.tobytes())

    print(f"{args.output}: {len(keep)} directions, {taps} taps at {rate} Hz, "
          f"{args.output.stat().st_size} bytes, sha256 {sha256(args.output)}")
    # The binaural check of docs/11-phase-10.md, on the data itself.
    i = int(np.argmin(np.abs(az - 90) + np.abs(el)))
    left, right = data[i]
    ild = 10 * np.log10((left ** 2).sum() / (right ** 2).sum())
    print(f"90 degrees left: ILD {ild:.1f} dB, ITD {(delay[i, 1] - delay[i, 0]) / rate * 1000:.3f} ms")


main()

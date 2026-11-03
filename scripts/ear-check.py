#!/usr/bin/env python3
# The Phase 10 golden path's ear helper (editor/apps/desktop/e2e/phase10/helpers.ts, ANODE_EAR_HELPER), run with the
# python of the venv scripts/setup-ear.py makes (engine/artifacts/ear; ANODE_EAR_PYTHON): the EBU ADM Renderer
# (ear, BSD-3-Clause-Clear; ITU-R BS.2127) as docs/spatial/README.md's opt-in tests use it.
#
#   ear-check.py validate <file.wav>               chna and axml read and validated per ITU-R BS.2076 (ear's own
#                                                   checks, strict timing), and the strict render accepted: exit 0
#   ear-check.py render <file.wav> <layout> <out>   ear-render --strict -s <layout> (e.g. 4+7+0): the speakers in
#                                                   BS.2051 order
#
# ear 2.1.0 reads a 32-bit float BW64 as 32-bit integers: give it 24-bit files (Anode's ADM export default).
import os
import subprocess
import sys
import tempfile


def validate(path):
    from ear.fileio import openBw64Adm
    from ear.fileio.adm.chna import validate_trackIndex
    from ear.fileio.adm.timing_fixes import check_blockFormat_timings

    with openBw64Adm(path) as reader:
        if reader.adm is None:
            raise SystemExit(f"{path}: no chna/axml (not an ADM BWF)")
        adm = reader.adm
        adm.validate()
        validate_trackIndex(adm, reader.channels)
        check_blockFormat_timings(adm, fix=False)
        programmes = list(adm.audioProgrammes)
        if not programmes:
            raise SystemExit(f"{path}: no audioProgramme")
        print(
            f"{path}: {reader.channels} channels at {reader.sampleRate} Hz, {reader.bitdepth}-bit; "
            f"{len(programmes)} programme(s), {len(list(adm.audioObjects))} object(s), "
            f"{len(list(adm.audioTrackUIDs))} track UID(s): valid"
        )
    # The strict render refuses anything it would have to guess about.
    with tempfile.TemporaryDirectory() as folder:
        render(path, "4+7+0", os.path.join(folder, "check.wav"))


def render(path, layout, out):
    result = subprocess.run(
        [sys.executable, "-I", "-m", "ear.cmdline.render_file", "--strict", "-s", layout, path, out],
        capture_output=True,
        text=True,
    )
    for line in (result.stderr or "").splitlines():
        # BS.2127 standardises BS.2076-1 rendering only: ear says so of a BS.2076-2 file, which is no finding.
        if line.strip() and "BS.2076-2" not in line and "version" not in line.lower():
            print(line, file=sys.stderr)
    if result.returncode != 0:
        raise SystemExit(f"ear-render failed ({result.returncode}): {result.stderr.strip()}")


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "validate":
        validate(sys.argv[2])
    elif len(sys.argv) == 5 and sys.argv[1] == "render":
        render(sys.argv[2], sys.argv[3], sys.argv[4])
    else:
        raise SystemExit("Usage: ear-check.py validate <file.wav> | render <file.wav> <layout> <out.wav>")


if __name__ == "__main__":
    main()

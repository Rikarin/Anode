#!/usr/bin/env python3
# Sets up the EBU ADM Renderer (`ear`, BSD-3-Clause-Clear, https://github.com/ebu/ebu_adm_renderer), the independent
# ITU-R BS.2127 renderer Anode's spatial tests use (docs/spatial/README.md): a virtual environment at engine/artifacts/ear
# with ear pinned. The opt-in tests in engine/tests/Anode.Interchange.Tests/Adm/EarTests.cs and
# engine/tests/Anode.Dsp.Tests/Spatial/EarPannerTests.cs run when engine/artifacts/ear/bin/python (Scripts\python.exe on
# Windows) exists, or the python ANODE_EAR names, and are skipped otherwise. The tests call `ear-render` through the
# module (python -m ear.cmdline.render_file) and ear's point-source panner through scripts/ear-gains.py.
#
# Usage: python3 scripts/setup-ear.py
import subprocess
import sys
import venv
from pathlib import Path

EAR = "ear==2.1.0"
ROOT = Path(__file__).resolve().parent.parent
TARGET = ROOT / "engine" / "artifacts" / "ear"


def main():
    if not TARGET.exists():
        venv.EnvBuilder(with_pip=True).create(TARGET)
    python = TARGET / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")
    subprocess.run([str(python), "-m", "pip", "install", "--quiet", "--disable-pip-version-check", EAR], check=True)
    subprocess.run([str(python), "-I", "-c", "import ear; print('ear ready:', ear.__file__)"], check=True)


main()

#!/usr/bin/env python3
# Sets up the EBU ADM Renderer (`ear`, BSD-3-Clause-Clear, https://github.com/ebu/ebu_adm_renderer), the independent
# ITU-R BS.2127 renderer Anode's spatial tests use (docs/spatial/README.md): a virtual environment at engine/artifacts/ear.
# The opt-in tests (engine/tests/Anode.Dsp.Tests/Spatial/EarPannerTests.cs, engine/tests/Anode.Interchange.Tests/Adm/
# EarAdmTests.cs) run when engine/artifacts/ear/bin/python (Scripts\python.exe on Windows) exists, or the python
# ANODE_EAR names, and are skipped otherwise. They call ear-render as `python -m ear.cmdline.render_file` and ear's
# panners through scripts/ear-gains.py.
#
# ear 2.1.0 (the latest release, also the version of the pinned commit below) declares numpy~=1.14 and lxml~=4.4,
# which have no builds for current Pythons (3.13). It runs on numpy 2, so the dependencies are installed here with
# current versions and ear itself from the pinned commit of its repository, by checksum, without its declared
# dependencies. Any Python from 3.10 works.
#
# Usage: python3 scripts/setup-ear.py
import subprocess
import sys
import venv
from pathlib import Path

EAR = (
    "ear @ https://github.com/ebu/ebu_adm_renderer/archive/5bb17b4278e8e67f24f90efb7a81a8aea7aed4f3.tar.gz"
    "#sha256=9fcb54bb4246ed41316334874a0ae69b4339965dfb58f896439109917e81359d"
)
DEPENDENCIES = [
    "numpy>=1.26,<3",
    "scipy>=1.11",
    "attrs>=22.2",
    "PyYAML~=6.0",
    "lxml>=5.1",
    "six~=1.11",
    "multipledispatch~=1.0",
    "importlib_resources>=5.0",
]
ROOT = Path(__file__).resolve().parent.parent
TARGET = ROOT / "engine" / "artifacts" / "ear"


def main():
    if not TARGET.exists():
        venv.EnvBuilder(with_pip=True).create(TARGET)
    python = TARGET / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")
    pip = [str(python), "-m", "pip", "install", "--quiet", "--disable-pip-version-check"]
    subprocess.run(pip + DEPENDENCIES, check=True)
    subprocess.run(pip + ["--no-deps", EAR], check=True)
    subprocess.run(
        [str(python), "-I", "-c", "import ear.cmdline.render_file, ear; print('ear ready:', ear.__file__)"],
        check=True,
    )


main()

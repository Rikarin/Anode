#!/usr/bin/env python3
# Sets up pyaaf2 (MIT, https://github.com/markreidvfx/pyaaf2), the independent AAF reader and writer Anode's AAF tests
# use (docs/interchange/aaf.md): a virtual environment at engine/artifacts/pyaaf2 with pyaaf2 pinned. The opt-in tests in
# engine/tests/Anode.Interchange.Tests/Aaf/PyAafTests.cs run when engine/artifacts/pyaaf2/bin/python (Scripts\python.exe
# on Windows) exists, or the python ANODE_PYAAF2 names, and are skipped otherwise. The same environment runs
# scripts/aaf-model.py and scripts/make-aaf-fixtures.py.
#
# Usage: python3 scripts/setup-pyaaf2.py
import subprocess
import sys
import venv
from pathlib import Path

PYAAF2 = "pyaaf2==1.7.1"
ROOT = Path(__file__).resolve().parent.parent
TARGET = ROOT / "engine" / "artifacts" / "pyaaf2"


def main():
    if not TARGET.exists():
        venv.EnvBuilder(with_pip=True).create(TARGET)
    python = TARGET / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")
    subprocess.run([str(python), "-m", "pip", "install", "--quiet", "--disable-pip-version-check", PYAAF2], check=True)
    subprocess.run([str(python), "-I", "-c", "import aaf2; print('pyaaf2 ready:', aaf2.__file__)"], check=True)


main()

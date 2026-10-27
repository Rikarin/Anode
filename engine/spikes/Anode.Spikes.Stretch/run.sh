#!/usr/bin/env bash
# Spike S5: Signalsmith Stretch quality and CPU across Flex modes (docs/spikes/S5-time-stretch.md).
# Usage: run.sh [file.wav ...]
# Builds the native shim and the harness, then writes renders (32-bit float WAVs, for listening) and results-*.md to
# engine/artifacts/spike-stretch. Takes about two minutes on an M1 Max; keep the machine otherwise idle.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
engine="$here/../.."
out="$engine/artifacts/spike-stretch"
bin="$here/bin/Release/net10.0/Anode.Spikes.Stretch"

"$engine/native/build.sh" >/dev/null
dotnet build "$here" -c Release -v quiet -nologo >/dev/null

# Everything, with the library the engine would ship on this platform (Accelerate's FFT on Apple).
"$bin" --out "$out" "$@"

# Apple only: the cost without Accelerate, i.e. Linear's own FFT, which is what Windows and Linux get.
if [ "$(uname -s)" = "Darwin" ]; then
  "$bin" --out "$out" --lib an_stretch_noaccel --suite cpu,blocks --no-wav
fi

echo "renders and results: $out"

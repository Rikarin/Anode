#!/usr/bin/env bash
# Spike S3 (docs/spikes/S3-bulk-ipc.md): publishes the harness NativeAOT, as anode-host ships, and runs the whole
# matrix; the server role spawns the host role and prints Markdown tables.
# Usage: run.sh [results.md] [extra server options, e.g. --swaps 20 --sizes 10000,1000000]
# Takes about two minutes and a spinning core (the simulated audio thread); plays nothing.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
results="${1:-$here/results.md}"
shift || true

dotnet publish "$here" -c Release -o "$here/bin/aot" -v quiet -nologo >/dev/null
"$here/bin/aot/Anode.Spikes.BulkIpc" server "$@" | tee "$results"
echo "results: $results" >&2

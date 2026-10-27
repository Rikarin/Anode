#!/usr/bin/env bash
# Spike S4 matrix: cross-process in-cycle wake for sandboxed plugins (docs/spikes/S4-sandbox-wake.md).
# Usage: run.sh "<device uid>" [results.jsonl]      (device uids: anode-s4 devices)
# Plays nothing audible (the output stays zeroed), but holds the device for about 25 minutes. Needs the native
# libraries (engine/native/build.sh) and publishes the spike NativeAOT into engine/artifacts/s4.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
engine="$here/../.."
device="${1:?device uid (see: anode-s4 devices)}"
results="${2:-$here/results.jsonl}"
bin="$engine/artifacts/s4/anode-s4"
warmup="${WARMUP:-3}"
seconds="${SECONDS_PER_RUN:-15}"
threads="$(sysctl -n hw.ncpu 2>/dev/null || nproc)"

dotnet publish "$here" -c Release -r "$(dotnet --info | awk '/RID:/ {print $2; exit}')" -nologo -v quiet -o "$engine/artifacts/s4" >/dev/null
: > "$results"

# anode-host's GC settings (docs/adr/0001-process-model.md): a 256 MB gen0 budget, bare hex.
export DOTNET_GCgen0size=10000000

host() { "$bin" host --device "$device" --warmup "$warmup" --seconds "$seconds" --results "$results" "$@"; }
# Background load at default priority on every core: arithmetic plus 16 MB memory streaming per thread.
loaded() { "$bin" load --threads "$threads" --seconds $((warmup + seconds + 4)) & local pid=$!; sleep 1; host --load load "$@"; wait "$pid"; }
both() { host --load idle "$@"; loaded "$@"; }

# A. Wake primitives: 8 instances over 2 sandboxes on the device at 64 frames.
for kind in address semaphore spin; do
  both --label "A primitive" --clock device --frames 64 --kind "$kind" --sandboxes 2 --instances 8
done
both --label "A primitive" --clock device --frames 64 --kind spin --rt 0 --sandboxes 2 --instances 8
both --label "A primitive" --clock device --frames 64 --kind address --host-spin-us 30 --sandbox-spin-us 30 --sandboxes 2 --instances 8

# B. The grid with the address wait: instances x sandboxes x clock x load.
for clock in "device 64" "device 32" "null 64"; do
  set -- $clock
  for nk in "1 1" "8 1" "8 2" "8 4" "50 1" "50 2" "50 4"; do
    read -r n k <<< "$nk"
    both --label "B grid" --clock "$1" --frames "$2" --kind address --sandboxes "$k" --instances "$n"
  done
done

# C. Heavier plugins: 50 instances burning 10 µs each (500 µs of DSP per cycle) over 1, 2 and 4 sandboxes.
for k in 1 2 4; do
  both --label "C 10 µs/instance" --clock device --frames 64 --kind address --sandboxes "$k" --instances 50 --burn-us 10
done

# D. Sandbox threads joined to the device's IO workgroup (macOS), against B and C.
for f in 64 32; do
  for k in 2 4; do
    loaded --label "D workgroup" --clock device --frames "$f" --kind address --sandboxes "$k" --instances 50 --workgroup 1
  done
done
loaded --label "D workgroup" --clock device --frames 64 --kind address --sandboxes 4 --instances 50 --burn-us 10 --workgroup 1

# E. No real-time policy on the sandbox threads at all.
loaded --label "E no rt" --clock device --frames 64 --kind address --sandboxes 4 --instances 50 --rt 0

"$bin" summarize "$results"
echo "results: $results"

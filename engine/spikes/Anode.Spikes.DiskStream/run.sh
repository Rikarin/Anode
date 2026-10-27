#!/usr/bin/env bash
# Spike S2 matrix: disk streaming of 32/64/128(/256) mono 48 kHz / 24-bit tracks at 64 frames, warm and cold page
# cache, with and without a cycle jump (docs/spikes/S2-disk-streaming.md).
# Usage: run.sh ["<device uid>" | null] [results.jsonl]
# With a device uid (the `devices` list printed first) the callback runs on that device with its output silent; with
# `null` (the default) a real-time thread ticks at the buffer period. Writes 7.7 GB of test audio to
# engine/artifacts/spike-audio (git-ignored) once, and takes about 15 minutes.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
engine="$here/../.."
device="${1:-null}"
results="${2:-$here/results.jsonl}"
audio="$engine/artifacts/spike-audio"
out="$engine/artifacts/spike-diskstream"
bin="$out/Anode.Spikes.DiskStream"

ls "$engine"/artifacts/native/*cth_audio* >/dev/null 2>&1 || "$engine/native/build.sh" >/dev/null
dotnet publish "$here" -c Release -v quiet -nologo -o "$out" >/dev/null
"$bin" devices || true
"$bin" generate --dir "$audio" --tracks 256 --min-seconds 120 --max-seconds 300
: > "$results"

# The host's gen0 budget (docs/adr/0001-process-model.md): NativeAOT reads it only from the environment, as bare hex.
export DOTNET_GCgen0size=10000000
if [ "$device" = null ]; then clock=(--clock null); else clock=(--clock device --device "$device"); fi
warm=(--prewarm)                                     # read the files through the page cache first
cold=(--evict --nocache)                             # drop the files from the page cache, then read around it
cycle=(--cycle --cycle-start-bar 5 --cycle-bars 8)   # 8 bars at 120 bpm (16 s); playback starts at the cycle start

run() { "$bin" run --dir "$audio" "${clock[@]}" --buffer 64 --results "$results" "$@" || true; }

# Self-tests: the checks must catch what they claim to catch (both rows must show failures).
run --tracks 8 --cycle --cycle-bars 1 --naive-cycle --warmup 1 --seconds 8 --label "self-test: naive cycle"
run --tracks 8 --alloc-in-callback --warmup 1 --seconds 4 --label "self-test: alloc in callback"

# Main matrix, default design (ring 2 s, 96 KiB reads, one disk thread).
for tracks in 32 64 128; do
  run --tracks "$tracks" "${warm[@]}" --warmup 3 --seconds 40 --label "$tracks warm"
  run --tracks "$tracks" "${warm[@]}" "${cycle[@]}" --warmup 3 --seconds 40 --label "$tracks warm cycle"
  run --tracks "$tracks" "${cold[@]}" --warmup 3 --seconds 40 --label "$tracks cold"
  run --tracks "$tracks" "${cold[@]}" "${cycle[@]}" --warmup 3 --seconds 40 --label "$tracks cold cycle"
done

# Headroom: a 1-bar (2 s) cycle, competing disk load, twice the tracks.
run --tracks 128 "${cold[@]}" --cycle --cycle-bars 1 --warmup 3 --seconds 30 --label "128 cold 1-bar cycle"
run --tracks 128 "${cold[@]}" "${cycle[@]}" --io-hog --warmup 3 --seconds 30 --label "128 cold cycle + io hog"
run --tracks 256 "${cold[@]}" "${cycle[@]}" --warmup 3 --seconds 30 --label "256 cold cycle"
run --tracks 256 "${cold[@]}" "${cycle[@]}" --io-hog --warmup 3 --seconds 30 --label "256 cold cycle + io hog"

# Design sweep (cold, 1-bar cycle, competing load): how small can the ring and the reads be?
for spec in "0.25 24" "0.5 24" "0.5 48" "1 48" "1 96" "2 192" "4 384"; do
  read -r ring kb <<<"$spec"
  run --tracks 128 "${cold[@]}" --cycle --cycle-bars 1 --io-hog --ring-seconds "$ring" --read-kb "$kb" \
    --warmup 3 --seconds 20 --label "sweep 128: ring $ring s, read $kb KiB"
done
for threads in 1 2 4; do
  run --tracks 256 "${cold[@]}" --cycle --cycle-bars 1 --io-hog --ring-seconds 0.5 --read-kb 48 --threads "$threads" \
    --warmup 3 --seconds 20 --label "sweep 256: ring 0.5 s, read 48 KiB, $threads thread(s)"
done

echo "results: $results (python3 -I $here/summarize.py $results)"

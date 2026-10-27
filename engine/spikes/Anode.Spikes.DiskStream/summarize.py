#!/usr/bin/env python3
"""Turns results.jsonl from run.sh into a markdown table. Usage: python3 -I summarize.py results.jsonl"""
import json
import sys

rows = [json.loads(line) for line in open(sys.argv[1]) if line.strip()]
print("| Configuration | Resident | Ring / read | Prime | Callbacks | Underruns | Position errors | Jumps | Low water "
      "| Callback p50 / p99 / max (µs) | Worst % of period | Overloads / late | Callback alloc | GCs "
      "| Disk MB/s (needed) | Read p50 / p99 / max (ms) | Competing load | CPU process / callback |")
print("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
for r in rows:
    hog = f"{r['IoHogMBps']:.0f} MB/s" if r['IoHogMBps'] > 0 else "—"
    worst = max(r['CallbackMaxPercent'], r['NativeMaxPercent'])
    print(f"| {r['Label']} | {r['ResidentPercent']:.0f} % | {r['RingSeconds']:.2f} s / {r['ReadKb']} KiB "
          f"| {r['PrimeMs']:.0f} ms | {r['Callbacks']} | {r['Underruns']} | {r['PositionErrors']} | {r['CycleJumps']} "
          f"| {r['LowWaterMs']:.0f} ms | {r['CallbackP50Us']:.1f} / {r['CallbackP99Us']:.1f} / {r['CallbackMaxUs']:.1f} "
          f"| {worst:.1f} % | {r['LateOrOverloads']} | {r['CallbackAllocatedBytes']} B | {r['Gcs']} "
          f"| {r['DiskMBps']:.1f} ({r['NeededMBps']:.1f}) | {r['ReadP50Ms']:.2f} / {r['ReadP99Ms']:.2f} / {r['ReadMaxMs']:.1f} "
          f"| {hog} | {r['ProcessCpuPercent']:.1f} % / {r['CallbackCpuPercent']:.1f} % |")

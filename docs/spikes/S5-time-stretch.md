# Spike S5: Signalsmith Stretch for Flex Time, Flex Pitch, Varispeed and tempo-following loops

- **Status:** done. **Go**, with conditions (see Recommendation)
- **Date:** 2026-10-27
- **Plan:** docs/01-implementation-plan.md §10.2, §11 (Flex Time, Pitch and Varispeed), §12 Phase 0 spike 5
- **Code:** `engine/native/an_stretch` (C ABI shim), `engine/spikes/Anode.Spikes.Stretch` (harness, `run.sh`)

## Question

Is Signalsmith Stretch (MIT, ADR-0008) good enough and cheap enough to be Anode's engine for Flex Time (ratios about
0.5–2), Flex Pitch and pitch shift (±12 semitones, with formant handling), Varispeed and tempo-following loops?

## Answer

**Yes for polyphonic stretch, region transposition, Varispeed (speed only) and tempo-following loops. Partly for
Flex Pitch.** CPU is negligible: under 1 % of one M1 Max core per channel in every mode. Quality is clean from 0.75× to
1.5×, still usable at 0.5× and 2×. Clicks stay sharp, and onsets land within about 1 ms (median) of where the tempo map
puts them, even when the rate changes on every block.

There are four limits:

1. **Low-register tuning under transposition.** A transposed 110 Hz tone is off by up to 17 ¢ with the default preset
   and 30 ¢ with the cheaper one. Low tones closer together than the analysis window can resolve are off by up to 100 ¢.
   A 300 ms block fixes this (≤ 13 ¢) but costs more smearing and latency.
2. **Formant compensation is approximate, and it costs level.** It brings the vocal envelope back from a 9–11 st shift
   to 0.5–2.5 st. Shifting up loses 3–6 dB of level, and f0 gross errors rise to 2–8 % of frames.
3. **The work comes in bursts.** Without split computation, each instance does a whole spectral block, about
   200–250 µs stereo, once every 30–40 ms. Several instances that hit that block on the same 64-frame callback would
   overrun it. Split computation smooths the work to ≤ 30 µs per 64-frame call, for 30–40 ms more output latency.
4. **It is not a live effect.** Input-to-output latency is 100–150 ms. That is fine for regions read from disk and
   unusable for monitoring a live input.

Monophonic Flex Pitch (vocal and bass note editing) should get a PSOLA path driven by the pYIN analysis. Slicing,
Speed and Tempophone do not need a stretcher at all.

## Setup

| | |
|---|---|
| Machine | Apple M1 Max (8 performance + 2 efficiency cores), macOS 27.2, Apple clang 21.0.0, CMake 4.4.4, .NET 10.0.14 (Release, TieredPGO) |
| Library | Signalsmith Stretch **1.4.0 + 1 commit** (`88061fd4eda56a2353c5d65c74aa3f4ccf76f9da`, main on 2026-10-22: 1.4.0 plus a bump of Linear to 0.6.6). Signalsmith Linear **0.6.6** (`7bb1c8f36794d6980080ce817c91f8da8fe56a66`). Both are MIT ("Copyright (c) 2022 Geraint Luff / Signalsmith Audio Ltd.", "Copyright (c) 2025 Signalsmith Audio") and header-only. The header's own `version` constant still says 1.3.2, so the pin is the commit. |
| FFT | Apple's Accelerate (`an_stretch`, what Anode would ship on macOS) and Linear's own FFT (`an_stretch_noaccel`, what Windows and Linux get). |
| Build | `-O3`, no `-ffast-math`. The shim builds with `-Wall -Wextra -Werror`; upstream headers are included as `SYSTEM`. |
| Rate | 48 kHz, float, planar. Fixed random seed, so renders are bit-reproducible. |

**Load caveat.** Other Phase 0 spikes ran on the same machine at the same time. The load average reached 18–29 on 10
cores during the first run. The CPU and callback tables below come from a re-run at a 1-minute load of 12–15.
They agree within about 10 % with the first run at load 28. Treat single-run maxima (the "max" column, occasionally
1–3 ms) as preemption by the other load, not stretcher cost. The p99.9 column is the stretcher's own worst case.

### The shim

`an_stretch.h` wraps `SignalsmithStretch<float>`. Its calls are:

- `create(channels, sampleRate, preset, split)` and `create_custom(block, interval)`
- `set_transpose_{factor,semitones}`, with an optional tonality limit
- `set_formant_factor(factor, compensatePitch)` and `set_formant_base(Hz)`
- `process(in, inFrames, out, outFrames)`
- `seek`, `output_seek` (an aligned start) and `flush` (an aligned end)
- `exact`, for offline use
- `input_latency`, `output_latency`, `block_frames`, `interval_frames`, `reset`, `destroy` and `version`

`build.sh` fetches both pins once into `engine/artifacts/deps`, with retries, as Cathode's `cth_nam` does. Upstream's
CMakeLists is not used, because it fetches Linear by a moving, shallow tag. `Directory.Build.targets` now also copies
`*an_*` native libraries next to executables.

### Method

The harness drives the stretcher the way the engine would:

- **Streaming.** Output comes in fixed blocks, 128 frames for the quality runs, and each call takes as many input
  frames as the rate needs.
- **Aligned start and end.** Each render opens with `output_seek`, so output frame 0 plays input frame 0. Input is fed
  `inputLatency` ahead of the processing time, and the render ends with `inputLatency` of silence plus a
  `flush(outputLatency)`.
- **Known content time.** Every render keeps the map from output frame to input time, the integral of the rate. Every
  metric compares against where the input should be.

Output does not depend on the callback size: renders at 32, 128 and 1024 frames are bit-identical. Split computation
changes samples by at most 6e-7.

The test material is synthesized in `Signals.cs`:

| Signal | What it is |
|---|---|
| sweep | Logarithmic sine sweep, 40 Hz → 12 kHz in 8 s, at −6 dBFS |
| tones | Steady sines at 110, 440, 1000 and 5000 Hz |
| chord | C4 E4 G4 C5, four harmonics each at 1/h. C4×3 and G4×2 are 0.9 Hz apart. |
| clicks | Twelve 1 ms decaying noise clicks, 0.5 s apart, over silence |
| drums | 120 bpm kit: synthesized kick, snare and bright hats on eighths |
| vocal | An "ah" sung on four notes: band-limited glottal-like source, four formant resonators (700/1220/2600/3500 Hz), delayed 5.5 Hz vibrato of ±40 ¢ |
| low-tones | 55, 82.4, 110, 220 and 440 Hz together. The lowest pair is closer than the analysis window resolves. |

Any WAV given on the command line is rendered too. Every render is written as 32-bit float WAV to
`engine/artifacts/spike-stretch/<signal>/<config>/` for listening (about 240 MB).

The quality proxies are computed in `Analysis.cs`:

- **Pitch error (¢).** The peak of a 1.4 s Hann FFT, interpolated, with ±100 ¢ search around the expected
  frequency × transposition.
- **Non-partial energy (dB).** Energy outside ±max(15 Hz, 1 %) of every expected partial, relative to the total. This
  is a phasiness and artefact proxy: the input is below −100 dB.
- **Sweep error.** Frame by frame (85 ms) against the input frequency at that content time: median and p95.
- **Vocal f0.** YIN every 10 ms on the steady parts, against the known f0 curve. Gross means more than 50 ¢ off or
  unvoiced.
- **Clicks.** The shortest span holding 90 % of the energy within ±100 ms (the input's is 0.2 ms), the energy more than
  2 ms early ("pre-echo"), the 10 %-energy onset against expected, and the peak change.
- **Drums.** The same measures within ±40 ms, reported as changes against the input's.
- **Duration.** This is exact by construction: output length = input length × ratio. Timing accuracy is the onset
  error.
- **RMS Δ and balance.** The RMS change, plus the mean absolute third-octave deviation after removing the level change
  ("balance dev").
- **Formants.** Cepstrally smoothed long-term envelope, 200 Hz–5 kHz. The shift that best realigns it with the input's
  (0 = preserved), and the unshifted mean difference.

## Results

### Latency

| config | block | interval | input latency | output latency | live in→out | seek length |
|---|---|---|---|---|---|---|
| default | 5760 (120 ms) | 1440 (30 ms) | 2880 (60 ms) | 2880 (60 ms) | 120 ms | 7200 |
| default+split | 5760 | 1440 | 2880 (60 ms) | 4320 (90 ms) | 150 ms | 7200 |
| cheaper | 4800 (100 ms) | 1920 (40 ms) | 2400 (50 ms) | 2400 (50 ms) | 100 ms | 6720 |
| cheaper+split | 4800 | 1920 | 2400 (50 ms) | 4320 (90 ms) | 140 ms | 6720 |

For a region played from disk, latency costs nothing. The engine reads `inputLatency` ahead and starts with
`output_seek`, so output is sample-aligned to the timeline. Starting or relocating needs one seek length of input,
150 ms, plus the pre-roll computation.

### Time stretch, default preset (ratio = output length / input length)

| ratio | 110 Hz ¢ | 440 Hz ¢ | 1k/5k ¢ | tones non-partial dB | chord max ¢ | chord non-partial dB | sweep median / p95 ¢ | vocal f0 median / p95 ¢ | f0 gross % |
|---|---|---|---|---|---|---|---|---|---|
| 0.50× | 0.0 | 0.0 | 0.0 | −50.4 | 0.00 | −49.5 | 8.8 / 17.4 | 4.0 / 15.9 | 0.0 |
| 0.75× | 0.0 | 0.0 | 0.0 | −57.5 | 0.00 | −56.1 | 3.1 / 5.6 | 1.1 / 8.5 | 0.0 |
| 1.00× | 0.0 | 0.0 | 0.0 | −85.4 | 0.00 | −84.1 | 0.1 / 0.2 | 0.6 / 2.0 | 0.0 |
| 1.25× | 0.0 | 0.0 | 0.0 | −59.6 | 0.00 | −54.4 | 1.9 / 3.6 | 1.7 / 6.5 | 0.0 |
| 1.50× | 0.0 | 0.0 | 0.0 | −50.8 | 0.00 | −45.7 | 3.1 / 5.9 | 1.9 / 12.3 | 0.0 |
| 2.00× | 0.0 | 0.0 | 0.0 | −47.3 | 0.01 | −47.4 | 5.1 / 13.5 | 2.4 / 10.7 | 0.0 |

| ratio | click 90 % width ms | click pre-echo dB | click onset err ms | click peak Δ dB | drum onset err median / max ms | drum 90 % width Δ ms | drum pre-onset Δ dB |
|---|---|---|---|---|---|---|---|
| 0.50× | 0.2 | −45.9 | 0.0 | −3.7 | 2.4 / 21.7 | +2.5 | +4.7 |
| 0.75× | 0.2 | −39.8 | 0.1 | −2.5 | 0.8 / 13.5 | +1.0 | +2.0 |
| 1.00× | 0.2 | — | 0.0 | 0.0 | 0.0 / 0.0 | 0.0 | 0.0 |
| 1.25× | 0.3 | −29.2 | −0.4 | −0.1 | 2.0 / 8.4 | +0.2 | −0.3 |
| 1.50× | 0.4 | −28.2 | 0.0 | +0.1 | 3.4 / 6.5 | −1.1 | 0.0 |
| 2.00× | 0.5 | −26.5 | 0.0 | 0.0 | 6.6 / 9.9 | −1.6 | −0.6 |

| ratio | RMS Δ dB chord / drums / vocal | balance dev dB chord / drums / vocal / sweep |
|---|---|---|
| 0.50× | −0.01 / −1.03 / −0.70 | 0.02 / 0.30 / 0.51 / 2.40 |
| 0.75× | 0.00 / −0.34 / −0.16 | 0.01 / 0.31 / 0.13 / 1.07 |
| 1.25× | 0.00 / −0.21 / −0.06 | 0.00 / 0.19 / 0.06 / 2.09 |
| 1.50× | 0.00 / −0.41 / −0.19 | 0.00 / 0.21 / 0.10 / 1.82 |
| 2.00× | −0.22 / −1.04 / −0.68 | 0.27 / 0.29 / 0.21 / 1.98 |

Time stretch alone never detunes steady tones; every tone stays within 0.01 ¢.

- **The cost of stretching** shows as non-partial energy. It is −55 to −60 dB at 0.75–1.25× and rises to about −47 dB
  at the extremes.
- **Clicks** keep their 0.2–0.5 ms width. Some energy comes early as a pre-echo ramp that starts about 95 ms ahead:
  −27 dB of the click's energy when slowing down, −40 to −46 dB when speeding up.
- **Drums** soften slightly. Their 10 %-energy point is late by a median of 2–7 ms, worst 22 ms at 0.5×. That is the
  attacks getting softer, not hits moving: clicks show no offset.
- **Level** holds within 1 dB.

### Time stretch, cheaper preset (excerpt)

| ratio | tones non-partial dB | chord non-partial dB | sweep p95 ¢ | vocal f0 p95 ¢ | chord RMS Δ dB | chord balance dev dB |
|---|---|---|---|---|---|---|
| 0.50× | −29.4 | −29.9 | 26.3 | 19.1 | −0.04 | 0.02 |
| 0.75× | −34.3 | −35.8 | 6.9 | 8.0 | −0.03 | 0.03 |
| 1.25× | −33.7 | −31.7 | 4.3 | 5.1 | −0.02 | 0.01 |
| 1.50× | −31.0 | −29.9 | 5.7 | 10.1 | −0.02 | 0.01 |
| 2.00× | −26.7 | −26.6 | 20.5 | 14.7 | −1.40 | 2.51 |

The cheaper preset is about 20 dB worse on tonal material: −27 to −36 dB of non-partial energy against −46 to −60 dB.
At 2× it audibly loses the chord's closely spaced partials (−1.4 dB, 2.5 dB balance error). Its transient and timing
numbers match the default's (full tables in `results-an_stretch.md`).

### Pitch shift at 1×, default preset

| shift | 110 Hz ¢ | 440 Hz ¢ | 1k/5k ¢ | tones non-partial dB | chord max ¢ | chord non-partial dB | sweep median / p95 ¢ | vocal f0 median / p95 ¢ | f0 gross % |
|---|---|---|---|---|---|---|---|---|---|
| −12 | 7.1 | 6.7 | 0.1 | −51.2 | 20.3 | −54.7 | 1.3 / 22.8 | 2.2 / 11.2 | 0.0 |
| −5 | 16.9 | 3.8 | 1.5 | −55.4 | 8.5 | −48.8 | 2.1 / 22.5 | 2.0 / 5.2 | 0.0 |
| +3 | 17.2 | 4.4 | 0.0 | −51.6 | 4.4 | −46.2 | 2.4 / 24.9 | 1.5 / 3.7 | 0.0 |
| +7 | −0.5 | 0.0 | 1.3 | −50.2 | 6.4 | −43.4 | 3.7 / 23.9 | 1.6 / 4.0 | 0.0 |
| +12 | −1.8 | 1.0 | 0.0 | −49.6 | 5.0 | −41.8 | 6.0 / 19.7 | 2.0 / 5.3 | 0.0 |

The cheaper preset is worse on the same signals: 110 Hz is off by up to 30 ¢, sweep p95 is 29–53 ¢, and non-partial
energy is −22 to −32 dB.

Transients under pitch shift: clicks stay 0.2–0.4 ms wide with −28 to −32 dB pre-echo. The peak drops by up to 5.8 dB
at +12. Drum onsets are within 1.5 ms (median) and 17 ms (worst).

### Formants on the vocal, default preset

| shift | plain: formant shift st | envelope dev dB | RMS Δ dB | compensated: formant shift st | envelope dev dB | RMS Δ dB | f0 median ¢ | f0 gross % |
|---|---|---|---|---|---|---|---|---|
| −12 | −9.5 | 15.2 | −0.03 | −1.5 | 6.3 | +0.47 | 2.9 | 3.3 |
| −5 | −4.0 | 8.3 | −0.09 | −1.0 | 3.8 | −0.82 | 1.8 | 2.7 |
| +3 | +2.5 | 5.5 | −0.14 | +1.0 | 2.7 | −3.15 | 1.9 | 0.0 |
| +7 | +6.5 | 11.9 | −0.17 | +2.0 | 4.8 | −3.53 | 2.4 | 7.9 |
| +12 | +11.0 | 18.4 | −0.36 | +2.5 | 6.5 | −5.38 | 2.7 | 4.1 |

Formant compensation (`set_formant_factor(1, compensate=1)`) works, roughly. The envelope stays within 0.5–2.5 st
instead of moving with the pitch, and the envelope error halves or better. The cheaper preset lands closer, at
−1 to +1 st.

The costs are a level loss that grows with upward shift and more f0 gross errors. Upstream's README says the same: its formant
correction is less sharp than monophonic algorithms such as PSOLA.

### Low tones against block size (cents per input tone, five simultaneous tones)

| config | shift | 55 Hz | 82.4 Hz | 110 Hz | 220 Hz | 440 Hz | non-partial dB |
|---|---|---|---|---|---|---|---|
| cheaper (100 ms) | −5 / +3 | −92.5 / 39.0 | 53.0 / 44.4 | −7.5 / −35.8 | −7.6 / 18.3 | −5.1 / 3.6 | −29 / −20 |
| default (120 ms) | −5 / +3 | −111.4 / 93.0 | −14.4 / 16.4 | 20.5 / −57.7 | 4.3 / 1.9 | 3.8 / 4.4 | −35 / −39 |
| custom 9600/2400 (200 ms) | −5 / +3 | −21.0 / 9.2 | −15.7 / 9.4 | −0.2 / 6.6 | −1.1 / 2.8 | 1.8 / 2.4 | −55 / −48 |
| custom 14400/3600 (300 ms) | −5 / +3 | −6.5 / 13.5 | 3.2 / 3.0 | 9.3 / 0.0 | −0.3 / −0.1 | 0.8 / 0.6 | −63 / −62 |

The full ±12 table is in `results-an_stretch.md`. Bass transposition with the stock presets is out of tune whenever
low partials crowd together. A 300 ms block resolves them, at 2.5× the latency and with more transient smearing.
Anode could pick the block size per region, for example from the region's lowest detected f0 or from a "bass" Flex
setting.

### Tempo following (rate changes every 128-frame block, default preset)

| rate curve | drum onset err median / max ms | click onset err median / max ms | click 90 % width ms |
|---|---|---|---|
| ramp 0.8×→1.25× over the clip | 0.9 / 8.3 | 0.2 / 0.6 | 0.2 |
| wobble 1 ± 0.15× at 0.5 Hz | 1.2 / 5.4 | 0.1 / 1.0 | 0.2 |
| steps 1.0 / 1.33 / 0.8× every second | 0.9 / 10.8 | 0.0 / 0.9 | 0.2 |

Changing the rate per block, which is what the tempo map or Varispeed automation does, has no cost: hits land where the
integrated rate says they should. The stretcher needs no notice of rate changes beyond being fed input `inputLatency`
ahead.

### CPU: % of one core per channel (Accelerate)

These figures are CPU time over output time, divided by channels. The source is 20 s of chord + drums + vocal, in
128-frame blocks, median of three runs after a warm-up.

| config | ch | 1× | 0.5× | 1.25× | 2× | +7 st | +7 st formant | 1.25× −5 st formant |
|---|---|---|---|---|---|---|---|---|
| default | 1 | 0.41 | 0.41 | 0.42 | 0.42 | 0.54 | 0.81 | 0.85 |
| default | 2 | 0.31 | 0.40 | 0.36 | 0.38 | 0.38 | 0.55 | 0.59 |
| default+split | 1 | 0.41 | 0.44 | 0.47 | 0.44 | 0.60 | 0.85 | 0.85 |
| default+split | 2 | 0.34 | 0.37 | 0.38 | 0.37 | 0.39 | 0.55 | 0.57 |
| cheaper | 1 | 0.26 | 0.28 | 0.27 | 0.28 | 0.34 | 0.51 | 0.52 |
| cheaper | 2 | 0.20 | 0.25 | 0.23 | 0.23 | 0.24 | 0.35 | 0.35 |
| cheaper+split | 1 | 0.27 | 0.29 | 0.29 | 0.29 | 0.36 | 0.56 | 0.55 |
| cheaper+split | 2 | 0.21 | 0.24 | 0.25 | 0.25 | 0.25 | 0.35 | 0.37 |

Without Accelerate (Linear's FFT, as on Windows and Linux):

| config | ch | 1× | 0.5× | 1.25× | 2× | +7 st | +7 st formant | 1.25× −5 st formant |
|---|---|---|---|---|---|---|---|---|
| default | 1 | 0.44 | 0.49 | 0.50 | 0.49 | 0.57 | 0.86 | 0.89 |
| default | 2 | 0.36 | 0.42 | 0.42 | 0.42 | 0.42 | 0.57 | 0.62 |
| default+split | 1 | 0.46 | 0.52 | 0.52 | 0.52 | 0.60 | 0.87 | 0.89 |
| default+split | 2 | 0.42 | 0.42 | 0.42 | 0.42 | 0.42 | 0.56 | 0.62 |
| cheaper | 1 | 0.28 | 0.32 | 0.32 | 0.34 | 0.36 | 0.55 | 0.56 |
| cheaper | 2 | 0.23 | 0.27 | 0.26 | 0.26 | 0.27 | 0.35 | 0.39 |
| cheaper+split | 1 | 0.30 | 0.35 | 0.33 | 0.33 | 0.38 | 0.56 | 0.58 |
| cheaper+split | 2 | 0.25 | 0.29 | 0.28 | 0.32 | 0.29 | 0.37 | 0.42 |

Stereo is cheaper per channel than mono: the work shared between channels is paid once. Formant compensation adds
about 50 %. Even the worst case, a mono formant-compensated shift, is under 1 % of a core. 100 stereo Flex tracks at the
default preset would take about 70 % of one core.

### Callback size: µs per `process` call, stereo, 1.25× (Accelerate)

| config | frames | budget µs | mean µs | median µs | p99 µs | p99.9 µs | max µs | p99.9 % of budget |
|---|---|---|---|---|---|---|---|---|
| default | 32 | 667 | 4.7 | 0.2 | 200.4 | 214.8 | 282.3 | 32 |
| default | 64 | 1333 | 9.6 | 0.4 | 209.2 | 213.5 | 2379.3 | 16 |
| default | 128 | 2667 | 18.4 | 0.7 | 210.0 | 214.7 | 247.0 | 8 |
| default | 256 | 5333 | 37.2 | 1.5 | 213.6 | 234.4 | 242.4 | 4 |
| default | 512 | 10667 | 78.2 | 4.2 | 221.0 | 306.9 | 3192.0 | 3 |
| default | 1024 | 21333 | 151.2 | 204.2 | 242.0 | 256.2 | 274.4 | 1 |
| default+split | 32 | 667 | 5.1 | 3.5 | 13.2 | 26.2 | 1413.0 | 4 |
| default+split | 64 | 1333 | 9.9 | 10.6 | 22.0 | 24.2 | 69.5 | 2 |
| default+split | 128 | 2667 | 19.6 | 20.0 | 34.0 | 45.1 | 101.5 | 2 |
| default+split | 256 | 5333 | 38.2 | 37.5 | 57.5 | 74.5 | 111.0 | 1 |
| default+split | 512 | 10667 | 74.3 | 72.5 | 103.0 | 108.4 | 141.5 | 1 |
| default+split | 1024 | 21333 | 158.5 | 157.6 | 178.0 | 212.7 | 1709.9 | 1 |
| cheaper | 32 | 667 | 3.0 | 0.2 | 165.2 | 180.2 | 224.8 | 27 |
| cheaper | 64 | 1333 | 6.2 | 0.4 | 180.1 | 185.2 | 208.9 | 14 |
| cheaper | 128 | 2667 | 12.8 | 0.7 | 183.2 | 187.5 | 1841.0 | 7 |
| cheaper | 256 | 5333 | 24.0 | 1.5 | 182.7 | 189.1 | 209.4 | 4 |
| cheaper | 512 | 10667 | 48.2 | 3.4 | 186.4 | 200.8 | 216.8 | 2 |
| cheaper | 1024 | 21333 | 95.1 | 162.2 | 190.8 | 192.5 | 192.9 | 1 |
| cheaper+split | 32 | 667 | 3.4 | 0.3 | 11.2 | 12.5 | 51.8 | 2 |
| cheaper+split | 64 | 1333 | 6.8 | 8.8 | 11.7 | 19.0 | 49.7 | 1 |
| cheaper+split | 128 | 2667 | 12.5 | 11.8 | 21.0 | 23.4 | 51.6 | 1 |
| cheaper+split | 256 | 5333 | 26.2 | 25.1 | 40.2 | 46.8 | 68.9 | 1 |
| cheaper+split | 512 | 10667 | 50.4 | 46.9 | 71.0 | 76.7 | 82.1 | 1 |
| cheaper+split | 1024 | 21333 | 100.6 | 100.6 | 123.8 | 135.0 | 137.0 | 1 |

Without Accelerate:

| config | frames | budget µs | mean µs | median µs | p99 µs | p99.9 µs | max µs | p99.9 % of budget |
|---|---|---|---|---|---|---|---|---|
| default | 32 | 667 | 5.6 | 0.2 | 242.5 | 256.0 | 517.9 | 38 |
| default | 64 | 1333 | 11.3 | 0.3 | 248.9 | 254.3 | 271.3 | 19 |
| default | 128 | 2667 | 24.3 | 0.6 | 278.4 | 590.7 | 3107.3 | 22 |
| default | 256 | 5333 | 45.3 | 1.2 | 257.9 | 343.1 | 438.6 | 6 |
| default | 512 | 10667 | 86.7 | 2.5 | 251.1 | 276.8 | 286.1 | 3 |
| default | 1024 | 21333 | 174.2 | 240.5 | 272.4 | 313.9 | 322.7 | 1 |
| default+split | 32 | 667 | 5.7 | 3.4 | 17.7 | 28.1 | 88.8 | 4 |
| default+split | 64 | 1333 | 11.3 | 11.0 | 21.4 | 33.5 | 65.6 | 3 |
| default+split | 128 | 2667 | 22.4 | 21.4 | 38.0 | 42.5 | 58.5 | 2 |
| default+split | 256 | 5333 | 45.0 | 43.8 | 70.7 | 77.2 | 98.8 | 1 |
| default+split | 512 | 10667 | 92.6 | 92.1 | 125.4 | 240.7 | 370.5 | 2 |
| default+split | 1024 | 21333 | 179.7 | 181.2 | 198.8 | 201.4 | 222.6 | 1 |
| cheaper | 32 | 667 | 3.5 | 0.2 | 202.2 | 206.8 | 214.6 | 31 |
| cheaper | 64 | 1333 | 7.1 | 0.3 | 205.0 | 211.1 | 216.2 | 16 |
| cheaper | 128 | 2667 | 14.1 | 0.6 | 208.4 | 216.8 | 237.4 | 8 |
| cheaper | 256 | 5333 | 28.2 | 1.2 | 208.2 | 215.2 | 219.0 | 4 |
| cheaper | 512 | 10667 | 57.2 | 2.6 | 218.7 | 224.4 | 231.8 | 2 |
| cheaper | 1024 | 21333 | 113.2 | 196.0 | 216.4 | 218.9 | 238.4 | 1 |
| cheaper+split | 32 | 667 | 3.7 | 0.3 | 15.0 | 15.5 | 37.6 | 2 |
| cheaper+split | 64 | 1333 | 7.8 | 8.9 | 15.9 | 19.6 | 32.1 | 1 |
| cheaper+split | 128 | 2667 | 15.1 | 15.2 | 30.6 | 31.0 | 52.8 | 1 |
| cheaper+split | 256 | 5333 | 29.7 | 30.2 | 48.8 | 51.3 | 55.1 | 1 |
| cheaper+split | 512 | 10667 | 62.1 | 61.5 | 85.6 | 88.8 | 136.8 | 1 |
| cheaper+split | 1024 | 21333 | 119.5 | 121.8 | 134.2 | 147.2 | 147.6 | 1 |

The mean scales linearly with the callback size, so the cost per frame is constant.

- **Without split computation,** the cost arrives in bursts. Most calls take under 1 µs, and once per interval (30 or
  40 ms) one call takes the whole spectral block: about 200–250 µs stereo, which is 15–19 % of a 64-frame budget and
  30–38 % of a 32-frame one. Instances started together hit their blocks on the same callback. About six stereo Flex
  tracks on one audio thread would then overrun a 64-frame callback, even though their average cost is under 1 %.
- **With split computation,** each call does its share. p99.9 is about 30 µs at 64 frames, 2 % of the budget, for
  0–10 % more average CPU and 30 ms (default) or 40 ms (cheaper) more output latency.

## Flex modes: what covers what

| Mode / feature | Engine | Verdict |
|---|---|---|
| Flex Time **Polyphonic** (mixes, pads, chords, full loops) | Stretch, default preset | **Good.** Clean from 0.75× to 1.5×, acceptable at 0.5× and 2× (about −47 dB of artefacts, soft drum attacks). |
| Flex Time **Rhythmic** (rhythm guitar, keys, loops) | Stretch between transient markers, or the slicing engine with stretched gap fill | Good. Stretch keeps clicks sharp, and rate changes per block track exactly. |
| Flex Time **Slicing** (drums, percussive material) | Slicer: cut at transient markers, play each slice at 1×, fade the gaps or overlaps | No stretcher needed; simpler and transient-perfect. |
| Flex Time **Monophonic** (solo voice, bass, wind) | **TD-PSOLA** on pitch marks from the pYIN analysis (§10.2); Stretch as the fallback | Stretch works on voice (f0 within 2–4 ¢), but it smears more than pitch-synchronous methods and detunes low notes. |
| Flex Time **Speed (FX)** | Varispeed resampler: a windowed-sinc or polyphase interpolator over the region | Pitch follows speed by design; no Stretch. |
| Flex Time **Tempophone (FX)** | Granular: fixed-length grains repeated or skipped, crossfaded | Simple in-house DSP; the artefact is the effect. |
| **Flex Pitch** (per-note pitch, drift, vibrato, formant, gain) | **PSOLA** (pitch and formant kept independently, per note) on the pYIN segmentation; Stretch with formant compensation for polyphonic material | Stretch's formant compensation is approximate (0.5–2.5 st residual) and loses 3–6 dB shifting up. |
| Region **transpose** / pitch shift (polyphonic) | Stretch, default preset; a longer block for bass | Good from 220 Hz up (≤ 7 ¢). Below that, choose the block size per region. |
| **Varispeed**, speed only | Stretch (pitch kept) | Good; this is the tempo-following case. |
| **Varispeed**, speed + pitch | Resampler | No Stretch. |
| **Smart Tempo / tempo-following loops / audio quantise** | Stretch, rate driven by the tempo map per block | Good: onsets within about 1 ms (median), 11 ms worst. |
| **Live** pitch shift of an input (an effect) | Not Stretch: 100–150 ms latency | Needs a low-latency effect algorithm (granular or PSOLA) if Anode ships one. |

## Recommendation

1. **Adopt Signalsmith Stretch** as the Flex Time (Polyphonic, Rhythmic), transpose, Varispeed (speed only) and
   Smart Tempo engine.
   - Pin it as here and keep `an_stretch` as the only boundary.
   - Use the **default preset**. The cheaper one saves about 35 % CPU but is about 20 dB worse on tonal material, and
     CPU is not the constraint.
2. **Process Flex regions off the audio callback, in the disk-streaming read-ahead.**
   - Regions are file-backed and the tempo map is known ahead, so latency costs nothing there.
   - Spectral bursts land on worker threads that can absorb them, which matches S2's streaming design.
   - Run Stretch in the callback only where the rate cannot be known ahead (live Varispeed or tempo changes during
     playback). There, use `split` computation, which bounds each call to about 2 % of a 64-frame budget.
3. **Choose the block per region.** Use the default preset normally and 200–300 ms blocks for bass or low-register
   material, chosen from the Flex Pitch / Smart Tempo analysis. `an_stretch_create_custom` already allows this.
4. **Build PSOLA for Monophonic Flex Time and Flex Pitch** on the pYIN pitch marks, with independent formant control.
   Stretch's formant compensation stays the polyphonic fallback, with level make-up gain: about +3 to +5 dB for upward
   shifts, to be calibrated.
5. **Slicing, Speed and Tempophone** are small in-house DSP: a slicer with fades, a varispeed resampler, and a granular
   player.
6. **Re-measure CPU on x86 Windows and Linux in CI.** The non-Accelerate FFT was measured only on ARM here, at 5–20 %
   above Accelerate.

## Reproduce

```bash
engine/spikes/Anode.Spikes.Stretch/run.sh [extra.wav ...]   # builds native + harness, about 2 min on an idle M1 Max
```

The results go to `engine/artifacts/spike-stretch/results-an_stretch.md` and `results-an_stretch_noaccel.md`, and the
renders go to `engine/artifacts/spike-stretch/<signal>/<config>/*.wav`. Pass `--suite latency,quality,cpu,blocks,files`
to the binary to run a subset.

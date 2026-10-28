# Flex algorithms

The seven algorithms behind `IFlexRenderer` (`engine/src/Anode.Dsp/Flex`), from
[docs/06-phase-5.md](../06-phase-5.md), workstream E. `FlexRenderers.Create` makes them; the engine pools one per
algorithm per lane and calls them on the disk thread.

## The algorithms

| Algorithm | Class | How |
|---|---|---|
| Polyphonic | `StretchRenderer` | Signalsmith Stretch (`an_stretch`, default preset: 120 ms blocks every 30 ms), at the warp map's local rate, transposed by the pitch points' cents, formants shifted by their formant cents. |
| Monophonic | `StretchRenderer` | As Polyphonic with a 100 ms block every 25 ms (measured below). |
| Pitch | `StretchRenderer` | As Polyphonic, with formant compensation: formants stay put under the transposition, then move by the formant cents. |
| Slicing | `SlicingRenderer` | Warp points cut slices; each starts at its warped position and plays at 1:1 up to the next slice's first file frame or the next cut. Gaps are silent; a cut ends with a 2 ms fade; a slice that doesn't continue its predecessor seamlessly fades in over 0.5 ms; a slice at exactly 1:1 joins its neighbours untouched. |
| Rhythmic | `SlicingRenderer` | As Slicing; a gap is filled by looping the slice's last 30 ms, each pass crossfaded into the next over 5 ms (equal power). |
| Speed | `SpeedRenderer` | Kaiser-windowed sinc (16 zero crossings each side, β 8.6, 512-step table), its cutoff following the rate when faster than 1:1 (up to 8×). Pitch follows speed exactly. |
| Tempophone | `TempophoneRenderer` | 60 ms Hann grains every 30 ms on a grid from the entry's start; each plays at 1:1, centred on the file frame the warp map gives its centre. |

All of them apply the pitch points' gain at the file frame each output frame plays. The cents and formant cents apply
in the three Stretch modes only (Speed's pitch is its rate; Slicing, Rhythmic and Tempophone play at 1:1).

**Slicing's cuts.** The compiler puts warp points on markers and beats, but also every 10 ms where the tempo ramps,
and a slice per point would chop a ramp into 10 ms fragments. So a warp point cuts only when it is the first in a new
20 ms cell of the entry: markers and beats, which lie further apart, always cut; under a ramp, slices are 20 ms or so.
The rule is local, so a render from any offset finds the same slices.

## Fill-start independence (the Stretch modes)

The C# algorithms are stateless: each output frame is a function of the maps and the file, so any fill start renders
the same samples and `PrimingFrames` is 0.

A phase vocoder is not: its output depends on everything it processed since it started, and a stretcher primed just
before a seek never converges on the stream played from the entry's start (its phases keep the history). So the
Stretch modes restart at fixed anchors:

- **Segments.** With I the stretcher's interval (1,440 frames at 48 kHz for the default preset), segment k covers
  entry offsets [k·64I − 2I, (k + 1)·64I − 2I). Stream k plays it, taking over from stream k − 1 with a 2I (60 ms)
  crossfade at its start. Stream k starts, aligned (`an_stretch_output_seek`, which now also reseeds the random
  engine), 8I + 2I before k·64I: an 8I (240 ms) lead-in that is discarded. Stream 0 starts at offset 0.
- **Fixed chunks.** Every stream is fed in chunks of 128 output frames from its anchor, the rate and pitch of each chunk
  taken from the maps, so a stream (and every output frame) is a function of the entry alone. The engine's call sizes
  don't matter.
- **A seek** starts the one or two streams that cover the offset at their anchors and runs them up to it. Priming is at
  most a segment plus a lead-in and a crossfade: `PrimingFrames` is 74I, 106,560 frames (2.2 s) at 48 kHz for
  Polyphonic and Pitch, 88,800 for Monophonic.
- Two native stretchers per renderer, overlapping during lead-ins and crossfades: about 16 % more work than one.

**Crossfades in phase.** Untransposed, a stretched partial's phase drifts from the file's by 2πf times the frames the
stream has gained or lost on the file. A stream whose lead-in starts on the file frame it would have reached at 1:1
from the entry's start (and then jumps to where the warp map is) carries the same drift as its predecessor, so the two
are in phase and are summed with a raised cosine. Transposed, no lead-in start kept the stretcher's phases together
(the integral of the transposition factor, its inverse and others were tried), so where any pitch point shifts the
pitch around a crossfade, the lead-in starts on the warp map and the crossfade is equal-power.

**Stereo as mid and side.** The stretcher locks each channel's phases to the loudest channel's, but equal channels
still came out up to 4e-4 apart. Stereo is stretched as mid and side, so a centred source gives exactly equal channels
and opposite channels cancel exactly.

**The shim change.** Signalsmith Stretch randomises phases when slowing down beyond 2×, from an engine it keeps
private and never reseeds, so a stream restarted at an anchor would have depended on what the stretcher processed
before. `an_stretch` now supplies the engine (a `std::minstd_rand` per instance, reached through a thread-local
pointer set on each call) and reseeds it in `an_stretch_reset`, `an_stretch_output_seek` and `an_stretch_exact`.

## Without the native library

`FlexRenderers.StretchAvailable` probes `an_stretch` once. When it does not load, `Create` gives Polyphonic,
Monophonic and Pitch the `ReferenceFlexRenderer` (linear interpolation along the warp map): timing is kept, pitch
follows the rate and pitch points are ignored. The other four algorithms are C# and always available. The tests that
need Stretch skip without it. `Directory.Build.targets` copies `*an_*` libraries next to every executable and test
project, and `publish-engine.mjs` builds them and publishes the host with them.

## Measurements

Apple M1 Max (8 performance + 2 efficiency cores), macOS 27.2, .NET 10.0.14 Release, `an_stretch` with Accelerate, at
48 kHz, stereo. Other agents were building and testing on the machine at the same time (load average 30–150): the CPU
table uses process CPU time, best of three, which load inflates less than wall time, but treat it as an upper bound.
Reproduce with `ANODE_DSP_DEMO=1 dotnet test --project engine/tests/Anode.Dsp.Tests -c Release -- --filter-class
"Anode.Dsp.Tests.Flex.FlexDemoTests"`, which writes `engine/artifacts/flex-demo/results.md` and the golden renders
(git-ignored) next to it: every algorithm on a 100 bpm drum loop at 120 and 80 bpm and with a dragged marker, and on a
melody at 80 % speed, transposed up 3 semitones.

### Onsets against the warp map (ms)

Clicks every 250 ms, through a map that slows to 0.8×, speeds to 1.33× and 1.5×, then slows to 0.9× and 0.6×. Slicing
and Rhythmic get a warp point on every other click, as flex markers on transients (they place slices, not frames). An
onset is the first frame within ±20 ms reaching a tenth of the local peak. Tempophone is not measured: its grains
repeat or skip material by design. Tests: ≤ 1 ms median, ≤ 3 ms worst.

| algorithm | onsets | median | worst |
|---|---|---|---|
| Polyphonic | 22 | 0.125 | 0.806 |
| Monophonic | 22 | 0.146 | 0.701 |
| Pitch | 22 | 0.125 | 0.806 |
| Slicing | 12 | 0.000 | 0.021 |
| Rhythmic | 12 | 0.000 | 0.021 |
| Speed | 22 | 0.014 | 0.125 |

### Pitch of transposed tones (cents off the target)

A sine, its frequency measured by phase vocoder over 0.68 s in the middle of the render (which spans a crossfade between
streams). Tests: Stretch ≤ 20 ¢ from 220 Hz up; Speed ≤ 0.5 ¢ off f·rate.

| algorithm | rate, shift | 220 Hz | 440 Hz | 1000 Hz |
|---|---|---|---|---|
| Polyphonic | 1×, +700 ¢ | −4.48 | −1.05 | 1.11 |
| Polyphonic | 1×, −500 ¢ | −1.75 | −0.19 | 3.17 |
| Polyphonic | 0.8×, +300 ¢ | 2.89 | 5.11 | 0.13 |
| Polyphonic | 1.25×, −1200 ¢ | 6.37 | 7.07 | −0.05 |
| Monophonic | 1×, +700 ¢ | 8.21 | 2.28 | 0.05 |
| Monophonic | 1×, −500 ¢ | −2.24 | −0.92 | 0.20 |
| Monophonic | 0.8×, +300 ¢ | 9.46 | 2.95 | 0.23 |
| Monophonic | 1.25×, −1200 ¢ | 4.24 | −3.07 | 0.72 |
| Pitch | 1×, +700 ¢ | −5.14 | 3.06 | 0.65 |
| Pitch | 1×, −500 ¢ | −1.96 | −0.52 | 3.04 |
| Pitch | 0.8×, +300 ¢ | 4.49 | 5.45 | 0.39 |
| Pitch | 1.25×, −1200 ¢ | 6.79 | 7.22 | −0.05 |
| Speed | 0.8× (target f·rate) | 0.000 | 0.000 | 0.000 |
| Speed | 1.25× | 0.000 | 0.000 | 0.000 |
| Speed | 1.5× | 0.000 | 0.000 | 0.000 |

### Formants

A synthesized vowel at 220 Hz (a glottal-like pulse train through resonators at 700, 1,220 and 2,600 Hz), shifted;
the shift of its cepstrally smoothed long-term envelope over 300–3,500 Hz that best realigns it with the input's
(0 = preserved). Test: Pitch within ±2.5 st, Polyphonic within 1.5 st of the shift.

| shift | Pitch (compensated) | Polyphonic |
|---|---|---|
| −700 ¢ | 0.6 | −5.9 |
| −500 ¢ | 1.2 | −4.0 |
| +300 ¢ | −0.7 | 2.5 |
| +500 ¢ | 0.4 | 5.2 |
| +700 ¢ | 0.6 | 5.8 |

### Crossfades between anchored streams

Polyphonic on a steady 440 + 660 Hz tone: the lowest 5 ms RMS inside the crossfades against the median 5 ms RMS
elsewhere (dB; 0 is seamless).

| rate, shift | in-phase lead-in, raised cosine | lead-in on the warp map, equal power |
|---|---|---|
| 1.25×, 0 ¢ | −0.90 (shipped) | −0.07 |
| 0.8×, 0 ¢ | −0.65 (shipped) | −3.53 |
| 1.25×, +300 ¢ | — | −2.70 (shipped) |
| 1×, −500 ¢ | — | −2.25 (shipped) |

A sustained transposed tone can dip by 2–3 dB for 60 ms every 1.92 s. Placing anchors in the quietest frames near
k·64I (still a function of the entry) would hide them on percussive material; it is not done yet.

### Fill-start independence

Every algorithm, on a melody through the ramp map with pitch points (transposition, gain and formant changes):
renders from seven offsets (inside lead-ins and crossfades among them), in 333-frame calls, against one continuous
render in 512-frame calls. Test: within 1e-4; by construction the renders are identical.

### Monophonic's block

Clicks through a 0.8×/1.22×/0.875× map (onsets as above; pre-echo is the energy 50 to 1 ms before each onset against
the 20 ms after it), and the worst pitch error over the four rate and shift cases above per tone.

| block / interval | click onset median / worst ms | pre-echo dB | worst ¢ at 220 Hz | 330 Hz | 110 Hz | priming frames |
|---|---|---|---|---|---|---|
| 120 / 30 ms (Polyphonic) | 0.23 / 5.70 | −30.7 | 6.37 | 6.80 | 33.09 | 106,560 |
| **100 / 25 ms (chosen)** | 0.17 / 3.58 | −31.5 | 9.46 | 16.60 | 69.46 | 88,800 |
| 80 / 20 ms | 0.18 / 0.40 | −29.2 | 22.14 | 7.72 | 39.49 | 71,040 |
| 60 / 15 ms | 0.14 / 0.27 | −31.1 | 27.46 | 17.31 | 31.73 | 53,280 |

Shorter blocks tighten attacks but detune the low notes a monophonic voice or bass plays: from 80 ms down, 220 Hz is
off by more than the 20 ¢ the tests allow. 100 ms keeps everything from 220 Hz within 17 ¢ and still tightens the worst
click from 5.7 to 3.6 ms. Below 200 Hz no Stretch block is in tune (S5 found the same); the PSOLA path S5 recommends
is out of Phase 5's scope.

### CPU (stereo, one core)

20 s of drums and melody at 1.2×, with +300 ¢ in the Stretch modes; process CPU time, best of three, after a warm-up
pass. "64 tracks" is 64 stereo instances against real time.

| algorithm | call frames | frames/s | × real time | 64 tracks, % of a core |
|---|---|---|---|---|
| Polyphonic | 512 | 5,103,036 | 106 | 60 |
| Polyphonic | 16,384 | 5,063,910 | 105 | 61 |
| Monophonic | 512 | 5,052,066 | 105 | 61 |
| Monophonic | 16,384 | 4,996,781 | 104 | 62 |
| Pitch | 512 | 3,747,288 | 78 | 82 |
| Pitch | 16,384 | 3,692,252 | 77 | 83 |
| Slicing | 512 | 209,422,123 | 4,363 | 1.5 |
| Slicing | 16,384 | 214,637,555 | 4,472 | 1.4 |
| Rhythmic | 512 | 213,952,747 | 4,457 | 1.4 |
| Rhythmic | 16,384 | 209,216,799 | 4,359 | 1.5 |
| Speed | 512 | 8,574,805 | 179 | 36 |
| Speed | 16,384 | 8,529,878 | 178 | 36 |
| Tempophone | 512 | 38,410,466 | 800 | 8.0 |
| Tempophone | 16,384 | 165,087,326 | 3,439 | 1.9 |

64 Polyphonic tracks take about 60 % of one core (S5 measured the stretcher alone at 0.38 % of a core per channel for
+7 st; the anchors' overlap adds about 16 %, and the measurement ran under load). Pitch costs a third more: formant
compensation. A seek costs up to 2.2 s of stretching per track, about 21 ms of CPU for Polyphonic.

## Notes for the engine

- **Construction** allocates: the Stretch modes create two native stretchers (a few MB, a few ms) and run them once on
  silence so their buffers are sized; the C# algorithms allocate a source window of a few tens of thousands of frames
  and Speed a shared kernel table. Make them on the control thread.
- **Renders and `Reset`** allocate nothing (tested). Not thread-safe: one thread per renderer at a time.
- **Call size** is free (any number of frames; the output does not depend on it). The source is read in windows of
  16k–80k frames (C#) or 128-frame chunks plus look-ahead (Stretch).
- **`Reset`** when a renderer moves to another entry, or when an entry's maps change: a renderer cannot tell that the
  region it is handed is not the one it was playing. A non-contiguous offset primes on its own.
- **`PrimingFrames`**: 0 for Slicing, Rhythmic, Speed and Tempophone; 74 stretcher intervals for the Stretch modes
  (106,560 at 48 kHz; 88,800 for Monophonic). It is the most a seek reads back, not a fixed cost: a seek just after an
  anchor costs a lead-in.

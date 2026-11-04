# Parity effects

The effects the parity audit of 2026-11-04 found missing (#130, #131, #132; part of #50). Built-ins in
`engine/src/Anode.Dsp/Effects`, listed in `BuiltinEffects` after the T3 ones, against the same `IAudioEffect` contract as
the T1 to T3 suites. Their tests are in `engine/tests/Anode.Dsp.Tests` (`UtilityEffectTests`, and the contract and
golden-render theories, which run over every built-in).

Names describe what an effect does and do not repeat another product's. Two differ from the issue that asked for them:
the issue's Stereo Spread is `anode.frequency-spread`.

## The effects

| Id | Name | Category | Algorithm | Latency |
|---|---|---|---|---:|
| `anode.tuner` | Tuner | Metering | `YinDetector` on the mono sum every 1,024 samples, 25 Hz … 4.5 kHz, the period then measured over as many periods as the window holds; nearest equal-tempered note from Reference A; passes or mutes | 0 |
| `anode.test-oscillator` | Test Oscillator | Utility | Replaces the input: sine, polyBLEP triangle, square and saw, white noise, or pink noise (white through six one-poles two octaves apart, weighted by the root of the period); a repeating log sweep, 20 Hz … 20 kHz; level as peak dBFS; both, left or right | 0 |
| `anode.expander` | Expander | Dynamics | Downward: stereo-linked peak detector, `GainComputer.Expansion` (ratio, soft knee), limited by Range; attack opens, release closes; make-up; mix | 0 |
| `anode.frequency-spread` | Frequency Spread | Utility | The mid through up to 16 log-spaced SVF band-passes summed with alternating signs, added to the left and taken from the right: only the side changes | 0 |
| `anode.scope` | Oscilloscope | Metering | Keeps 4 s of stereo; a trace of 1 ms … 2 s, free or triggered on a rising or falling crossing of Level on the left channel, read on the control thread | 0 |
| `anode.phase-meter` | Phase Meter | Metering | `StereoImage` (correlation and balance over an exponential window, shared with MultiMeter), and a goniometer of the last 66 ms | 0 |

## Channel EQ: dynamic bands and mid/side

`anode.channel-eq` gains two things on top of its eight bands, as parameters after the original ones, so a state or
project saved without them loads as it played. Its golden render did not change.

- **Placement** (`{band}Place`: Stereo, Mid, Side, on all eight bands). A placed band takes left and right to mid and side
  around itself and back. A band on the side leaves the mono sum as it was, within rounding; a low cut there monos the
  bass.
- **Dynamic bands** (`{band}Dyn`, `{band}DynThreshold`, `{band}DynRatio`, `{band}DynRange` on the bells and shelves, and
  `dynAttack`, `dynRelease` for all). A detector hears the input (before the EQ) through a band-pass at the band's
  frequency and Q, or a shelf's low- or high-pass, in the band's placement. Its 30 ms peak goes through
  `GainComputer.Gain` (a 6 dB knee); the reduction, limited by Range, is added to the band's gain: a negative range cuts
  when loud, a positive one boosts. It moves at Attack and Release on the 32-sample control grid, and the band is
  redesigned there (the trapezoidal SVF does not click).

The Linear Phase EQ has neither: a FIR redesigned per control interval is not a dynamic filter, and it keeps the Channel
EQ's first 31 parameter ids, so a setting still moves between the two (the extras are ignored).

The editor's curve follows: a dynamic band's handle gets a ring and the curve moves with what it adds, and once a band is
placed the curve splits, the side's dashed. The focused band's placement and dynamics show under the graph.

| What | Target | Measured (`EqTests`) |
|---|---|---|
| +12 dB bell on Mid, Side, Stereo | 12 dB on its signal, 0 on the other | within 0.02 dB |
| Bands on the side, mono sum | unchanged | residual under −120 dB |
| Low cut on the side (48 dB/oct, 200 Hz), 50 Hz hard left | side gone | under −60 dB |
| Dynamic bell, −10 dBFS tone, −30 dB threshold, 4:1 | −15 dB | within 0.15 dB, and in the display |
| The same at 2:1; Range −6; Range +9 and +24 at 2:1 | −10; −6; +9; +5 | within 0.15 dB |
| Under the threshold; a tone outside the band | no gain change | within 0.15 and 0.05 dB |
| A quiet probe beside a loud trigger | the bell's own curve at the dynamic gain | within 0.3 dB |
| Attack 20 ms | 63 % at one time constant | 52 … 72 % |
| Dynamic band on the mid, all-side input | detector silent, signal untouched | residual under −100 dB |

## Displays

`InsertReading.values`, while a window shows the insert (`IEffectDisplay`, see [T2.md](T2.md)):

| Effect | `values` |
|---|---|
| Channel EQ | 70: each gain band's dynamic gain in dB (low shelf, peaks 1 to 4, high shelf), then the 64-band spectrum |
| Tuner | 3: pitch in Hz (0 while nothing is heard), the note's MIDI number, cents |
| Oscilloscope | 512: 256 left samples, then 256 right; a point of a long trace is the largest sample of its slice, so a click never falls between points |
| Phase Meter | 514: correlation, balance in dB, then 256 pairs of side and mid, newest first |

The readings: the Tuner's is its cents, the Expander's its deepest gain change, the Phase Meter's the correlation; the
Test Oscillator's, the Frequency Spread's and the Oscilloscope's are their peak in dBFS.

## Measured

On an Apple M1 Max, .NET 10.0.14, Release, 48 kHz, against known signals (`UtilityEffectTests`).

| What | Target | Measured |
|---|---|---|
| Tuner, A0 (27.5 Hz) to C8 (4.2 kHz), at offsets to ±49 ¢ | within 0.5 ¢ | passes at every note, on three harmonics below 2.6 kHz and a sine above |
| Test Oscillator, sine at 60 Hz, 1 kHz and 15 kHz | level within 0.02 dB, frequency within 1 ¢ | passes |
| Test Oscillator, saw at 1 kHz | every alias at least 50 dB under the fundamental | passes |
| Test Oscillator, noise | white +3 dB per octave band within 0.1 dB, pink flat within 0.25 dB, 125 Hz … 8 kHz | passes |
| Expander, −30 dBFS tone, −20 dB threshold, 3:1, no knee | 20 dB down within 0.1 dB, with a matching reading | passes; also −28 dB at −34 dBFS, 0 above the threshold, and Range holds |
| Frequency Spread, mono sum at any setting | the input's | residual under −130 dB at 100 %, 12 bands (rounding only) |
| Frequency Spread, a band's centre at 100 % | up to +6 dB one side, gone from the other | 3.5 … 6.1 dB and under −6 dB, alternating band by band |
| Oscilloscope, 480 Hz in a 10 ms trace | starts on the crossing, 4.8 periods | starts within 0.05 of it, a tone stands still to −30 dB between frames |
| Phase Meter, mono, out of phase, quadrature, unrelated | +1, −1, 0, 0 | within 0.02; balance within 0.05 dB |
| Goniometer | mono vertical, out of phase horizontal, one channel at 45° | exact to rounding |

## Editor

- A panel per effect, loaded with the plugin window (`@defer`, through `an-t3-panel`), found by plugin id
  (`ownPanel` in `panel-patterns.ts`).
- The window's reading follows the plugin: correlation −1 … +1 for the Phase Meter and the MultiMeter, cents for the
  Tuner, LUFS only for the Loudness Meter.

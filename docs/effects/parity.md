# Parity effects

The effects the parity audit of 2026-11-04 found missing (#130, #131, #132; part of #50). Built-ins in
`engine/src/Anode.Dsp/Effects`, listed in `BuiltinEffects` after the T3 ones, against the same `IAudioEffect` contract as
the T1 to T3 suites. Their tests are in `engine/tests/Anode.Dsp.Tests` (`UtilityEffectTests`, and the contract and
golden-render theories, which run over every built-in).

Names describe what an effect does and do not repeat another product's. Two differ from the issue that asked for them:
the issue's Stereo Spread is `anode.frequency-spread`, and its Vocal Transformer is `anode.voice-shaper`.

## The effects

| Id | Name | Category | Algorithm | Latency |
|---|---|---|---|---:|
| `anode.tuner` | Tuner | Metering | `YinDetector` on the mono sum every 1,024 samples, 25 Hz … 4.5 kHz, the period then measured over as many periods as the window holds; nearest equal-tempered note from Reference A; passes or mutes | 0 |
| `anode.test-oscillator` | Test Oscillator | Utility | Replaces the input: sine, polyBLEP triangle, square and saw, white noise, or pink noise (white through six one-poles two octaves apart, weighted by the root of the period); a repeating log sweep, 20 Hz … 20 kHz; level as peak dBFS; both, left or right | 0 |
| `anode.expander` | Expander | Dynamics | Downward: stereo-linked peak detector, `GainComputer.Expansion` (ratio, soft knee), limited by Range; attack opens, release closes; make-up; mix | 0 |
| `anode.frequency-spread` | Frequency Spread | Utility | The mid through up to 16 log-spaced SVF band-passes summed with alternating signs, added to the left and taken from the right: only the side changes | 0 |
| `anode.scope` | Oscilloscope | Metering | Keeps 4 s of stereo; a trace of 1 ms … 2 s, free or triggered on a rising or falling crossing of Level on the left channel, read on the control thread | 0 |
| `anode.phase-meter` | Phase Meter | Metering | `StereoImage` (correlation and balance over an exponential window, shared with MultiMeter), and a goniometer of the last 66 ms | 0 |
| `anode.vocoder` | Vocoder | Specialized | 4 … 32 bands of two SVF band-passes, log-spaced; the modulator's level per band, followed at attack and release, sets the same band of the carrier; formant moves the carrier's bank; sibilance adds the modulator above the bank. Sources: input over a saw at Note, input over the sidechain, or sidechain over the input | 0 |
| `anode.voice-shaper` | Voice Shaper | Pitch | `YinDetector` on the input (and the sidechain), TD-PSOLA whose grains are read faster or slower to scale the formants alone; ±24 st, ±12 st; Target Side Chain sings at the sidechain's pitch; the shifted signal's power matched to the dry over 50 ms | 3·⌈rate/80⌉ + 2 (1,802 at 48 kHz) |

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
| Vocoder | 4 … 32: each band's level, dBFS, lowest first |
| Voice Shaper | 3: the pitch heard in Hz (0 while unvoiced), the shift in cents, the sidechain's pitch in Hz (0 without one) |

The readings: the Tuner's is its cents, the Expander's its deepest gain change, the Phase Meter's the correlation, the
Voice Shaper's the pitch it hears in Hz; the Test Oscillator's, the Frequency Spread's, the Oscilloscope's and the
Vocoder's are their peak in dBFS.

## Limits

- The Voice Shaper is TD-PSOLA, made for a voice's harmonics. A pure tone has no energy at the harmonics of the new
  period when that is a whole multiple of the old one: 330 Hz sine, +3 and +7 st at 0.0 dB, +12 st at −68.6 dB, +19 st at
  −66.4 dB, beyond the ±12 dB of the level match. See #187.
- Dynamic bands have no external sidechain: a detector hears the band of the input (#188).

## Sidechain

The Vocoder and the Voice Shaper list `HasSidechain`, so the mixer's insert menu offers Side Chain on them as on the
Compressor and the Noise Gate. A sidechain from a track processed later in the cycle arrives a block late (see
`AudioEngine.Mix.cs`), and with plugin delay compensation it is not delayed to match: that matters little to a vocoder's
envelopes and nothing to a pitch reference.

The Vocoder with a sidechain source and none connected leaves the audio as it is. The Voice Shaper without one transposes
by Pitch; Target Side Chain with a sidechain that goes quiet holds the last interval.

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

`VoiceEffectTests`, on an additive voice (harmonics in phase, falling 3 dB an octave, a resonance at a known frequency):

| What | Target | Measured |
|---|---|---|
| Vocoder, a tone in a band, tone carrier | A·C | −4 … +3 dB (between band centres and neighbours) |
| Vocoder, a tone over white noise at 300 Hz, 1 kHz, 3 kHz | the noise takes the tone's spectrum | centred within 2 semitones, 25 dB over a third of an octave two octaves up and 1.6 down |
| Vocoder, silence after a voice | silence | under −90 dB once the release has run |
| Vocoder, Synth carrier | at Note | a harmonic of A3 within 1 ¢ |
| Vocoder, Formant +12 | the carrier's bank an octave up | an 800 Hz voice peaks at 1.6 kHz within 2 semitones |
| Voice Shaper at rest | a delay of `LatencySamples` | residual under −50 dB |
| Pitch +7, +12, −5, −12, +3.5 st at 150 … 330 Hz | the pitch within 8 ¢, the formant where it was | passes, formant within 2 semitones |
| Formant ±6, ±12 st | the formant moved within 2.2 semitones, the pitch within 8 ¢ | passes |
| Level, +7 st and −4 st formant | as the dry | within 1 dB |
| Target Side Chain, 220 → 330 Hz, 220 → 165 Hz, 196 → 523 Hz (with +12 st) | at the sidechain's pitch within 8 ¢ | passes, the display within 10 ¢ |

### Allocations

None in `Process`, `SetParameter` or `Reset` after `Prepare`, for all of these and for the Channel EQ with every band
dynamic and placed: the contract theory sweeps every parameter while processing, sidechain on and off.

### CPU

One instance at its demo setting (`Support/EffectPresets`), stereo, 64-frame blocks, one core (`Measure_effect_cpu`).
Measured while other work loaded the machine (load average about 170 on 10 cores): the medians are steady, the means and
99th percentiles pessimistic. Rerun on a quiet machine for clean figures.

| Effect | Id | × real time | Median µs | p99 µs | p99 % of the period |
|---|---|---:|---:|---:|---:|
| Channel EQ, plain (its T1 setting) | `anode.channel-eq` | 113 | 5.7 | 23.1 | 1.73 |
| Tuner | `anode.tuner` | 120 | 0.6 | 133.1 | 9.98 |
| Test Oscillator | `anode.test-oscillator` | 1,200 | 1.1 | 1.2 | 0.09 |
| Expander | `anode.expander` | 338 | 2.4 | 3.3 | 0.25 |
| Frequency Spread | `anode.frequency-spread` | 183 | 5.4 | 18.9 | 1.42 |
| Oscilloscope | `anode.scope` | 2,053 | 0.6 | 0.7 | 0.05 |
| Phase Meter | `anode.phase-meter` | 1,397 | 1.0 | 1.0 | 0.08 |
| Vocoder (16 bands) | `anode.vocoder` | 77 | 11.7 | 34.8 | 2.61 |
| Voice Shaper | `anode.voice-shaper` | 27 | 4.0 | 135.7 | 10.18 |

The Tuner's and the Voice Shaper's 99th percentiles are their YIN bursts (three FFTs every 1,024 and 256 samples). The FFT
behind an EQ's or the Oscilloscope's display runs on the host's readings timer, not the audio thread, and only while a
window shows the insert.

## Editor

- A panel per effect, loaded with the plugin window (`@defer`, through `an-t3-panel`), found by plugin id
  (`ownPanel` in `panel-patterns.ts`).
- The window's reading follows the plugin: correlation −1 … +1 for the Phase Meter and the MultiMeter, cents for the
  Tuner, LUFS only for the Loudness Meter.

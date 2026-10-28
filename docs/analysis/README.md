# Audio analysis (`Anode.Dsp.Analysis`)

The analyses behind Flex Time, Flex Pitch, Smart Tempo and Strip Silence ([docs/06-phase-5.md](../06-phase-5.md),
workstream D). All take mono audio (`Downmix.ToMono`), are pure C# with no dependencies (the FFT is
`Anode.Dsp.Metering.SplitFft`), single-threaded and deterministic: the same input gives bit-identical output (a test
checks every entry point).

| Entry point | Algorithm |
|---|---|
| `Transients.Detect(mono, rate, sensitivity = 0.5)` | Spectral flux (SuperFlux-style, log magnitudes, 21 ms Hann frames, 5 ms hop), peak-picked against λ × local mean + δ × max (both fall as sensitivity rises); each peak placed to the sample where the next 2 ms' power most exceeds the previous 10 ms'; releases (next 20 ms under half the previous) rejected; 20 ms minimum gap. |
| `BeatTracker.Track(mono, rate)` | The same flux as an onset envelope → tempo from the autocorrelation (median over 8 s windows, a log-normal prior at 120 bpm, scored with the two-beat and half-beat lags) → local period per 8 s window (±25 %) → Ellis's dynamic programme → beats snapped to transients within 40 ms → tempo = beats over their span; *variable* when the 8-beat median interval moves by more than 4 %; downbeats assume 4/4 and pick the phase with the strongest sub-200 Hz onsets. |
| `PitchTracker.Track(mono, rate, hop)` | pYIN: YIN difference by FFT (21 ms window, 50–2,000 Hz), 100 Beta(2, 18)-weighted thresholds → candidates and voicing probability; Viterbi over 20-cent bins × voiced/unvoiced (35.92 octaves/s, switch 0.01); the voiced pitch is the candidate nearest the bin, moved to the hop's own time. Hops quieter than −70 dBFS (or 30 dB below their window) are unvoiced. |
| `NoteSegmenter.Segment(track, rate)` | Voiced runs, split where the mean of the next 50 ms differs from the previous 50 ms by over 70 ¢ (at its peak), each split placed by least absolute deviation; notes under 60 ms dropped, neighbours within 50 ¢ joined; pitch = median without the first and last 20 ms; confidence = mean voicing. |
| `Silence.Detect(mono, rate, thresholdDb, minTimeMs, preAttackMs, postReleaseMs)` | 10 ms centred RMS gate; runs above the threshold shorter than the minimum time dropped, the rest widened and merged. |
| `Polyphony.IsPolyphonic(mono, rate, track)` (added) | For `FileAnalysis.Polyphonic`. Loud hops mostly unvoiced or unconfident, or (every 40 ms) voiced hops whose pitch does not explain their spectrum (first three harmonics under 5 % of the energy, or under 70 % within 3 % of the first twenty). Chords, noise and drums read as polyphonic. |

## Method

Test material is synthesized by `engine/tests/Anode.Dsp.Tests/Analysis/Material.cs` at 48 kHz, seeded:

- **Clicks:** 3 ms decaying noise bursts.
- **Drum loops:** 8 bars of kicks (a 150 → 50 Hz sine sweep) on beats 1, 3 and the "and" of 3, and hats (differenced noise,
  15 ms decay) on every eighth, accented on the beats. Humanised loops move each eighth by a normal spread of 3.3 ms,
  clipped to ±10 ms, and vary velocity by ±10 %. Ramps run 16 bars, linear in tempo per beat.
- **Tones:** six harmonics at 1/k (or a pure sine), 10 ms attack and release, with vibrato and glides as listed.
- **The phrase:** eight notes from A2 to C5, a few cents off, with vibrato (±25 to ±40 ¢), a glide of two semitones, two
  legato joins, and silences between the others.
- **Noise**, **silence**, and a **C major triad**.

Pitch errors are scored on hops voiced in both the truth and the track, away from each note's first and last 40 ms
(where the up-to-40 ms window straddles the attack or release); voicing F1 is over every hop. Tempo errors are
|found − true| > 0.5 bpm. Speeds are the best of three runs over 60 s of audio, one thread.

Reproduce with `ANODE_ANALYSIS_REPORT=1 dotnet test --project tests/Anode.Dsp.Tests -c Release -- --filter-class
Anode.Dsp.Tests.Analysis.AnalysisReportTests` (from `engine/`), which writes `engine/artifacts/analysis/report.md`.

**Machine:** Apple M1 Max (10 cores), macOS 27.2, .NET 10.0.14, Release, measured 2026-10-28.

## Results

### Accuracy against the targets

| Target | Measured |
|---|---|
| Transients within 3 ms | Clicks and every hit of steady (70–180 bpm) and humanised loops: 100 %, worst 0.19 ms, no false onsets. Notes after silence: worst 0.1 ms. Legato note joins (no attack): 6 and 8 ms late. Noise: only its start. |
| Tempo within 0.5 bpm (steady loops) | 20 of 25 tempi (60–180 bpm in 5s) within 0.02 bpm; humanised loops within 0.03 bpm. |
| Octave error rate | 5 of 25 (20 %): 60 and 65 bpm read as double, 170, 175 and 180 bpm as half. Nothing else (no 3:2 errors). |
| Beats within 10 ms | Every beat of the correctly tempoed loops, humanised loops and ramps: worst 0.04 ms (humanised: against the beats as played). Downbeats all right. Ramps flagged variable, steady loops not. |
| Pitch within 5 ¢ on voiced hops | Phrase: 100 % (median 0.17 ¢, 95th percentile 1.1 ¢, worst 1.6 ¢). Sines and steady harmonic tones from 55 Hz to 1,760 Hz: 100 %. Harmonic tones with ±30 ¢ vibrato: 100 % from A2 up; at A1 (55 Hz) 87 % (95th percentile 6.0 ¢, worst 7.0 ¢), as the 40 ms window spans a fifth of a vibrato cycle. No octave errors. |
| Voicing F1 | Phrase 0.996; tones 0.987–1.000. White noise: 0 % voiced. |
| Note boundaries within 20 ms | Phrase: every start and end within 2.5 ms (half a hop), pitches within 1 ¢ of the truth. |

### Pitch detail

| Material | Scored hops | Median (¢) | 95th pct (¢) | Worst (¢) | Within 5 ¢ | Voicing F1 |
|---|---|---|---|---|---|---|
| Phrase (vibrato, glide, legato, 110–523 Hz) | 530 | 0.17 | 1.11 | 1.57 | 100 % | 0.996 |
| Sine, A1 (55 Hz) | 145 | 0.00 | 0.00 | 0.06 | 100 % | 0.987 |
| Harmonic tone ±30 ¢ vibrato, A1 | 145 | 2.58 | 6.04 | 6.95 | 86.9 % | 1.000 |
| Sine, A2 | 145 | 0.00 | 0.00 | 0.00 | 100 % | 0.997 |
| Harmonic tone ±30 ¢ vibrato, A2 | 145 | 0.73 | 1.71 | 2.05 | 100 % | 0.997 |
| Sine, A4 | 145 | 0.07 | 0.08 | 0.08 | 100 % | 0.997 |
| Harmonic tone ±30 ¢ vibrato, A4 | 145 | 0.49 | 0.80 | 0.90 | 100 % | 0.997 |
| Sine, A5 | 145 | 0.10 | 0.11 | 0.11 | 100 % | 0.997 |
| Harmonic tone ±30 ¢ vibrato, A5 | 145 | 0.51 | 0.86 | 0.98 | 100 % | 0.997 |
| Sine, A6 (1,760 Hz) | 145 | 0.98 | 0.99 | 0.99 | 100 % | 0.997 |
| Harmonic tone ±30 ¢ vibrato, A6 | 145 | 0.62 | 1.81 | 1.89 | 100 % | 0.997 |

### Speed (× real time, one core, 60 s of 48 kHz mono)

| Analysis | Material | × real time |
|---|---|---|
| `Transients.Detect` | drum loop | 377 |
| `Transients.Detect` | melody | 390 |
| `BeatTracker.Track` (includes its transients) | drum loop | 305 |
| `PitchTracker.Track` (5 ms hop) | melody | 54 |
| `NoteSegmenter.Segment` | the melody's pitch track | 35,000 |
| `Polyphony.IsPolyphonic` | the melody and its pitch track | 513 |
| `Silence.Detect` | melody | 10,400 |

Transients and silence clear the 50× target several times over. Memory: the pitch tracker's Viterbi keeps one byte per
state per hop (640 states), about 7.7 MB per minute at a 5 ms hop.

## Notes for callers

- **Hop:** `PitchTracker.Track(mono, rate, (int)Math.Round(rate * 0.005))` (240 at 48 kHz, 220 at 44.1 kHz). The track has
  `ceil(frames / hop)` values; value k is at frame k · hop.
- **`FileAnalysis.Pitch`:** `track.Cents[k]` as `float?`, null where `float.IsNaN`; `FileAnalysis.Hop` = `track.Hop`.
- **`FileAnalysis.Notes`:** `NoteSegment` maps field for field onto `PitchSegment` (start inclusive, end exclusive,
  frames; cents; confidence).
- **`FileAnalysis.Polyphonic`:** `Polyphony.IsPolyphonic(mono, rate, track)`.
- **`FileTempo`:** `new(beat.Bpm, beat.Variable, beat.Beats, beat.Downbeats)`. With no beats found (silence, noise, under
  2 s) `Bpm` is 120, the lists are empty and `Confidence` is 0: keep the project tempo then. A low confidence (under
  about 0.5: few beats on onsets) also means the tempo is a guess.
- **Sensitivity:** 0.5 (the default) finds every hit of the test loops and nothing in noise; 0 keeps only strong
  onsets, 1 also finds hits 30 dB below their neighbours. The beat tracker always uses 0.5.
- **Strip Silence** follows the contract's wording: the *minimum time* drops non-silent runs shorter than it (Logic's
  dialog applies it to the silences). A short click under a long minimum time is therefore dropped.
- **Downbeats** assume 4/4 and that the loudest low-band (kick) beat starts the bar, preferring the first beat.

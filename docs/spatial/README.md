# Spatial audio: panners, object rendering, down-mix, binaural and ADM BWF

Phase 10's spatial DSP (`engine/src/Anode.Dsp/Spatial`) and ADM BWF (`engine/src/Anode.Interchange/Adm`), per
[ADR-0012](../adr/0012-spatial-audio.md) and [docs/11-phase-10.md](../11-phase-10.md). Angles are ADM's: azimuth 0
ahead, **positive to the left**, −180…180; elevation positive up. Every processing class allocates in its constructor
or `Prepare` (off the audio thread) and nothing in its process methods (tests check each with an allocation probe).

## Layouts

`SpeakerLayout.Of(SpeakerFormat)`: the ADM labels and nominal positions of every format, in the ADM/SMPTE channel
order, the same data as `Anode.Contracts.SpeakerLayouts` (a test keeps them equal). Positions follow ITU-R BS.2051
where it defines the system (0+2+0, 0+5+0, 0+7+0, 4+7+0); mono, LCR, quad, 5.0, 7.0, 7.1.2 and 9.1.6 are not BS.2051
systems.

## Surround Panner (`SurroundPanner`)

Logic's 2D panner: the puck's angle and distance from the centre (`diversity`, 1 at the rim), a centre level, an LFE
level, elevation for layouts with height, and a spread for stereo inputs (each channel at angle ± spread/2).

- **Point** P at an angle: a constant-power pan (cos/sin of the fraction of the arc) between the two neighbouring
  ear-layer speakers, going round the circle.
- **Centre level** c (linear): P² = c²·P²(ring with the centre) + (1 − c²)·P²(ring without it); at −144 dB the
  front is a phantom centre between L and R.
- **Elevation** e (0…90, only with an upper layer): ear·cos(πt/2) and upper·sin(πt/2), t = e/30, so 30° (the layer's
  elevation) is entirely in the upper layer's pair; from 30° to 90° the upper share spreads to all upper speakers
  equally (straight up).
- **Diversity** d: G² = d·P² + (1 − d)·A², A the all-speaker spread (every non-LFE speaker equally, the centre
  weighted by c²). Every setting has unit power.
- **LFE**: each input at LFE level / √inputs into a 4th-order Butterworth low-pass at 120 Hz (two biquads), into the
  LFE channel. `Gains` reports the send before the filter.
- **Smoothing**: `Set` computes new target gains; `Process` ramps every gain linearly per sample from the gains at the
  block's start to the targets at its end (one block of glide; `Reset` jumps). The engine sets per block.

Logic differs: its diversity curve and its LFE filter are not documented; its "Center Level" and "LFE Level" are the
same ideas. The puck is 2D only in Logic's surround panner too; heights come from the elevation control (Logic's
Atmos panner is the object panner below).

## Objects: VBAP (`Vbap`) and the renderer (`ObjectRenderer`)

`Vbap` is a port of the EBU ADM Renderer's point-source panner (ITU-R BS.2127 §6.1, `ear.core.point_source`):

- The layout without its LFE, plus extra speakers filling the layers it lacks: for the layers at −30° and +30°, a copy
  of each middle-layer speaker whose azimuth is beyond the layer's widest speaker + 40° (every one when the layer is
  empty), mixed down to that middle-layer speaker. 7.1.4 gets a copy of the whole middle layer at −30°; 7.1.2 gets
  M±135 copies at +30°.
- Virtual speakers at both poles; the convex hull of all of them (brute force over triples, coplanar triangles merged
  into facets); facets touching a pole become virtual n-gons (the pole's gain shared by its ring at 1/√n), the others
  VBAP triplets or BS.2127's quad regions (bilinear in two quadratic axis positions). Gains are normalised for power.
- Stereo: BS.2127's own case, 0+5+0 down-mixed (centre √3/3, surrounds √½), normalised, then 0 dB at the front to
  −3 dB at the back. LCR (not in BS.2127) does the same keeping its centre. Mono takes everything.
- **Size** (`ObjectPosition.Size` 0…1 ↔ ADM `width` = `height` = 360° × size, `depth` 0): BS.2127's polar extent
  panner for a circular extent: the point-source gains of 1652 grid directions (37 rows, as the reference) weighted 1
  inside the cap of radius 180° × size and fading to 0 over 10°, summed and normalised; below an extent of 10° a power
  cross-fade from the point source; the spread uses at least 5°.
- **Distance** is level only (`ObjectPosition.DistanceGain`): 1/distance from the sphere inwards, at most +6 dB (0.5
  and closer), 0 dB at 1. BS.2127 instead ignores distance for level and widens the extent for distances below 1, so a
  scene with distances below 1 renders differently in `ear` (the golden path uses 1).

`ObjectRenderer` renders up to 118 objects (mono or stereo, both channels at the same gains) into the master's layout
with `Vbap` gains times the distance gain. Positions are points of a piecewise-linear gain curve: a position set at
frame f > 0 is reached at f; one set at frame 0 (the per-block default) is reached at the end of the block, so a
position per block glides over the block exactly as an ADM block with interpolation; the gains hold after the last
point; `Reset` makes the next position apply at once. Gains are recomputed only when the position changes.

**Agreement with `ear`** (opt-in tests, `EarPannerTests`): on a grid of 396 directions (azimuth every 10° offset by
2.5°, elevation −60…90 every 15°) for 0+2+0, 0+5+0, 0+7+0 and 4+7+0, every gain within 1e-4 of `ear`'s; sizes 0.02,
0.1, 0.25 and 0.6 at seven directions within 1e-3.

## Down-mix (`Downmixer`)

A gain matrix from a layout to a smaller one, applied by `Process` (replacing its output). Rules per source speaker:

1. A speaker the target has (same label): 1.
2. The LFE: the target's LFE, else **off** (BS.775); the optional `lfeGain` sends it to the front pair at −3 dB each.
3. An upper speaker: the target's nearest upper speaker(s) by azimuth (a tie splits at −3 dB each); with no upper
   layer in the target, folded into the ear layer at **−3 dB** at its azimuth, then the rules below. (No ITU
   recommendation defines height down-mixes, BS.775 stops at 3/2; folding the upper layer into the one below at −3 dB
   is the common convention, and Anode's documented choice.)
4. The centre with no centre in the target: L and R at −3 dB (ITU-R BS.775-3, Table 2).
5. A front speaker (|azimuth| < 90°): the target's front speaker on its side nearest in azimuth at 1; mono −3 dB.
6. A surround (|azimuth| ≥ 90°): the target's surround on its side nearest in azimuth (ties split); with none, the
   front speaker on its side at −3 dB (BS.775; mono −6 dB). When k surrounds of one side and layer fold into one target
   speaker, each is scaled by 1/√k (power-preserving for uncorrelated sides and rears).

So 5.1 → 2.0 is BS.775's L' = L + 0.7071·C + 0.7071·Ls (the LFE dropped), and the golden path's 7.1.4 → 2.0 is

| | L | R | C | LFE | Lss | Rss | Lrs | Rrs | Ltf | Rtf | Ltr | Rtr |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| L' | 1 | 0 | 0.7071 | 0 | 0.5 | 0 | 0.5 | 0 | 0.7071 | 0 | 0.5 | 0 |
| R' | 0 | 1 | 0.7071 | 0 | 0 | 0.5 | 0 | 0.5 | 0 | 0.7071 | 0 | 0.5 |

7.1 → 5.1 puts sides and rears into Ls/Rs at −3 dB each; 7.1.4 → 7.1 top fronts into L/R and top rears into the rears
at −3 dB; 9.1.6 → 7.1.4 splits the top middles into the top fronts and rears at −3 dB and folds the wides into L/R.
`DownmixTests` checks these matrices within 0.1 dB.

## Binaural (`HrtfSet`, `BinauralRenderer`)

**HRTFs:** SADIE II (University of York, release v2-2, **Apache-2.0**, stated on the database page and in each SOFA
file's `License` attribute), subject **D2 (KEMAR)**, 48 kHz, 256 taps. `scripts/build-hrtf.py` downloads
`D2_HRIR_SOFA.zip` from Zenodo by SHA-256, keeps 1982 of the 8802 directions (a 2° azimuth grid on the rings at −75°…75°
every 15°, and the poles), stores the raw HRIR pairs (the interaural delay in them) as 16-bit integers with one scale,
and each ear's delay (the peak of the response low-passed at 1.5 kHz, to a fraction of a sample). The result,
`engine/src/Anode.Dsp/Spatial/Hrtf/sadie2-d2-48k.anhrtf` (2.06 MB, SHA-256 `377b8265…51e85`), is committed with the
licence and a `NOTICE` (source, citation, changes); whatever ships it must keep both beside it. The Server's build and
publish copy all three into `hrtf/` beside it, where binaural monitoring finds them. The format is in
`HrtfSet`'s remarks and the script's header. KEMAR rather than KU100 (D1): D1's interaural delay at 90° is 0.79–0.81 ms
(cross-correlation), at the edge of the exit criterion's 0.5–0.8 ms; D2's is 0.71.

`HrtfSet.Load` reads the file (`InvalidDataException` for anything else); `HrtfSet.Find(folder)` finds the shipped set
from a folder up (development and tests). `ImpulseResponses(az, el, …)` returns a measured direction as measured, else
the three nearest weighted by inverse angular distance, each ear's neighbours shifted to the weighted delay before
summing (so the delay interpolates rather than smearing); allocation-free and thread-safe. At another sample rate the
renderer resamples the responses (windowed sinc).

**Renderer:** each non-LFE speaker of the layout is a virtual speaker at its nominal position, each ear a
`PartitionedConvolver` (the convolution reverb's: uniformly partitioned FFT convolution of 32-tap partitions behind a
direct head), so there is **no added latency** (`LatencySamples` 0; the HRIRs' own ~1.5 ms onset is acoustic). The LFE
goes to both ears unfiltered at 1. `ProcessObject(index, mono, az, el, left, right)` renders an object directly (after
`Process`, which replaces the ears): two convolver pairs per object, the idle pair loaded with the new direction's
responses (interpolated, on the audio thread, allocation-free) when the direction moves more than 1°, and a linear
cross-fade over the block.

Measured (tests, impulse through the renderer, ITD as the lag of the cross-correlation of the two ears low-passed at
1.5 kHz): at 90° left, ILD 17.1 dB and the right ear 0.71 ms later (48 kHz; 44.1 kHz within the same bounds); ±90°
mirror; 0°, 180° and straight up within ±1.5 dB and ±0.1 ms. CPU (Apple M-series, one core, 48 kHz, 256-frame blocks):
the 7.1.4 binaural render 5.9 %, a directly rendered moving object 1.0 %.

## ADM BWF (`AdmWriter`, `AdmReader`)

**Writer** (`AdmWriter.Write(scene, path, bitDepth = 24)`; 24-bit PCM or 32-bit float):

- BW64 per ITU-R BS.2088: `RIFF`/`WAVE`, a 28-byte `JUNK` reserving the `ds64`, `fmt ` (PCM or IEEE float, plain),
  `chna`, `axml`, `data` last. Above 4 GB the header becomes `BW64` with `ds64` (RIFF and data sizes, sample count) in
  place of the `JUNK`, and the 32-bit sizes −1. Audio streams from the sources in 4096-frame blocks (no whole-file
  buffers); the sources (WAV, RF64 or BW64; a RIFF whose data size is 0 or −1 is read to the end) must have the
  scene's rate, channels and length exactly (`ArgumentException`).
- `chna`: one row per track, `ATU_00000001`… in file order, the track format and pack.
- `axml`: BS.2076-2 (`version="ITU-R_BS.2076-2"`) in `ebuCoreMain` (namespace `urn:ebu:metadata-schema:ebuCore_2017`):
  one `audioProgramme` (`APR_1001`) and `audioContent` (`ACO_1001`) referencing every `audioObject` (`AO_1001`…, beds
  first). A bed references BS.2094's pack for its layout (mono `AP_00010001`, stereo `…02`, LCR `…0a`, 5.0 `…0c`, 5.1
  `…03`, 7.1 `…0f`, 7.1.4 `…17`) or, for quad, 7.0, 7.1.2 and 9.1.6, an Anode `DirectSpeakers` pack (`AP_00011001`…)
  of BS.2094's channel formats (BS.2094's own "7.1.2" pack has other speakers). Common definitions are referenced,
  not repeated. An object has its own pack (`AP_00031001`…), a channel format per channel (`AC_00031001`…) with the
  same `audioBlockFormat`s, and stream and track formats. Every track has an `audioTrackUID` (sample rate, bit depth,
  track format, pack).
- Blocks: `rtime` and `duration` in BS.2076-2's sample-exact fractional format (`00:00:00.00960S48000`); polar
  `azimuth`, `elevation`, `distance`; `width` = `height` = 360° × size, `depth` 0; an interpolating block has no
  `jumpPosition` (interpolation over the whole block), a step has `jumpPosition` 1 with `interpolationLength` 0.
  Blocks must be contiguous from frame 0.
- `AdmBlocks.FromPoints(points, frames, rate)` turns automation breakpoints into blocks (for B's `AdmMapping`): a hold
  is one jumping block; a ramp is cut into interpolating blocks of at most 20 ms (960 frames at 48 kHz), each at the
  ramp's position at its end, preceded by a one-frame jump to the ramp's start when the previous block ends elsewhere.

**Reader** (`AdmReader.Read(path, mediaFolder)`): RIFF, RF64 or BW64; the first programme's content's objects (or
every top-level object), nested objects flattened. A `DirectSpeakers` object whose speaker labels (from the common
definitions or its own channel formats' `speakerLabel`; `LFE`/`LFEL` as `LFE1`) are exactly one of Anode's layouts, in
any order, becomes a bed in that layout's order; an `Objects` object with one or two channels and polar positions
becomes an object (its first channel's blocks, times offset by the object's `start`, missing durations to the next
block, size from the larger of width and height). Each bed's and object's tracks are extracted to a WAV in
`mediaFolder` (`01 Bed.wav`, …, at the file's encoding; silent tracks as zeros). Anything else (no `chna`/`axml`,
malformed chunks, unknown references, HOA, Binaural, Matrix, Cartesian positions, layouts Anode lacks) throws
`FormatException`.

**Verification** (opt-in `EarAdmTests`, `python3 scripts/setup-ear.py` first): `ear-render --strict -s 4+7+0` renders
Anode's files with no warning but its notice that BS.2127 standardises BS.2076-1 rendering only: every bed layout with
a stereo object, and the golden path's scene (a 7.1.4 bed of noise and a mono noise object moving linearly from
(30°, 0°) to (−135°, 30°) over 4 s in 20 ms blocks), whose per-speaker RMS over each second matches Anode's render
(the bed passed through, the object through `ObjectRenderer` block by block) within **0.001 dB** (the criterion is 1).
`ear` 2.1.0 reads 32-bit float BW64 as 32-bit integers, so the comparisons use 24-bit files.

## Running the opt-in tests

```sh
python3 scripts/setup-ear.py          # engine/artifacts/ear (gitignored), or set ANODE_EAR to a python with ear
cd engine && dotnet test --project tests/Anode.Dsp.Tests -c Release -- --filter-class Anode.Dsp.Tests.Spatial.EarPannerTests
dotnet test --project tests/Anode.Interchange.Tests -c Release -- --filter-class Anode.Interchange.Tests.Adm.EarAdmTests
```

`ear` (BSD-3-Clause-Clear) 2.1.0 declares numpy~=1.14 and lxml~=4.4, which do not build on Python 3.13; the script
installs current dependencies and `ear` from its pinned commit (`5bb17b4`) by checksum without its declared ones.

## Differences from Logic

- No Dolby Atmos renderer, binaural renderer or master file: objects render by BS.2127's panner, monitoring is
  Anode's binaural render, and the export is ADM BWF, never called an Atmos master (ADR-0008, ADR-0012).
- Logic's Atmos objects are Cartesian (room-centric); Anode's are polar as ADM's (and BS.2127's egocentric panner),
  with distance as level only.
- Logic's binaural render offers near/mid/far distance modes and personalised HRTFs; Anode has one HRTF set and no
  distance modes or head tracking.
- The Surround Panner's curves (diversity, centre, elevation) are Anode's documented ones, not Logic's undocumented
  ones; Ambisonics are out of scope.

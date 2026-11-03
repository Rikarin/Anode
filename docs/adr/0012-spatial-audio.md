# ADR-0012: Surround and immersive audio with ADM speaker layouts, VBAP objects, binaural monitoring and ADM BWF

- **Status:** accepted
- **Date:** 2026-11-03

## Context

Logic mixes in surround (quad to 7.1.4) and in Dolby Atmos (beds plus objects), monitors binaurally, and exports ADM
BWF. ADR-0008 and the plan decided: no Dolby SDKs, renderer or trademarks; object panning plus ADM BWF (ITU-R
BS.2076) export and an in-house binaural renderer with SOFA HRTFs (e.g. SADIE II). Until Phase 10 the engine is
stereo throughout.

## Decision

- **Layouts are ADM speaker layouts.** A channel format is an ordered list of ADM speaker labels with nominal
  positions (ITU-R BS.2051 for the named formats). The engine's buffers carry a strip's channels in that order, so
  rendering, bouncing and ADM export use one vocabulary. Stereo is the two-speaker layout; a stereo project is
  unchanged.
- **Beds and objects.** A strip panned with the Surround Panner feeds a bed (a bus or the master in the project's
  format). A strip in object mode is an object: its mono or stereo signal and its position (azimuth, elevation,
  distance, size) are rendered at the master to the master's layout.
- **Object rendering: VBAP** over the layout's triangulation, with a size spread, modelled on the EBU ADM Renderer's
  point-source panner so that Anode's render and the EBU reference agree (the golden path checks it within 1 dB).
- **Monitoring is not the mix.** Down-mix (ITU-R BS.775) and binaural rendering happen after the master, for the
  device only; bounces and ADM export use the master's layout unless asked otherwise.
- **Binaural:** SADIE II HRTFs, converted offline from SOFA into a compact Anode file (no HDF5 reader in the engine),
  rendered by partitioned FFT convolution per virtual speaker, interpolated between measured directions for objects.
- **ADM BWF:** BW64 with `chna` and `axml` (and `ds64` above 4 GB), beds as `DirectSpeakers`, objects as `Objects`
  with `audioBlockFormat`s from their automation, following the EBU ADM production profile so other tools read it.
  Re-import reads the same subset.
- **Verification:** the EBU ADM Renderer (`ear`) checks Anode's ADM files and renders them for comparison, in opt-in
  tests and the golden path.

## Implementation notes (workstream D)

- **Objects:** a port of BS.2127's point-source panner (`ear`'s `point_source.py`: extra layer speakers, polar virtual
  speakers, convex-hull triplets, quads and virtual n-gons) and of its polar extent panner for size, so Anode's gains
  equal `ear`'s (within 1e-4 on a grid; the golden path's render within 0.001 dB per speaker and second). Distance
  changes the level only (+6 dB at most, inwards); BS.2127 widens the extent instead.
- **HRTFs:** SADIE II subject D2 (KEMAR), Apache-2.0, 1982 directions of the 48 kHz set as a 2 MB `.anhrtf` committed
  with its licence and notice (`scripts/build-hrtf.py` rebuilds it from the SOFA by checksum). Raw HRIRs with per-ear
  delays for interpolation; no minimum-phase step.
- **Binaural** uses the convolution reverb's zero-latency partitioned convolver per ear and speaker; objects can be
  rendered directly with a cross-fade between two convolver pairs.
- **ADM:** BS.2076-2 with sample-exact fractional times; BS.2094 common packs where they match Anode's layouts, Anode
  packs of common channel formats otherwise; no `profileList` (`ear --strict` refuses elements it does not know).
- Details and measurements: [docs/spatial/README.md](../spatial/README.md).

## Consequences

- The engine's graph, metering and bounce become channel-count-aware; the stereo path must not get slower.
- What Anode exports can be rendered by any ADM renderer, but it is not a Dolby Atmos master and is never called one.
- Ambisonics, head tracking and personalised HRTFs are later work.

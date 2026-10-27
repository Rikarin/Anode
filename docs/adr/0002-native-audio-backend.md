# ADR-0002: Audio and MIDI I/O through the `cth_audio` C shim

- **Status:** accepted
- **Date:** 2026-10-27

## Decision

Reuse Cathode's `cth_audio` (CoreAudio HAL IOProc, WASAPI, ALSA, JACK, CoreMIDI, WinMM, ALSA sequencer, FTZ/DAZ) with
its planar ABI and `[UnmanagedCallersOnly]` callbacks. It is **vendored** into `engine/native/cth_audio` unchanged until
it is extracted into a shared package (plan §10.1); changes go to both until then.

## To add for a DAW

Multichannel I/O beyond stereo with per-device channel maps, aggregate devices on macOS, MIDI clock and MTC output,
MIDI 2.0 / UMP where the OS supports it, and the device's I/O latencies for record-delay compensation.

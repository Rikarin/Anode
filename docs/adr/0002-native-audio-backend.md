# ADR-0002: Audio and MIDI I/O through the `cth_audio` C shim

- **Status:** accepted
- **Date:** 2026-10-27

## Decision

Reuse Cathode's `cth_audio` (CoreAudio HAL IOProc, WASAPI, ALSA, JACK, CoreMIDI, WinMM, ALSA sequencer, FTZ/DAZ) with
its planar ABI and `[UnmanagedCallersOnly]` callbacks. It is **vendored** into `engine/native/cth_audio` unchanged until
it is extracted into a shared package (plan §10.1); changes go to both until then.

## Switching device

Settings ▸ Audio ▸ Devices restarts `anode-host` on the new device (`Sidecars.applyAudio` in the desktop app); the Server
reconnects and resends the project. The engine is built for one rate and buffer, so nothing is reopened in place, and
the audio thread is not involved. Two hosts never run side by side to try a device first, because one changing a
device's rate or buffer would disturb the other. Instead the new host has to print its readiness line and stay up past
the callback watchdog; if it does not, a host on the previous settings replaces it. Only settings that worked are saved.
The latency shown is what the driver reports for the open stream (`cth_stream_stats`), not a loopback measurement.

## To add for a DAW

Multichannel I/O beyond stereo with per-device channel maps, aggregate devices on macOS, MIDI clock and MTC output,
MIDI 2.0 / UMP where the OS supports it, and the device's I/O latencies for record-delay compensation.

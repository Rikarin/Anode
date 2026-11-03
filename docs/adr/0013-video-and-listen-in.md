# ADR-0013: Video through the editor's player and an FFmpeg process; listen-in as Opus over a WebSocket

- **Status:** accepted
- **Date:** 2026-11-03

## Context

Phase 10 adds Logic's movie features (a movie window and track locked to the transport, importing and exporting a
movie's audio, scene markers) and a listen-in stream so a tablet in the live room hears the master. The plan named
Electron's `<video>` with transport-locked seeking and FFmpeg (an LGPL build) for audio extraction and remux, and
Opus to remote clients over WebRTC or a WebSocket.

## Decision

- **Playback in the editor.** The editor plays the movie file with the platform's `<video>` element (desktop and LAN
  browsers, the file served with HTTP range requests), seeking to the transport on locate and correcting drift while
  playing with `requestVideoFrameCallback`; the engine never decodes video. Codec support is the platform's.
- **Media work by FFmpeg as a separate process.** `ffmpeg` and `ffprobe` from an LGPL-only build, pinned and fetched by
  checksum, shipped beside the server as unmodified executables and run as child processes. Nothing links against
  FFmpeg, which keeps the LGPL's replaceability obvious and keeps codec crashes out of the server.
- **Listen-in over a WebSocket.** On a LAN a WebSocket is enough and needs no signalling or STUN; the host writes the
  stereo master into a shared-memory ring, the server encodes Opus (48 kHz, 20 ms, 128 kbit/s) and sends each packet
  with its transport sample position; the browser decodes (WebCodecs where available, else a WebAssembly decoder) and
  plays through an AudioWorklet with a small jitter buffer. At most four listeners.

## The FFmpeg build

No one source publishes LGPL-only builds for every platform (the builds linked from ffmpeg.org for macOS, evermeet.cx
and Martin Riedl's, include x264 and are GPL), so the build comes from two, each pinned by URL and SHA-256 in
`scripts/ffmpeg.json` and fetched by `scripts/fetch-ffmpeg.mjs` into `engine/artifacts/ffmpeg/<platform>/`:

| Platform | Source | Version | What is kept |
|---|---|---|---|
| macOS arm64, x64 | conda-forge: `ffmpeg 9.0.2 lgpl_h1d9c18f_5` (arm64), `lgpl_h13b04f9_5` (x64), with the conda-forge packages of the libraries it loads (53 and 50 packages; resolved once with `conda create --dry-run`) | 9.0.2 | `bin/ffmpeg`, `bin/ffprobe`, the `lib/*.dylib` they load (found with `otool -L`), `licenses/<package>/` (about 150 MB) |
| Windows x64, arm64; Linux x64, arm64 | BtbN/FFmpeg-Builds, release `autobuild-2026-10-31-12-59` (month-end auto-builds are kept), `ffmpeg-n9.0.2-39-g7adfbe40dc-<platform>-lgpl-9.0` | 9.0.2-39-g7adfbe40dc | the two static executables, `LICENSE.txt` (about 270 MB) |

Both are configured `--enable-version3` without `--enable-gpl` or `--enable-nonfree` (`ffmpeg -L`: LGPL version 3
or later); the fetch script refuses a build that says otherwise. H.264 encoding, which the test movie needs, comes from
VideoToolbox (`h264_videotoolbox`, macOS) or Cisco's OpenH264 (`libopenh264`, BSD, every build); the macOS build has
`drawtext` (FreeType) but not `libsoxr`, so resampling uses FFmpeg's own `swr`. The conda-forge executables find their
libraries through `@rpath` (`@loader_path/../lib`); a `DYLD_LIBRARY_PATH` (Homebrew's `/opt/homebrew/lib` is common)
would load Homebrew's GPL libraries instead, so `Anode.Media`'s runner and the fetch script start the tools without
`DYLD_*`, `LD_LIBRARY_PATH` and `LD_PRELOAD`. A self-built minimal LGPL FFmpeg for macOS (VideoToolbox and
AudioToolbox only, no third-party libraries, a few MB) would be smaller to ship; it is not done (see
[docs/media](../media/README.md)).

## Consequences

- Movie playback quality and codecs depend on the browser; ProRes may not play in every browser.
- FFmpeg adds 150–270 MB per platform to the package as fetched; its build must be checked to stay LGPL.
- Listen-in latency is a few hundred milliseconds: fine for a live room, not for tracking to it.

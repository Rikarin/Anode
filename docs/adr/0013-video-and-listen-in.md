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

## Consequences

- Movie playback quality and codecs depend on the browser; ProRes may not play in every browser.
- FFmpeg adds tens of MB per platform to the package; its build must be checked to stay LGPL.
- Listen-in latency is a few hundred milliseconds: fine for a live room, not for tracking to it.

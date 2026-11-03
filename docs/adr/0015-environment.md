# ADR-0015: The Environment is a MIDI object graph in the host, edited as a node graph

- **Status:** accepted
- **Date:** 2026-11-03

## Context

Logic's Environment is a patchable MIDI layer between the physical inputs and the tracks (and from tracks to external
MIDI): layers of objects (physical input, keyboards, monitors, faders, transformers, delay lines, arpeggiators, chord
memorisers, instruments, ports) joined by cables. The plan (§11) puts it in Phase 11 as an `@xui/node-graph` editor
and a MIDI object graph in the host. Since Phase 6, Scripter and the MIDI FX run on a strip's MIDI path in the host;
since Phase 8, control surfaces and MIDI learn take their input before the tracks.

## Decision

- **The document is in the project, the graph runs in the host.** The project holds the Environment: layers, objects
  with their parameters and positions, and cables from an object's outlet to another's inlet. The server validates it
  (no cycles, known object kinds, inlets and outlets that exist), and compiles it to the host as a flat, ordered list
  of objects with their routes. The host runs it on the MIDI input path: events from MIDI devices, live MIDI (the
  editor's keyboard and the remote) and control surfaces in, at their sample, through the graph, to the sequencer
  input (the record-enabled or selected track, as today), to a named track's input, to an external MIDI port or to a
  control target.
- **A fixed set of objects**, real-time safe (preallocated queues, no allocation per event, a bounded number of
  events per block): physical input, sequencer input, keyboard (an on-screen source), monitor, fader (a MIDI value
  source or target: CC, note, program, pitch bend; with a range), transformer (Logic's conditions and operations on
  status, channel, data 1 and data 2, reusing Phase 7's MIDI Transform rules where they fit), delay line (taps,
  delay, transpose and velocity per tap), arpeggiator and chord memoriser (the Phase 6 MIDI FX engines), channel
  splitter, MIDI port (out), track input (to a track), and a control target (a strip parameter by address). Anything
  else is Scripter's job; the Environment does not run scripts.
- **No default change.** An empty Environment, which every existing project has, is the Phase 10 input path exactly:
  physical input to sequencer input.
- **What the editor sees:** monitors and faders report through the host's telemetry at the meter rate (the last
  events, the value), never per event over the socket; moving an on-screen fader or playing an on-screen keyboard
  sends live MIDI into that object.
- **Editor:** the Environment window with `@xui/node-graph` (one canvas per layer, objects as nodes, cables as
  edges), its own undo through the server's commands.

## Consequences

- The Environment adds a stage to the MIDI input path; the cost of the empty graph must stay zero.
- Logic's Environment features that need the score or the transport's internals (touch tracks, the mixer layer as
  Environment objects, alias objects) are not in Phase 11.
- Projects store the Environment and interchange does not carry it.

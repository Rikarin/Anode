# ADR-0004: The server owns state; clients receive deltas

- **Status:** accepted
- **Date:** 2026-10-27

## Decision

- The server's project snapshot is the truth. Every mutation is a typed `EditCommand` sent as a batch to
  `POST /api/v1/projects/{id}/commands` with `If-Match: "<revision>"`; one batch is one undo step.
- Applying a command yields the new snapshot, its inverse (for undo) and a **domain delta**
  (`{revision, ops:[{op, entity, id, data}]}`), broadcast on `/ws`.
- Clients apply deltas to normalised signal stores. A client that misses a revision (the hub's `resync`) refetches the
  visible window.
- The host only runs what the server compiles (ADR-0006); it never owns project state.

## Departure from Cathode

Cathode refetches its whole `LiveState` after an event. A DAW project holds up to hundreds of thousands of notes, so
Anode sends deltas instead. Optimistic local previews (knob and fader drags, timeline drags) follow Cathode's pattern.

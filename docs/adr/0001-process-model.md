# ADR-0001: Engine host, server and plugin sandboxes are separate processes

- **Status:** accepted (inherited from Cathode ADR-0001, measured there)
- **Date:** 2026-10-27

## Decision

- **`anode-host`** is the real-time engine, published NativeAOT. Workstation, non-concurrent GC with a large gen0
  budget; zero allocation on the audio path, enforced by tests.
- **`anode-server`** is ASP.NET Core. It owns the domain (projects, undo, library, analysis jobs) and drives the host
  over a Unix domain socket (4-byte little-endian length-prefixed JSON frames) plus shared memory for telemetry and
  bulk data (ADR-0006).
- **`anode-plugin`** processes host third-party plugins (ADR-0003).
- An in-process engine mode (`--Anode:Engine:Mode=inprocess`) exists for tests, UI development and offline renders.

## Why

Cathode measured that a server GC in the audio process is an audible dropout risk under realistic API load; a DAW's
server does more (large projects, analysis jobs), so the separation matters more, not less.

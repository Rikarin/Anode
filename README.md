# Anode

A digital audio workstation in the spirit of Logic Pro: record, arrange, edit, mix and master audio and MIDI. It runs
as a desktop app (Electron) and serves the same editor to any browser or tablet on the local network.

Anode shares its stack and architecture with Cathode: a NativeAOT real-time engine host (`anode-host`), an ASP.NET Core
server that owns the project and drives the host over IPC (`anode-server`), and an Angular + Tailwind + xUI editor. The
full plan is in [docs/01-implementation-plan.md](docs/01-implementation-plan.md); decisions are in [docs/adr](docs/adr).

> Status: beta, Phase 5 on macOS. Records, arranges, edits and mixes audio and MIDI; flexes audio in time and pitch with Smart Tempo and Varispeed; hosts sandboxed plugins and bounces.

## Layout

| Path | What |
|---|---|
| `engine/` | .NET 10 solution (`Anode.slnx`): engine, host, server, contracts, model, tests |
| `engine/native/` | C shims built with CMake into `engine/artifacts/native` (`cth_audio`, vendored from Cathode) |
| `editor/` | Nx + pnpm workspace: `apps/editor` (Angular), `apps/desktop` (Electron), `libs/*` |
| `packaging/` | Headless (systemd) packaging, to come |
| `docs/` | Plan, architecture decisions |

## Build

Prerequisites: .NET SDK 10.0.401 (see `engine/global.json`), CMake ≥ 3.24 and a C compiler, Node 24 and pnpm. On Linux
also `libasound2-dev`.

```bash
./engine/native/build.sh
dotnet build engine/Anode.slnx -c Release
dotnet test --solution engine/Anode.slnx -c Release --max-parallel-test-modules 2
cd editor && pnpm install && pnpm exec nx run-many -t typecheck lint test build
```

## Run

Desktop (spawns the host and server as sidecars):

```bash
cd editor && pnpm exec nx serve desktop
```

Editor in a browser against an in-process engine (no audio):

```bash
dotnet run --project engine/src/Anode.Server -- --Anode:Engine:Mode=inprocess
cd editor && pnpm exec nx serve editor
```

## Licence

GPL-3.0-or-later. See [LICENSE](LICENSE).

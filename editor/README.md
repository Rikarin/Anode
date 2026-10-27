# Anode editor

The desktop editor (../docs/01-implementation-plan.md §6): an Angular 22 renderer styled with [xUI](https://xuijs.org) and Tailwind 4, in an Electron shell that runs the engine (anode-host and anode-server) as sidecars. The same build is served by anode-server to browsers and tablets on the local network.

| Project           | Role                                                                                                                                        |
| ----------------- | ------------------------------------------------------------------------------------------------------------------------------------------- |
| `apps/editor`     | The renderer: project chooser, control bar, Tracks area, Piano Roll, Inspector, Keyboard, Mixer; stores fed by REST and `/ws` deltas        |
| `apps/desktop`    | Electron main (sidecar supervision, `app://` scheme with CSP) and the preload bridge; Playwright E2E in `e2e/`                              |
| `libs/api-client` | Types generated from `engine/src/Anode.Server/openapi.json`, plus a typed `fetch` client                                                    |
| `libs/audio-ui`   | Audio controls xUI does not have (fader, pan knob, knob, choice, on-screen keyboard, peak meter, channel strip), stories that must pass axe |
| `libs/timeline`   | The canvas/WebGL2 timeline engine behind the Tracks area: regions, waveforms, ruler, hit-testing (its README)                               |

## Develop

```bash
pnpm install
```

Run the editor in a browser against a development Server, with the engine host on no device:

```bash
dotnet run -c Release --project ../engine/src/Anode.Host -- --serve --null-audio --socket /tmp/anode-host.sock
```

```bash
dotnet run -c Release --project ../engine/src/Anode.Server -- --Anode:Engine:SocketPath=/tmp/anode-host.sock
```

```bash
pnpm exec nx serve editor
```

Or run the editor against an in-memory fake Server (no engine needed): `pnpm exec nx serve editor`, then open
`http://localhost:4200/?fake` (a demo project) or `?fake&empty` (the project chooser). The fake
(`apps/editor/src/app/core/fake`) follows the Phase 1 and Phase 2 contracts: every command, undo, deltas, transport,
recording on armed tracks (audio takes, and MIDI takes from live MIDI or a few faked notes), meters, synthetic peaks, a
plugin catalogue (`anode.synth`, `anode.sampler`, a CLAP test instrument), MIDI devices, live MIDI with activity, and
the MIDI-thru track. The demo has two instrument tracks with MIDI regions. Unit tests use it too.

Or run the whole desktop app. Build the engine first with `dotnet build -c Release` in `engine/`:

```bash
pnpm exec nx serve desktop
```

## Test

```bash
pnpm exec nx run-many -t test
```

Lint and type-check every project. ESLint is type-aware (`eslint.config.mjs`: typescript-eslint's strict and stylistic type-checked sets, angular-eslint with template accessibility), any warning fails, and CI runs both:

```bash
pnpm exec nx run-many -t lint
```

```bash
pnpm exec nx run-many -t typecheck
```

```bash
pnpm exec nx e2e desktop
```

```bash
pnpm exec nx test-storybook audio-ui
```

After an API change, regenerate the client. CI fails while it is stale:

```bash
pnpm exec nx run api-client:generate
```

## Package

Build the desktop app for this machine's OS into `dist/release`: a `.dmg` and `.zip` on macOS, an NSIS installer on Windows, an AppImage on Linux. It publishes the engine too (NativeAOT host, self-contained server, native shims), so it needs the .NET SDK, CMake and a C compiler:

```bash
pnpm dist
```

NativeAOT does not cross-compile, so each OS builds its own.

## Security model

- **Renderer.** It is sandboxed with context isolation and no Node.
- **Preload.** It exposes only `window.anode` (connection, server target, sidecar status and logs, audio settings, network control, and the platform and full-screen state for the macOS title bar), and main validates every argument.
- **Serving the editor.** It comes from the `app://` scheme with a CSP that allows connections only to the Server in use. Navigation, new windows and permission requests are refused.
- **Sidecars.** They listen on loopback with a random per-run token. A remote Server needs its own token, and the Server refuses to listen beyond localhost without one.

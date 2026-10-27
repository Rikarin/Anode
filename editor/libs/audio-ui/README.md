# @anode/audio-ui

Anode's audio controls: Angular components in xUI's conventions (standalone, OnPush, signal inputs, `cva()` variants
merged with `xui()`, semantic theme tokens only), each with a Storybook story that must pass its axe checks.

Planned (docs/01-implementation-plan.md §6.4): knob, fader with a dB taper, pan and balance, surround panner, 3D object
panner, peak/RMS/LUFS meters, goniometer, spectrum, tuner and an on-screen keyboard. Cathode's knob and meter
(`Cathode/editor/libs/audio-ui`) are the starting point; both are meant to move upstream into xUI as `@xui/knob` and
`@xui/meter`, at which point this library keeps only what is DAW-specific.

```bash
pnpm exec nx test audio-ui            # unit tests (Vitest, jsdom)
pnpm exec nx storybook audio-ui       # Storybook on :4400
pnpm exec nx test-storybook audio-ui  # every story in Chromium, with axe
```

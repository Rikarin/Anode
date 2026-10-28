# Anode editor design system

Anode looks like Logic Pro on a current macOS: graphite panes in a near-black window, chrome with soft vertical
gradients and hairline seams, grouped gradient-bezelled buttons, a glossy LCD, Logic's track palette, LED meters and
metal faders. Dark is the primary theme (`index.html` pins `class="dark"`); every token also has a light value, and a
`.light` / `.dark` class on any element re-themes that subtree.

This file is the contract for new editor UI. Build from the tokens and composites below; never from raw colours.

## Where things live

| What                                                 | Where                                                            |
| ---------------------------------------------------- | ---------------------------------------------------------------- |
| Tokens and composite utilities (the design system)   | `libs/audio-ui/src/styles/anode-theme.css`                       |
| App-wide CSS: imports, xUI chrome (dock, segmented…) | `apps/editor/src/styles.css`                                     |
| Audio controls (fader, knobs, meters, strip, keys)   | `libs/audio-ui/src/lib/*.ts`, each with a Storybook story        |
| The design system rendered (both themes)             | Storybook → _Audio UI / Design system_ (`nx storybook audio-ui`) |
| Timeline colours and shapes (regions, ruler, notes)  | `libs/timeline/src/lib/frame-builder.ts` (themes, `RectStyle`)   |
| Timeline drawing (shader, Canvas2D fallback)         | `libs/timeline/src/lib/render/`                                  |
| Track colours stored by the Server                   | `apps/editor/src/app/state/track-colors.ts`                      |

`anode-theme.css` is imported after xUI's `theme.css` by both the app and the audio-ui Storybook. A new stylesheet that
renders Anode UI must import it the same way.

## Rules

1. **Semantic tokens only.** Use xUI's utilities (`bg-surface`, `text-foreground-muted`, `border-border`…) and the DAW
   ones below (`bg-lcd`, `text-lit`, `bg-meter-high`, `border-hairline`…). No palette classes (`bg-zinc-800`), no hex in
   templates, no `dark:` variants: tokens carry both themes.
2. **Composites before hand-rolled gradients.** A bezelled button is `bezel`; a toolbar is `glass-bar`. If a new
   material is needed twice, add a `@utility` to `anode-theme.css` with values in both theme blocks and document it here.
3. **Declare a new token in both theme blocks, literally** (not `var()` of another theme's value), so nested theme
   scopes work.
4. **Don't change behaviour for looks.** Accessible names, test ids, roles and `aria-pressed` stay as they are; e2e and
   unit tests depend on them.
5. **Contrast:** body text ≥ 4.5:1 on its surface in both themes. Small text on bezels uses `text-foreground`, not
   `text-foreground-muted`. Coloured glyphs (record red, cycle yellow) are icons, not text. Storybook's axe check
   (`nx test-storybook audio-ui`) must stay clean.
6. **Motion:** state changes only, 80–200 ms (`duration-100`, `--duration-press`). `prefers-reduced-motion` is handled
   globally in `anode-theme.css`; don't add animation that would need its own opt-out.

## Typography

- `font-sans` is the system stack: SF Pro on Apple platforms, then `system-ui`, Segoe UI, Roboto. No web fonts. The
  body is 13 px (macOS's standard); dense chrome uses 11–12 px, section labels 10–11 px semibold.
- `font-mono` is `ui-monospace` / SF Mono: code and the timeline's glyph atlas only, never as a "technical" costume.
- Every changing number (positions, dB, bpm, pan, velocity) uses `readout` (or `tabular-nums`) so digits don't jitter.
- Section headers in panes: `text-[10px] font-semibold uppercase tracking-wide text-foreground-muted` (parameter
  groups) or the Inspector's band (`border-y border-hairline bg-surface-raised px-3 py-1 text-[11px] font-bold`).

## Tokens

xUI's own tokens are re-tuned (graphite surfaces, Apple blue `#0071e3` primary, a brighter focus ring) so every xUI
component follows. The surface ladder, darkest first:

| Token (dark)     | Value     | Use                                |
| ---------------- | --------- | ---------------------------------- |
| `background`     | `#111112` | window gutters behind panes        |
| `surface-sunken` | `#171718` | mixer and keyboard wells           |
| `surface-inset`  | `#1a1a1c` | fields, readouts, segmented tracks |
| `surface`        | `#222224` | pane bodies, track headers         |
| `surface-raised` | `#2c2c2f` | bands, raised rows                 |
| `muted`          | `#38383b` | selected track header              |

DAW tokens (each is a CSS variable and, where a utility makes sense, a Tailwind colour):

| Group       | Variables / utilities                                                                                              |
| ----------- | ------------------------------------------------------------------------------------------------------------------ |
| Separators  | `--hairline`, `--hairline-strong` (`border-hairline`, `border-hairline-strong`); `--highlight` (top bevel)         |
| Chrome      | `--chrome-top/bottom` (control bar), `--glass` (translucent bars), `--pane-header-top/bottom`                      |
| Bezels      | `--bezel-top/bottom/border/shadow`, `--bezel-hover-*`, `--bezel-pressed-*`, `--radius-control` (6 px)              |
| Lit state   | `--lit` (`text-lit`): a toggled text control (Click, Count-in, Merge, Musical typing)                              |
| LCD         | `--lcd-top/bottom/border/glare/divider`, `text-lcd-foreground`, `text-lcd-muted`, `text-lcd-accent`                |
| Transport   | `text-transport-play` (green), `text-transport-record` (red), `text-transport-cycle` (yellow)                      |
| Track state | `bg-mute`/`text-mute-foreground` (blue), `solo` (yellow), `arm` (red), `monitor` (orange), each with `-foreground` |
| Meters      | `meter-low` green, `meter-mid` yellow, `meter-high` red, `meter-unlit`, `meter-well`, `meter-peak`                 |
| Fader       | `--fader-groove`, `--fader-cap-top/mid/bottom/line/border`                                                         |
| Knob        | `--knob-top/bottom/rim/shine`, `stroke-knob-arc`, `stroke-knob-track`, `stroke-knob-pointer`                       |
| Strips      | `--strip-top/bottom`, `--strip-selected-top/bottom`                                                                |
| Tracks      | `--track-1` … `--track-12` (`bg-track-N`): Logic's palette, same in both themes; `TrackColors` mirrors it as hex   |

## Composite utilities

| Utility                             | What it draws                                                                                        |
| ----------------------------------- | ---------------------------------------------------------------------------------------------------- |
| `chrome-bar`                        | The control bar: vertical chrome gradient, top highlight, bottom hairline.                           |
| `glass-bar`                         | A toolbar or header over content: translucent `--glass` with `backdrop-filter` blur, hairline under. |
| `bezel`                             | A single raised button face; hover lightens, `:active` / `aria-pressed="true"` sinks it.             |
| `bezel-group`                       | Buttons joined into one capsule split by hairlines (transport, undo/redo, Click/Count-in/Merge).     |
| `lcd-glass`                         | The LCD's glass: gradient, glare over the top half, inset edge, tabular figures.                     |
| `state-toggle` + `toggle-*`         | Logic's M/S/R/I keys; fill with the state colour when `aria-pressed="true"`.                         |
| `strip-body`, `strip-selected`      | A mixer channel strip's graded column; the selected strip is lighter.                                |
| `panel`                             | A free-standing card (project chooser): graded, hairline edge, soft shadow.                          |
| `well`                              | A sunken slot: fader groove, meter.                                                                  |
| `knob-body`                         | A knob's turned-metal body with a shine.                                                             |
| `fader-cap`                         | A fader's metal cap with its index line.                                                             |
| `meter-gradient`, `meter-segments`  | LED meter colours (green to −12 dB, yellow to −6, red near 0) and the 2 px / 1 px segment mask.      |
| `key-white`, `key-black`, `key-lit` | On-screen piano keys; `roll-key-white`, `roll-key-black` for the Piano Roll's sideways keys.         |
| `an-compact-select` (class)         | Lets an `xui-select` shrink below xUI's 11 rem minimum in dense grids.                               |
| `readout`                           | Tabular figures.                                                                                     |

## Component patterns

**Panes.** Panes are xUI dock-manager content; `styles.css` styles its chrome through its documented structure
(`[data-dock-key]` frames, `role="tablist"` strips): 8 px rounded graphite panes with a hairline edge in a near-black
window, graded headers with 11 px semibold titles, and tabs as Logic's editor switcher (quiet labels, the selected one a
small bezelled pill). A pane's own toolbar is a `glass-bar` row, `h-9`, `px-2`, `gap-2`, under the header.

**Control bar.** `chrome-bar`, `h-13`, three columns: project (folder bezel, name, unsaved dot, save/undo/redo
`bezel-group`) | centre (transport `bezel-group` `h-8` with 18 px icons, the LCD, the Click/Count-in/Merge group) |
engine status. Toggles show state with `aria-pressed` (sunken) plus colour: play green, cycle yellow, lit blue for
modes; record is always red.

**LCD.** `lcd-glass`, `h-10`: position (19 px semibold bars-beats-divisions-ticks over clock time) | tempo | signature |
key, separated by `border-(--lcd-divider)`, each value 15 px semibold over a 9 px uppercase label. Editable values are
buttons that turn into transparent inputs. The key is read-only until the global tracks land.

**Buttons.** In chrome: `bezel` or `bezel-group`, heights 26 px (`h-6.5`) or 32 px (transport). In pane content and
forms: xUI buttons (`xuiButton`), which follow the re-tuned tokens. Icons: `@ng-icons/material-icons/round` at `sm`
(16 px) or `18px`.

**Track header.** `bg-surface` (selected `bg-muted`), hairline under, colour bar on the left; number (subtle, tabular),
kind icon, 12 px semibold name; `state-toggle` keys M (`toggle-mute`), S (`toggle-solo`), R (`toggle-arm`), I
(`toggle-monitor`); a thin volume slider with a metal cap (`.an-track-volume`).

**Mixer.** Strips sit edge to edge on `bg-surface-sunken`: `strip-body` columns `w-21`, filling the pane's height. Top to
bottom: pan knob + readout, dB readout, fader (scale left, metal cap) beside the LED meter, M/S `state-toggle`s, name
in an inset box under a 3 px bar of the track colour. The master sits apart on the right.

**Audio controls.** Knobs: `knob-body` under an SVG arc (`stroke-knob-track` 2.25 px track, `stroke-knob-arc` value,
from the centre for bipolar ranges) and a `stroke-knob-pointer` line; value in `readout`, label below. Meters:
`well` slot, `meter-segments` channels with `meter-gradient` under an opaque `bg-meter-unlit` cover scaled from the top
(no change detection), a 2 px `bg-meter-peak` hold segment, a clip LED (`bg-meter-high` with a glow).

**Inspector.** Sections start with the Inspector band (see Typography); fields in a `grid-cols-[5.5rem_1fr]` grid of
muted labels and controls; parameters as knob/choice grids under uppercase group labels.

## Timeline (canvas) visuals

The Tracks area and Piano Roll are drawn by `@anode/timeline` (ADR-0007); their colours are `TimelineTheme`s
(`darkTheme`, `lightTheme`) in `frame-builder.ts`, chosen per scene (`TimelineScene.theme`, from `documentTheme()`, which
reads the root's `color-scheme`). Shapes are `RectStyle`s the WebGL2 fragment shader shades in one instance each, and
the Canvas2D fallback mirrors:

- **Regions** (`RectStyle.Region`): 4 px rounded, a body graded from 0.86× to 0.64× the track colour, a darker name bar
  (0.56×–0.46×, from 22 px tall) with the name in semibold SF Mono (`regionText`), a hairline 0.34× edge and a highlight
  along the top. Waveforms are `waveformColor(base)` (a lighter tint); MIDI regions show their notes as small bars in
  `miniatureColor(base)`. Selected: the colour 1.25× brighter and a white ring (`RectStyle.Selected`). Muted: `muted` grey.
- **Notes** (`RectStyle.Note`): 2 px rounded, velocity-coloured, soft gradient, darker edge; selected with a white ring.
- **Ruler** (`RectStyle.Ruler`): a graded band, bar numbers beside tall ticks, beat ticks at the bottom; the **cycle
  area** (`RectStyle.Cycle`) is a rounded yellow band (`cycle`; grey `cycleOff` when cycling is off).
- **Playhead**: a 1 px line with a small downward triangle in the ruler (`RectStyle.PlayheadHead`), white in dark, near
  black in light.
- Lanes: graphite with a faint alternate, a hairline between tracks; the Piano Roll's keys rows with hairlines and a
  darker line at each octave.

To change a timeline colour, edit both themes in `frame-builder.ts`; to change a shape, change `RegionShade` /
`CornerRadius` (both renderers read them) and the shader branch together. Keep geometry (`RULER_HEIGHT`, lane insets,
`NAME_BAR_HEIGHT`) unchanged unless hit-testing and the e2e lane maths change with it.

## Adding UI: a checklist

- Start from an existing pattern above; reuse its classes verbatim.
- Light and dark: check both (Storybook's theme switch, or `class="light"` on `<html>`).
- States: hover, `focus-visible` (xUI's ring in `--focus`), pressed (`aria-pressed`), disabled (`disabled:opacity-35`
  in bezels), empty (a sentence of guidance in `text-foreground-muted`).
- A new audio control gets a Storybook story with an axe-clean render and a keyboard `play` test.

import type { Meta, StoryObj } from '@storybook/angular-vite';

/**
 * Anode's design system (editor/DESIGN.md) in one place: the composite surfaces from `styles/anode-theme.css` and the
 * DAW tokens, rendered in the active theme (switch it in the toolbar). Build new UI from these, not from raw colours.
 */
const meta: Meta = {
  title: 'Audio UI/Design system',
  tags: ['!autodocs']
};

export default meta;
type Story = StoryObj;

const tracks = Array.from({ length: 12 }, (_, i) => i + 1);

/** The track and region palette (`bg-track-1` … `bg-track-12`), in the order new tracks take it. */
export const TrackPalette: Story = {
  render: () => ({
    props: { tracks },
    template: `
      <ol class="flex flex-wrap gap-3" aria-label="Track palette">
        @for (n of tracks; track n) {
          <li class="flex flex-col items-center gap-1 text-[11px]">
            <span class="size-10 rounded-md shadow-[inset_0_0_0_1px_var(--hairline)]" [style.background]="'var(--track-' + n + ')'"></span>
            <span class="text-foreground-muted readout">track-{{ n }}</span>
          </li>
        }
      </ol>
    `
  })
};

/** Window chrome: the control bar's gradient, a translucent toolbar, bezels, a bezel group and the glossy LCD. */
export const Chrome: Story = {
  render: () => ({
    template: `
      <div class="flex w-[42rem] flex-col gap-4">
        <div class="chrome-bar flex h-13 items-center gap-3 rounded-md px-3">
          <div class="bezel-group text-foreground h-8" role="toolbar" aria-label="Transport">
            <button type="button" class="px-3 text-xs">Start</button>
            <button type="button" class="px-3 text-xs" aria-pressed="true">Play</button>
            <button type="button" class="flex items-center gap-1.5 px-3 text-xs"><span class="bg-transport-record size-2.5 rounded-full" aria-hidden="true"></span>Record</button>
          </div>
          <div class="lcd-glass flex h-10 items-stretch px-1" role="group" aria-label="LCD sample">
            <div class="flex flex-col items-center justify-center px-3 leading-none">
              <span class="text-[19px] font-semibold">12 3 2 120</span>
              <span class="text-lcd-muted mt-1 text-[10px]">00:00:23.456</span>
            </div>
            <div class="flex flex-col items-center justify-center border-s border-(--lcd-divider) px-3 leading-none">
              <span class="text-[15px] font-semibold">112</span>
              <span class="text-lcd-muted mt-1 text-[9px] font-semibold tracking-[0.08em] uppercase">bpm</span>
            </div>
          </div>
          <button type="button" class="bezel text-foreground h-6.5 px-2.5 text-[11px] font-semibold">Bezel</button>
        </div>
        <div class="glass-bar text-foreground-muted flex h-9 items-center rounded-md px-3 text-xs">Glass toolbar</div>
        <div class="panel text-foreground p-4 text-sm">A panel</div>
      </div>
    `
  })
};

/** Track-state keys (Logic's M, S, R, I), off and on. */
export const StateKeys: Story = {
  render: () => ({
    template: `
      <div class="flex flex-col gap-3">
        <div class="flex gap-1">
          <button type="button" class="state-toggle toggle-mute" aria-pressed="false" aria-label="Mute off">M</button>
          <button type="button" class="state-toggle toggle-solo" aria-pressed="false" aria-label="Solo off">S</button>
          <button type="button" class="state-toggle toggle-arm" aria-pressed="false" aria-label="Record enable off">R</button>
          <button type="button" class="state-toggle toggle-monitor" aria-pressed="false" aria-label="Input monitoring off">I</button>
        </div>
        <div class="flex gap-1">
          <button type="button" class="state-toggle toggle-mute" aria-pressed="true" aria-label="Mute on">M</button>
          <button type="button" class="state-toggle toggle-solo" aria-pressed="true" aria-label="Solo on">S</button>
          <button type="button" class="state-toggle toggle-arm" aria-pressed="true" aria-label="Record enable on">R</button>
          <button type="button" class="state-toggle toggle-monitor" aria-pressed="true" aria-label="Input monitoring on">I</button>
        </div>
      </div>
    `
  })
};

/** The meter colours on LED segments, and a knob body and fader cap on their own. */
export const Materials: Story = {
  render: () => ({
    template: `
      <div class="flex items-end gap-6">
        <div class="well flex h-40 w-4 gap-px rounded-[2px] p-px" role="img" aria-label="LED meter at full scale">
          <div class="meter-segments meter-gradient flex-1"></div>
          <div class="meter-segments bg-meter-unlit flex-1"></div>
        </div>
        <span class="knob-body block size-8" role="img" aria-label="Knob body"></span>
        <span class="fader-cap block h-4.5 w-7" role="img" aria-label="Fader cap"></span>
      </div>
    `
  })
};

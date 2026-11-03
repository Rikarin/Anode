import { moduleMetadata, type Meta, type StoryObj } from '@storybook/angular-vite';
import { XuiNodeGraphImports } from '@xui/node-graph';
import { expect, fn, userEvent, within } from 'storybook/test';
import { AnEnvironmentFader, AnEnvironmentMonitor, AnEnvironmentNode } from './environment-node';
import { AnPianoKeyboard } from './piano-keyboard';

/**
 * The Environment window's objects (ADR-0015) as nodes of an `@xui/node-graph` canvas: inlets on the left, outlets on
 * the right, the kind under the name, and the bodies of monitors (their last events), faders (0…127) and on-screen
 * keyboards. The golden path's patch: physical input → transformer → arpeggiator → a track's input, a monitor on the
 * transformer.
 */
const meta: Meta<AnEnvironmentNode> = {
  title: 'Audio UI/Environment/Nodes',
  component: AnEnvironmentNode,
  decorators: [
    moduleMetadata({ imports: [AnEnvironmentFader, AnEnvironmentMonitor, AnPianoKeyboard, XuiNodeGraphImports] })
  ]
};

export default meta;
type Story = StoryObj<AnEnvironmentNode>;

const edges = [
  { id: 'c1', source: { nodeId: 'in', portId: 'out0' }, target: { nodeId: 'tf', portId: 'in0' } },
  { id: 'c2', source: { nodeId: 'tf', portId: 'out0' }, target: { nodeId: 'arp', portId: 'in0' } },
  { id: 'c3', source: { nodeId: 'arp', portId: 'out0' }, target: { nodeId: 'trk', portId: 'in0' } },
  { id: 'c4', source: { nodeId: 'tf', portId: 'out0' }, target: { nodeId: 'mon', portId: 'in0' } }
];

/** A patch on one layer. */
export const Patch: Story = {
  render: () => ({
    props: {
      edges,
      lines: ['Note On C4 100 · 1', 'Note On E4 96 · 1', 'Note Off C4 · 1']
    },
    template: `
      <xui-node-graph class="block h-96 w-[48rem]" background="dots" [edges]="edges" aria-label="Layer 1">
        <an-environment-node nodeId="in" name="Physical Input" kindName="Physical Input" [outlets]="['Out']" [position]="{ x: 20, y: 40 }" accent="var(--track-5)" />
        <an-environment-node nodeId="tf" name="Octave Up" kindName="Transformer" [inlets]="['In']" [outlets]="['Matched', 'Unmatched']" [position]="{ x: 220, y: 40 }" />
        <an-environment-node nodeId="arp" name="Arpeggiator" kindName="Arpeggiator" [inlets]="['In']" [outlets]="['Out']" [position]="{ x: 420, y: 40 }" accent="var(--track-9)" />
        <an-environment-node nodeId="trk" name="Track Input" kindName="Track Input" [inlets]="['In']" [position]="{ x: 600, y: 40 }" accent="var(--track-3)" />
        <an-environment-node nodeId="mon" name="Monitor" kindName="Monitor" [inlets]="['In']" [outlets]="['Out']" [position]="{ x: 420, y: 180 }" accent="var(--track-7)">
          <an-environment-monitor label="Monitor events" [lines]="lines" />
        </an-environment-node>
      </xui-node-graph>
    `
  })
};

/** A monitor before any event arrives. */
export const EmptyMonitor: Story = {
  render: () => ({
    template: `
      <xui-node-graph class="block h-48 w-96" aria-label="Layer 1">
        <an-environment-node nodeId="mon" name="Monitor" kindName="Monitor" [inlets]="['In']" [outlets]="['Out']" [position]="{ x: 20, y: 20 }">
          <an-environment-monitor label="Monitor events" />
        </an-environment-node>
      </xui-node-graph>
    `
  })
};

/** An on-screen keyboard object: what is played on it enters the Environment at its outlet. */
export const Keyboard: Story = {
  render: () => ({
    template: `
      <xui-node-graph class="block h-56 w-[32rem]" aria-label="Layer 1">
        <an-environment-node nodeId="kbd" name="Keyboard" kindName="Keyboard" [outlets]="['Out']" [width]="300" [position]="{ x: 20, y: 20 }" accent="var(--track-1)">
          <div class="text-foreground px-2 pb-2" xuiGraphNoDrag><an-piano-keyboard class="h-16" label="Keyboard keys" [from]="60" [octaves]="1" [keyWidth]="18" /></div>
        </an-environment-node>
      </xui-node-graph>
    `
  })
};

/** A fader object: ←/→ move it by 1, ⇧ by 10, Home/End the ends; each change is sent as it moves. */
export const Fader: Story = {
  args: {},
  render: () => ({
    props: { value: 64, changed: fn(), committed: fn() },
    template: `
      <xui-node-graph class="block h-48 w-96" aria-label="Layer 1">
        <an-environment-node nodeId="fad" name="Volume" kindName="Fader" [inlets]="['In']" [outlets]="['Out']" [position]="{ x: 20, y: 20 }" accent="var(--track-4)">
          <an-environment-fader label="Volume value" [(value)]="value" (valueChange)="changed($event)" (committed)="committed($event)" />
        </an-environment-node>
      </xui-node-graph>
    `
  }),
  play: async ({ canvasElement }) => {
    const slider = within(canvasElement).getByRole('slider', { name: 'Volume value' });
    slider.focus();
    await userEvent.keyboard('{ArrowRight}{ArrowRight}');
    await expect(slider).toHaveAttribute('aria-valuenow', '66');
    await userEvent.keyboard('{Shift>}{ArrowLeft}{/Shift}');
    await expect(slider).toHaveAttribute('aria-valuenow', '56');
    await userEvent.keyboard('{End}');
    await expect(slider).toHaveAttribute('aria-valuenow', '127');
    await userEvent.keyboard('{Home}');
    await expect(slider).toHaveAttribute('aria-valuenow', '0');
  }
};

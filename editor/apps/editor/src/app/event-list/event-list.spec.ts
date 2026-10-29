import { TestBed } from '@angular/core/testing';
import type { MidiRegion } from '@anode/api-client';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TransportStore } from '../state/transport.store';
import { EventList } from './event-list';

describe('Event List', () => {
  async function open() {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Events' }));
    await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: false }], 'Track');
    const trackId = project.trackIds()[0] ?? '';
    await project.execute([{ type: 'addMidiRegion', trackId, startTick: 0, lengthTicks: 3840 }], 'Region');
    const regionId = [...project.midiRegions().keys()][0] ?? '';
    await project.execute(
      [
        {
          type: 'addNotes',
          regionId,
          notes: [
            { pitch: 60, velocity: 100, channel: 1, startTick: 0, lengthTicks: 480 },
            { pitch: 64, velocity: 80, channel: 1, startTick: 960, lengthTicks: 480 }
          ]
        },
        {
          type: 'addControls',
          regionId,
          events: [{ kind: 'pitchBend', channel: 1, number: 0, value: 100, tick: 480 }]
        }
      ],
      'Events'
    );
    TestBed.inject(SelectionStore).selectTrack(trackId);
    const fixture = TestBed.createComponent(EventList);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const region = () => project.midiRegions().get(regionId) as MidiRegion;
    const rows = () =>
      [...element.querySelectorAll('[role="grid"] [role="rowgroup"]:last-child an-event-row')].map(row =>
        [...row.querySelectorAll('[role="gridcell"]')].map(c => c.textContent.trim())
      );
    const byTestId = (id: string) => {
      const found = element.querySelector<HTMLElement>(`[data-testid="${id}"]`);
      if (!found) {
        throw new Error(`No ${id}`);
      }
      return found;
    };
    const stable = async () => {
      await settle();
      await fixture.whenStable();
    };
    return { server, project, fixture, element, region, rows, byTestId, stable, regionId };
  }

  it('lists the events, filters them, and edits a note numerically', async () => {
    const { element, rows, byTestId, region, stable } = await open();
    expect(element.querySelector('[role="grid"]')?.getAttribute('aria-label')).toBe('Events');
    expect([...element.querySelectorAll('[role="columnheader"]')].map(h => h.textContent.trim())).toEqual([
      'Position',
      'Status',
      'Ch',
      'Num',
      'Val',
      'Length',
      'Articulation'
    ]);
    expect(rows().map(r => r[1])).toEqual(['Note', 'Pitch Bend', 'Note']);

    const notes = byTestId('event-filter-notes');
    expect(notes.getAttribute('aria-pressed')).toBe('false');
    notes.click();
    await stable();
    expect(notes.getAttribute('aria-pressed')).toBe('true');
    expect(rows().map(r => r[1])).toEqual(['Note', 'Note']);

    // Position: click the cell, type, Enter.
    const edit = async (row: number, column: string, text: string) => {
      byTestId(`event-row-${row}`).querySelector<HTMLElement>(`[data-column="${column}"]`)?.click();
      await stable();
      const input = byTestId(`event-edit-${column}`) as HTMLInputElement;
      expect(document.activeElement).toBe(input);
      input.value = text;
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      await stable();
    };
    await edit(0, 'position', '1 3 1 1');
    expect(region().notes.find(n => n.pitch === 60)?.startTick).toBe(1920);
    // The edited note moved after the other: it is the second row now.
    expect(rows()[1]?.slice(0, 5)).toEqual(['1 3 1 1', 'Note', '1', 'C3', '100']);
    await edit(1, 'length', '0 0 1 0');
    expect(region().notes.find(n => n.pitch === 60)?.lengthTicks).toBe(240);
    await edit(1, 'val', '42');
    expect(region().notes.find(n => n.pitch === 60)?.velocity).toBe(42);
    await edit(1, 'num', 'G3');
    expect(
      region()
        .notes.map(n => n.pitch)
        .sort()
    ).toEqual([64, 67]);

    // Invalid text: refused, nothing changes.
    const before = region();
    await edit(1, 'val', '300');
    expect(region()).toBe(before);
    expect(byTestId('event-error').textContent).toContain('1 to 127');

    // Escape cancels.
    byTestId('event-row-1').querySelector<HTMLElement>('[data-column="channel"]')?.click();
    await stable();
    const channel = byTestId('event-edit-channel') as HTMLInputElement;
    channel.value = '9';
    channel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await stable();
    expect(element.querySelector('[data-testid="event-edit-channel"]')).toBeNull();
    expect(region().notes.every(n => n.channel === 1)).toBe(true);
  });

  it('inserts at the playhead and at the selection, and deletes in one undo step', async () => {
    const { fixture, project, region, rows, byTestId, stable, element } = await open();
    const list = fixture.componentInstance;
    TestBed.inject(TransportStore).anchorTicks.set(1920);

    // The "+" menu inserts a program change at the playhead; it is then selected.
    byTestId('event-insert').click();
    await stable();
    const item = document.querySelector<HTMLElement>('[data-testid="event-insert-programChange"]');
    expect(item?.getAttribute('role')).toBe('menuitem');
    item?.click();
    await stable();
    const program = region().controls.find(c => c.kind === 'programChange');
    expect(program).toMatchObject({ tick: 1920, value: 0, channel: 1 });
    expect(TestBed.inject(SelectionStore).controls()).toEqual(new Set([program?.id]));

    // A CC 1 at the selected event (the program change).
    await list.insert('controlChange');
    await stable();
    expect(region().controls.find(c => c.kind === 'controlChange')).toMatchObject({ number: 1, tick: 1920 });
    expect(rows().map(r => r[1])).toEqual(['Note', 'Pitch Bend', 'Note', 'Program', 'Control']);

    // Select the CC and the pitch bend (⌘-click) and delete both.
    element.querySelector<HTMLElement>('[data-testid="event-row-4"] [data-column="status"]')?.click();
    byTestId('event-row-1')
      .querySelector<HTMLElement>('[data-column="status"]')
      ?.dispatchEvent(new MouseEvent('click', { bubbles: true, metaKey: true }));
    await stable();
    expect(element.querySelectorAll('an-event-row[aria-selected="true"]').length).toBe(2);
    const revision = project.revision();
    byTestId('event-delete').click();
    await stable();
    expect(project.revision()).toBe(revision + 1);
    expect(region().controls.map(c => c.kind)).toEqual(['programChange']);

    // Delete from the keyboard on a focused cell.
    byTestId('event-row-0').querySelector<HTMLElement>('[data-column="status"]')?.click();
    await stable();
    byTestId('event-row-0')
      .querySelector<HTMLElement>('[data-column="status"]')
      ?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true }));
    await stable();
    expect(region().notes.map(n => n.startTick)).toEqual([960]);
  });

  it('moves the selection with ↑/↓ and edits with Enter', async () => {
    const { byTestId, stable, region } = await open();
    const cell = (row: number, column: string) =>
      byTestId(`event-row-${row}`).querySelector<HTMLElement>(`[data-column="${column}"]`) as HTMLElement;
    cell(0, 'status').click();
    await stable();
    cell(0, 'status').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    await stable();
    expect(byTestId('event-row-1').getAttribute('aria-selected')).toBe('true');
    expect(document.activeElement).toBe(cell(1, 'status'));
    cell(1, 'status').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    expect(document.activeElement).toBe(cell(1, 'channel'));
    cell(1, 'channel').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await stable();
    const input = byTestId('event-edit-channel') as HTMLInputElement;
    input.value = '5';
    input.dispatchEvent(new FocusEvent('blur'));
    await stable();
    expect(region().controls[0]?.channel).toBe(5);
  });

  it('shows and assigns a note’s articulation from the track’s set', async () => {
    const { project, byTestId, stable, region } = await open();
    const trackId = project.trackIds()[0] ?? '';
    await project.execute(
      [
        {
          type: 'setArticulationSet',
          set: {
            id: 'ars_1',
            name: 'Strings',
            articulations: [
              { id: 1, name: 'Legato' },
              { id: 2, name: 'Pizzicato' }
            ],
            switches: [],
            outputs: []
          }
        },
        { type: 'assignArticulationSet', trackId, setId: 'ars_1' }
      ],
      'Articulations'
    );
    await stable();
    byTestId('event-row-0').querySelector<HTMLElement>('[data-column="articulation"]')?.click();
    await stable();
    const select = byTestId('event-edit-articulation') as HTMLSelectElement;
    expect([...select.options].map(o => o.text.trim())).toEqual(['None', 'Legato', 'Pizzicato']);
    select.value = '2';
    select.dispatchEvent(new Event('change'));
    await stable();
    expect(region().notes.find(n => n.startTick === 0)?.articulationId).toBe(2);
    expect(byTestId('event-row-0').querySelector('[data-column="articulation"]')?.textContent.trim()).toBe('Pizzicato');
  });
});

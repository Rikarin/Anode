import { TestBed } from '@angular/core/testing';
import type { Track } from '@anode/api-client';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { MixerStore } from '../state/mixer.store';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { MixerStrip } from './mixer-strip';

async function twoStrips() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  const mixer = TestBed.inject(MixerStore);
  project.load(await server.createProject({ name: 'Inserts' }));
  await project.execute(
    [
      { type: 'addTrack', stereo: true, name: 'Gtr' },
      { type: 'addTrack', stereo: true, name: 'Bass' }
    ],
    'Tracks'
  );
  await TestBed.inject(PluginsStore).refetchPlugins();
  const gtr = () => project.trackList()[0] as Track;
  const bass = () => project.trackList()[1] as Track;
  await mixer.addInsert(gtr().id, 'anode.compressor');
  const insert = gtr().strip.inserts[0];
  if (!insert) {
    throw new Error('no insert');
  }
  await project.execute([{ type: 'setInsert', trackId: gtr().id, insertId: insert.id, bypassed: true }], 'Bypass');
  return { server, project, mixer, gtr, bass, id: insert.id };
}

/** A strip's slot wrappers, and a drag event of a kind on one of them. */
function dragEvent(type: string, altKey = false): DragEvent {
  const event = new Event(type, { bubbles: true, cancelable: true }) as DragEvent;
  Object.defineProperty(event, 'altKey', { value: altKey });
  return event;
}

function slotWrap(element: HTMLElement, index: number): HTMLElement {
  const slot = element.querySelectorAll('an-insert-slot')[index]?.parentElement;
  if (!slot) {
    throw new Error(`no slot ${index}`);
  }
  return slot;
}

describe('moving and copying inserts between strips', () => {
  it('moves one to another strip with its settings, and undoes', async () => {
    const { project, mixer, gtr, bass, id } = await twoStrips();
    await mixer.transferInsert(gtr().id, id, bass().id, null, false);
    expect(gtr().strip.inserts).toEqual([]);
    expect(bass().strip.inserts[0]).toMatchObject({ id, bypassed: true });
    await project.undo();
    expect(gtr().strip.inserts[0]?.id).toBe(id);
    expect(bass().strip.inserts).toEqual([]);
  });

  it('copies one under its own id, leaving the original', async () => {
    const { mixer, gtr, bass, id } = await twoStrips();
    await mixer.transferInsert(gtr().id, id, bass().id, 0, true);
    expect(gtr().strip.inserts[0]?.id).toBe(id);
    const copy = bass().strip.inserts[0];
    expect(copy).toMatchObject({ pluginId: 'anode.compressor', bypassed: true });
    expect(copy?.id).not.toBe(id);
  });

  it('takes its automation along on a move and leaves it on a copy', async () => {
    const { project, mixer, gtr, bass, id } = await twoStrips();
    const target = `insert:${id}:threshold`;
    await project.execute(
      [{ type: 'setAutomation', trackId: gtr().id, target, points: [{ tick: 0, value: -20, curve: 0 }] }],
      'Automation'
    );
    await mixer.transferInsert(gtr().id, id, bass().id, null, true);
    expect(project.automationList().map(l => l.trackId)).toEqual([gtr().id]);
    await mixer.transferInsert(gtr().id, id, bass().id, null, false);
    expect(project.automationList().map(l => l.trackId)).toEqual([bass().id]);
  });

  it('moves on a drop on another strip and copies with option held', async () => {
    const { gtr, bass, id } = await twoStrips();
    const from = TestBed.createComponent(MixerStrip);
    from.componentRef.setInput('track', gtr());
    await from.whenStable();
    const to = TestBed.createComponent(MixerStrip);
    to.componentRef.setInput('track', bass());
    await to.whenStable();
    const source = from.nativeElement as HTMLElement;
    const target = to.nativeElement as HTMLElement;

    slotWrap(source, 0).dispatchEvent(dragEvent('dragstart'));
    const over = dragEvent('dragover');
    slotWrap(target, 0).dispatchEvent(over);
    expect(over.defaultPrevented).toBe(true);
    slotWrap(target, 0).dispatchEvent(dragEvent('drop', true));
    await settle();
    expect(gtr().strip.inserts.map(i => i.id)).toEqual([id]);
    expect(bass().strip.inserts).toHaveLength(1);

    slotWrap(source, 0).dispatchEvent(dragEvent('dragstart'));
    slotWrap(target, 0).dispatchEvent(dragEvent('drop'));
    await settle();
    expect(gtr().strip.inserts).toEqual([]);
    expect(bass().strip.inserts.map(i => i.id)).toContain(id);
  });

  it('refuses a full strip and a strip without inserts, as the Server does', async () => {
    const { server, project, gtr, bass, id } = await twoStrips();
    await project.execute(
      Array.from(
        { length: 15 },
        () => ({ type: 'addInsert', trackId: bass().id, pluginId: 'anode.compressor' }) as const
      ),
      'Fill'
    );
    await expect(
      server.applyCommands({
        commands: [{ type: 'transferInsert', insertId: id, trackId: gtr().id, toTrackId: bass().id, copy: false }]
      })
    ).rejects.toThrow('at most 15');
    await server.applyCommands({ commands: [{ type: 'addVca', name: 'V' }] });
    await expect(
      server.applyCommands({
        commands: [
          {
            type: 'transferInsert',
            insertId: id,
            trackId: gtr().id,
            toTrackId: server.current?.tracks.at(-1)?.id ?? null,
            copy: false
          }
        ]
      })
    ).rejects.toThrow('takes no inserts');
  });
});

import { TestBed } from '@angular/core/testing';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { HistoryStore } from '../state/history.store';
import { ProjectStore } from '../state/project.store';
import { formatOffset, parseOffset, SyncStore } from './sync.store';

async function setUp() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  const sync = TestBed.inject(SyncStore);
  project.load(await server.createProject({ name: 'Sync' }));
  TestBed.tick();
  await settle();
  return { server, project, sync };
}

describe('SyncStore', () => {
  it('reads and writes timecode offsets', () => {
    expect(formatOffset(0)).toBe('00:00:00:00');
    expect(formatOffset(3600 + 2 * 60 + 3)).toBe('01:02:03:00');
    expect(parseOffset('01:00:00:00')).toBe(3600);
    expect(parseOffset('1:00:00')).toBe(3600);
    expect(parseOffset('90')).toBe(90);
    expect(parseOffset('00:61:00')).toBeNull();
    expect(parseOffset('24:00:00')).toBeNull();
    expect(parseOffset('ten')).toBeNull();
  });

  it('starts internal and locked, from the live state', async () => {
    const { sync } = await setUp();
    expect(sync.source()).toBe('internal');
    expect(sync.state()).toEqual({ source: 'internal', locked: true, externalTempo: null, linkPeers: 0 });
    expect(sync.locked()).toBe(true);
  });

  it('applies settings as undoable edits, and follows sync events', async () => {
    const { server, sync } = await setUp();
    await sync.setSource('midiClock');
    await sync.apply({ clockInPort: 'anode:in' });
    await settle();
    expect(sync.settings()).toEqual(expect.objectContaining({ source: 'midiClock', clockInPort: 'anode:in' }));
    expect(sync.locked()).toBe(false);
    server.setExternalSync(true, 100);
    await settle();
    expect(sync.locked()).toBe(true);
    expect(sync.externalTempo()).toBe(100);

    await sync.addDestination('anode:out');
    await sync.setDestination('anode:out', { mtc: true, delayMs: -2.5 });
    expect(sync.settings().destinations).toEqual([{ port: 'anode:out', clock: true, mtc: true, delayMs: -2.5 }]);
    await sync.removeDestination('anode:out');
    expect(sync.settings().destinations).toEqual([]);

    await TestBed.inject(HistoryStore).undo();
    expect(sync.settings().destinations).toHaveLength(1);
  });

  it('turns Link on and off from the control bar, with its peers', async () => {
    const { server, sync } = await setUp();
    await sync.toggleLink();
    expect(sync.linkOn()).toBe(true);
    server.setLinkPeers(3);
    await settle();
    expect(sync.linkPeers()).toBe(3);
    await sync.toggleLink();
    expect(sync.source()).toBe('internal');
    expect(sync.linkPeers()).toBe(0);
  });
});

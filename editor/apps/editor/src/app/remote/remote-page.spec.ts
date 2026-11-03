import { TestBed } from '@angular/core/testing';
import type { Track } from '@anode/api-client';
import { appRoutes } from '../app.routes';
import type { FakeListenInSocket } from '../core/fake/fake-phase10';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TransportStore } from '../state/transport.store';
import { ListenInStore } from './listen-in/listen-in.store';
import type { DirectPlayer } from './listen-in/player';
import { RemotePadsStore } from './pads.store';
import {
  addPad,
  bankCount,
  bankOf,
  bankOfIndex,
  drumPadsOf,
  movePad,
  newSetId,
  removePad,
  updatePad,
  withoutSet,
  withSet
} from './remote-model';
import { RemotePage } from './remote-page';

describe('the remote model', () => {
  it('cuts the tracks into banks of 8', () => {
    const items = Array.from({ length: 19 }, (_, i) => i);
    expect(bankCount(19)).toBe(3);
    expect(bankCount(0)).toBe(1);
    expect(bankOf(items, 1)).toMatchObject({ bank: 1, first: 9, last: 16, items: [8, 9, 10, 11, 12, 13, 14, 15] });
    expect(bankOf(items, 9)).toMatchObject({ bank: 2, first: 17, last: 19 });
    expect(bankOfIndex(8)).toBe(1);
    expect(bankOfIndex(-1)).toBe(0);
  });

  it('plays a Drum Machine Designer’s pads, else the GM ones', () => {
    expect(drumPadsOf(null).map(p => p.note)).toEqual(Array.from({ length: 16 }, (_, i) => 36 + i));
    const kit = { drumMachine: { pads: [{ note: 38, name: 'Snare', trackId: 't2' }] } } as unknown as Track;
    expect(drumPadsOf(kit)).toEqual([{ note: 38, name: 'Snare', color: null }]);
  });

  it('edits pad sets', () => {
    const set = { id: 'a', name: 'A', pads: [] };
    const one = addPad(addPad(set, 'transport.play', 'Play'), 'edit.undo', 'Undo');
    expect(one.pads.map(p => [p.id, p.commandId])).toEqual([
      ['pad1', 'transport.play'],
      ['pad2', 'edit.undo']
    ]);
    expect(movePad(one, 'pad2', -1).pads.map(p => p.id)).toEqual(['pad2', 'pad1']);
    expect(movePad(one, 'pad1', -1)).toBe(one);
    expect(updatePad(one, 'pad1', { label: 'Go' }).pads[0]?.label).toBe('Go');
    expect(removePad(one, 'pad1').pads.map(p => p.id)).toEqual(['pad2']);
    const pads = withSet({ sets: [] }, one);
    expect(newSetId(pads)).toBe('set2');
    expect(withoutSet(pads, 'a').sets).toEqual([]);
  });
});

describe('the remote route', () => {
  it('is its own lazily loaded route', async () => {
    const route = appRoutes.find(r => r.path === 'remote');
    expect(route?.loadComponent).toBeDefined();
    expect(await route?.loadComponent?.()).toBe(RemotePage);
  });

  async function open(tracks = 0, kind: Track['kind'] = 'instrument') {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    await server.createProject({ name: 'Live Room' });
    await settle();
    for (let i = 0; i < tracks; i++) {
      await project.execute([{ type: 'addTrack', kind, stereo: true }], 'New Track');
    }
    const fixture = TestBed.createComponent(RemotePage);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const query = (selector: string) => element.querySelector<HTMLElement>(selector);
    const stable = async () => {
      await settle();
      await fixture.whenStable();
    };
    const tab = async (name: string) => {
      query(`[data-testid="remote-tab-${name}"]`)?.click();
      await stable();
    };
    return { server, project, fixture, element, query, stable, tab };
  }

  it('says when no project is open', async () => {
    setUpFakeServer();
    const fixture = TestBed.createComponent(RemotePage);
    fixture.detectChanges();
    await settle();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.getAttribute('data-testid')).toBe('remote');
    expect(element.querySelector('[data-testid="remote-no-project"]')).not.toBeNull();
  });

  it('shows the project, the Server, the transport and the views; tabs below on a phone, a track column on a tablet', async () => {
    const { query, element, tab, stable } = await open(2);
    await stable();
    expect(query('[data-testid="remote-project"]')?.textContent.trim()).toBe('Live Room');
    expect(query('[data-testid="remote-server"]')?.textContent).toContain('localhost:5190');
    const tabs = [...element.querySelectorAll('[role="tab"]')].map(t => t.textContent.trim());
    expect(tabs).toEqual(['Mixer', 'Smart', 'Loops', 'Play', 'Pads', 'Steps', 'Tracks']);
    const nav = query('[role="tablist"]');
    // Phones: the tab bar is last (the bottom); landscape tablets put it first and show the track column.
    expect(nav?.className).toContain('order-last');
    expect(nav?.className).toContain('md:landscape:order-none');
    expect(query('[data-testid="remote-track-column"]')?.className).toContain('md:landscape:flex');
    await tab('tracks');
    expect(element.querySelectorAll('main [role="option"]')).toHaveLength(2);
  });

  it('plays and stops from the transport', async () => {
    const { server, query, stable } = await open(1);
    query('[data-testid="remote-play"]')?.click();
    await stable();
    expect((await server.transport()).state).toBe('playing');
    expect(query('[data-testid="remote-play"]')?.getAttribute('aria-label')).toBe('Stop');
    query('[data-testid="remote-play"]')?.click();
    await stable();
    expect((await server.transport()).state).toBe('stopped');
  });

  it('shows the mixer in banks of 8 that follow the selection; a fader moves the track on the Server', async () => {
    const { server, project, query, element, stable } = await open(10);
    const strips = () => [...element.querySelectorAll('[data-testid="remote-strip"]')];
    expect(strips()).toHaveLength(8);
    expect(query('[data-testid="bank-label"]')?.textContent.trim()).toBe('Tracks 1–8 of 10');
    expect(query('[data-testid="bank-previous"]')?.hasAttribute('disabled')).toBe(true);
    query('[data-testid="bank-next"]')?.click();
    await stable();
    expect(strips()).toHaveLength(2);
    TestBed.inject(SelectionStore).selectTrack(project.trackIds()[0] ?? '');
    await stable();
    expect(query('[data-testid="bank-label"]')?.textContent.trim()).toBe('Tracks 1–8 of 10');
    const fader = strips()[0]?.querySelector<HTMLElement>('an-fader [role="slider"]');
    fader?.focus();
    fader?.dispatchEvent(new KeyboardEvent('keydown', { key: 'PageDown', bubbles: true }));
    await stable();
    await new Promise(resolve => setTimeout(resolve, 50));
    await stable();
    expect(server.current?.tracks[0]?.strip.volumeDb).toBe(-6);
    strips()[0]?.querySelector<HTMLButtonElement>('[aria-label^="Mute"]')?.click();
    strips()[0]?.querySelector<HTMLButtonElement>('[aria-label^="Record enable"]')?.click();
    await stable();
    expect(server.current?.tracks[0]?.strip.mute).toBe(true);
    expect(server.current?.tracks[0]?.recordArmed).toBe(true);
  });

  it('plays chords on the touch keyboard and the GM pads as live MIDI on the selected track', async () => {
    const { server, project, query, tab, stable } = await open(1);
    const trackId = project.trackIds()[0] ?? '';
    TestBed.inject(SelectionStore).selectTrack(trackId);
    await tab('play');
    expect(query('[data-testid="play-target"]')?.textContent).toContain('Plays');
    const keys = query('an-touch-keyboard [role="group"]');
    expect(keys).not.toBeNull();
    if (!keys) {
      return;
    }
    keys.getBoundingClientRect = () =>
      ({ left: 0, top: 0, width: 1500, height: 100, right: 1500, bottom: 100 }) as DOMRect;
    // C major over two octaves: 15 keys of 100 px. Two fingers: C2 low on its key (loud), E2 near its top (soft).
    const touch = (type: string, pointerId: number, x: number, y: number) =>
      keys.dispatchEvent(
        new PointerEvent(type, { pointerId, clientX: x, clientY: y, pointerType: 'touch', bubbles: true })
      );
    touch('pointerdown', 1, 50, 95);
    touch('pointerdown', 2, 250, 5);
    await stable();
    await new Promise(resolve => setTimeout(resolve, 20));
    const ons = server.liveMidi.flatMap(m => m.events).filter(e => (e.status & 0xf0) === 0x90);
    expect(ons.map(e => [e.data1, e.data2])).toEqual([
      [48, 123],
      [52, 44]
    ]);
    touch('pointerup', 1, 50, 95);
    touch('pointerup', 2, 250, 5);
    await stable();
    await new Promise(resolve => setTimeout(resolve, 20));
    expect(server.liveMidi.flatMap(m => m.events).filter(e => (e.status & 0xf0) === 0x80)).toHaveLength(2);
    query('[data-testid="play-drums"]')?.click();
    await stable();
    const kick = query('an-drum-pads [data-note="36"]');
    expect(kick?.textContent).toMatch(/Kick\s*C1/);
    kick?.dispatchEvent(new PointerEvent('pointerdown', { pointerId: 3, pointerType: 'touch', bubbles: true }));
    await stable();
    await new Promise(resolve => setTimeout(resolve, 20));
    expect(server.liveMidi.at(-1)?.events[0]).toMatchObject({ status: 0x90, data1: 36 });
  });

  it('runs key-command pads it can, shows the rest as desktop-only, and edits the sets', async () => {
    const { server, query, element, tab, stable } = await open(1);
    await tab('pads');
    const pad = (command: string) => query(`[data-testid="key-pad"][data-command="${command}"]`);
    pad('transport.play')?.click();
    await stable();
    expect((await server.transport()).state).toBe('playing');
    [...element.querySelectorAll<HTMLButtonElement>('[role="radio"]')]
      .find(b => b.textContent.includes('Editing'))
      ?.click();
    await stable();
    expect(pad('region.split')?.hasAttribute('disabled')).toBe(true);
    expect(pad('track.marker')?.hasAttribute('disabled')).toBe(false);
    pad('track.marker')?.click();
    await stable();
    expect(server.current?.markers).toHaveLength(1);
    query('[data-testid="pads-edit"]')?.click();
    await stable();
    const select = query('[data-testid="pad-new-command"]') as HTMLSelectElement | null;
    if (select) {
      select.value = 'transport.metronome';
    }
    query('[data-testid="pad-add"]')?.click();
    await stable();
    const saved = (await server.remotePads()).sets.find(s => s.id === 'editing');
    expect(saved?.pads.at(-1)).toMatchObject({ commandId: 'transport.metronome', label: 'Metronome Click' });
    expect(TestBed.inject(RemotePadsStore).set()?.pads.at(-1)?.commandId).toBe('transport.metronome');
  });

  it('launches Live Loops scenes', async () => {
    const { server, project, query, tab } = await open(1);
    await project.execute([{ type: 'addScene' }], 'Add Scene');
    await tab('loops');
    expect(query('[data-testid="remote-scene"]')).not.toBeNull();
    query('[data-testid="remote-scene"]')?.click();
    await settle();
    expect(server.requests).toContain('LaunchCells');
  });
});

describe('listen-in on the remote', () => {
  it('turns on, plays the fake stream, and shows how far behind the transport it is', async () => {
    const server = setUpFakeServer();
    await server.createProject({ name: 'Listen' });
    await settle();
    const sockets: FakeListenInSocket[] = [];
    const open = server.openListenIn.bind(server);
    server.openListenIn = () => {
      const socket = open();
      sockets.push(socket);
      return socket;
    };
    const store = TestBed.inject(ListenInStore);
    TestBed.inject(TransportStore).play();
    await settle();
    await store.toggle();
    expect(store.status()).toBe('connecting');
    await settle();
    await settle();
    expect(store.decoderName()).toBe('PCM (test)');
    // The golden path's capture hook keeps what is heard, from the first frame with a transport position.
    const hook = (globalThis as { anodeListenIn?: Pick<ListenInStore, 'startCapture' | 'capture'> }).anodeListenIn;
    hook?.startCapture(0.05);
    sockets[0]?.pump(5);
    const captured = hook?.capture();
    expect(captured?.samples.length).toBe(2400);
    expect(captured?.transportSample).toBeGreaterThanOrEqual(0);
    expect(store.status()).toBe('playing');
    (store.player as DirectPlayer).render(10);
    store.refresh();
    expect(store.stats()?.priming).toBe(false);
    expect(store.latencyMs()).not.toBeNull();
    await settle();
    expect(store.server().listeners).toBe(1);
    store.stop();
    expect(store.status()).toBe('off');
    await settle();
    expect(store.server().listeners).toBe(0);
  });

  it('says so when four remotes listen already', async () => {
    const server = setUpFakeServer();
    await server.createProject({ name: 'Listen' });
    await settle();
    for (let i = 0; i < 4; i++) {
      server.openListenIn();
    }
    const store = TestBed.inject(ListenInStore);
    await store.toggle();
    expect(store.status()).toBe('failed');
    expect(store.error()).toContain('4 listeners');
  });
});

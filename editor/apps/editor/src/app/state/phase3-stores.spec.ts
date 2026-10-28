import { TestBed } from '@angular/core/testing';
import type { Track } from '@anode/api-client';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { defaultLayout, parseLayout, showPane } from '../pages/layout';
import { KeyCommandsStore } from './key-commands.store';
import { laneRows } from './lanes';
import { ProjectStore } from './project.store';
import { ScreensetStore } from './screenset.store';

const Bar = 3840;

async function openProject() {
  const server = setUpFakeServer();
  const store = TestBed.inject(ProjectStore);
  store.load(await server.createProject({ name: 'Song' }));
  return { server, store };
}

describe('ProjectStore, Phase 3 deltas', () => {
  it('keeps markers, sections, take folders and notes in step with the Server', async () => {
    const { server, store } = await openProject();
    await store.execute(
      [
        { type: 'addTrack', stereo: false },
        { type: 'addMarker', name: 'Verse', tick: Bar, lengthTicks: 0 },
        { type: 'addSection', name: 'Intro', startTick: 0, lengthTicks: 4 * Bar },
        { type: 'setProjectNotes', text: 'Lyrics' }
      ],
      'Setup'
    );
    expect(store.markers().map(m => m.name)).toEqual(['Verse']);
    expect(store.sections().map(s => s.name)).toEqual(['Intro']);
    expect(store.notes()).toBe('Lyrics');

    const file = await server.importAudioFile('/audio/take.wav');
    store.upsertFile(file);
    const trackId = store.trackIds()[0] ?? '';
    await store.execute(
      [
        { type: 'addRegion', trackId, fileId: file.id, startTick: 0, length: 96_000 },
        { type: 'addRegion', trackId, fileId: file.id, startTick: 0, length: 96_000 }
      ],
      'Two takes'
    );
    await store.execute([{ type: 'packTakeFolder', regionIds: [...store.regions().keys()] }], 'Pack');
    expect(store.regions().size).toBe(0);
    expect(store.takeFolderList()[0]?.takes).toHaveLength(2);

    await store.undo();
    expect(store.takeFolders().size).toBe(0);
    await store.execute([{ type: 'removeTrack', trackId }], 'Remove');
    expect(store.takeFolders().size).toBe(0);
    await store.execute([{ type: 'deleteMarkers', markerIds: [store.markers()[0]?.id ?? ''] }], 'Delete');
    expect(store.markers()).toEqual([]);
  });

  it('measures audio under the tempo track', async () => {
    const { store } = await openProject();
    await store.execute(
      [
        {
          type: 'setTempoEvents',
          events: [
            { tick: 0, bpm: 120, curve: 'step' },
            { tick: Bar, bpm: 60, curve: 'step' }
          ]
        }
      ],
      'Tempo'
    );
    // 3 s at 48 kHz: 2 s for the first bar at 120 bpm, then 1 s (one beat) at 60.
    expect(store.regionEnd({ startTick: 0, length: 144_000 })).toBeCloseTo(Bar + 960);
    expect(store.meter().position(Bar).bar).toBe(2);
  });
});

describe('lane rows', () => {
  const track = (id: string, extra: Partial<Track> = {}): Track => ({
    id,
    name: id,
    kind: 'audio',
    color: '#fff',
    input: { channel: 1, stereo: false },
    recordArmed: false,
    inputMonitoring: false,
    strip: {
      volumeDb: 0,
      pan: 0,
      mute: false,
      solo: false,
      inserts: [],
      sends: [],
      output: 'master',
      groups: [],
      vcaId: null
    },
    instrument: null,
    midiInput: { device: null, channel: 0 },
    midiOutput: null,
    parentId: null,
    collapsed: false,
    notes: '',
    inputBus: 0,
    automationMode: 'read',
    frozenFileId: null,
    ...extra
  });
  const folder = {
    id: 'tkf_1',
    trackId: 'a',
    name: 'Vox',
    startTick: 0,
    lengthTicks: Bar,
    takes: [
      { id: 'tak_1', name: 'Take 1', fileId: 'f', startTick: 0, fileOffset: 0, length: 1 },
      { id: 'tak_2', name: 'Take 2', fileId: 'f', startTick: 0, fileOffset: 0, length: 1 }
    ],
    comp: [],
    crossfade: 0,
    muted: false
  };

  it('hides the members of a collapsed stack and indents the others', () => {
    const tracks = [track('s', { kind: 'stack', collapsed: true }), track('a', { parentId: 's' }), track('b')];
    expect(laneRows(tracks, [], new Set()).map(r => r.id)).toEqual(['s', 'b']);
    const open = laneRows([{ ...tracks[0], collapsed: false } as Track, ...tracks.slice(1)], [], new Set());
    expect(open.map(r => (r.kind === 'track' ? r.depth : -1))).toEqual([0, 1, 0]);
  });

  it('lists an open take folder’s takes under its track', () => {
    const rows = laneRows([track('a'), track('b')], [folder], new Set(['tkf_1']));
    expect(rows.map(r => r.id)).toEqual(['a', 'tkf_1/tak_1', 'tkf_1/tak_2', 'b']);
    expect(laneRows([track('a')], [folder], new Set()).map(r => r.id)).toEqual(['a']);
  });
});

describe('layouts', () => {
  it('brings a tucked-away pane forward and selects a tab', () => {
    const layout = showPane(defaultLayout(), 'browsers');
    expect(JSON.stringify(layout)).toContain('"contentId":"browsers","header":"Browsers","size":2,"isPinned":true');
    const editors = showPane(defaultLayout(), 'editors');
    expect(JSON.stringify(editors)).toContain('"selectedIndex":1');
    expect(showPane(defaultLayout(), 'nowhere')).toEqual(defaultLayout());
  });

  it('reads stored layouts, refusing ones without the current panes', () => {
    expect(parseLayout(JSON.stringify(defaultLayout()))).toEqual(defaultLayout());
    expect(parseLayout('{"rootPane":{"type":"splitPane","orientation":"vertical","panes":[]}}')).toBeNull();
    expect(parseLayout('not json')).toBeNull();
  });
});

describe('ScreensetStore', () => {
  it('saves an unlocked screenset’s layout and recalls each by number', async () => {
    vi.useFakeTimers();
    try {
      const { server } = await openProject();
      const screensets = TestBed.inject(ScreensetStore);
      TestBed.tick();
      await vi.runOnlyPendingTimersAsync();
      expect(screensets.current()).toBe(1);

      screensets.showPane('browsers');
      TestBed.tick();
      await vi.advanceTimersByTimeAsync(1_000);
      expect((await server.screensets()).map(s => s.number)).toEqual([1]);

      await screensets.recall(2);
      expect(screensets.current()).toBe(2);
      expect(JSON.stringify(screensets.layout())).toEqual(JSON.stringify(defaultLayout()));
      await screensets.rename('Mixing');
      await screensets.toggleLock();
      screensets.showPane('notes');
      TestBed.tick();
      await vi.advanceTimersByTimeAsync(1_000);
      // Locked: the change is not kept, and recalling it returns to its saved layout.
      await screensets.recall(1);
      expect(JSON.stringify(screensets.layout())).toContain(
        '"contentId":"browsers","header":"Browsers","size":2,"isPinned":true'
      );
      await screensets.recall(2);
      expect(screensets.name()).toBe('Mixing');
      expect(screensets.locked()).toBe(true);
      expect(JSON.stringify(screensets.layout())).toEqual(JSON.stringify(defaultLayout()));
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('KeyCommandsStore', () => {
  it('resolves keys to registered actions and saves rebindings for the user', async () => {
    const { server } = await openProject();
    const keys = TestBed.inject(KeyCommandsStore);
    await settle();
    let cycles = 0;
    keys.register('transport.cycle', () => cycles++);
    const press = (code: string, key: string) =>
      keys.resolve({ code, key, metaKey: false, ctrlKey: false, altKey: false, shiftKey: false });

    expect(press('KeyC', 'c')).toBe('transport.cycle');
    expect(press('KeyR', 'r')).toBeNull(); // bound, but nothing registered here
    await keys.rebind('transport.cycle', 'L');
    expect(press('KeyC', 'c')).toBeNull();
    const id = press('KeyL', 'l');
    expect(id).toBe('transport.cycle');
    keys.run(id ?? '');
    expect(cycles).toBe(1);
    expect((await server.keyCommands()).bindings).toEqual({ 'transport.cycle': ['L'] });

    await keys.reset();
    expect(press('KeyC', 'c')).toBe('transport.cycle');
  });
});

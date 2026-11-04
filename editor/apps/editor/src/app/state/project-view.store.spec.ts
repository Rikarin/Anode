import { TestBed } from '@angular/core/testing';
import type { FakeAnodeServer } from '../core/fake/fake-server';
import { setUpFakeServer } from '../core/fake/testing';
import { MixerViewStore } from '../mixer/mixer-pane';
import { FlexStore } from './flex.store';
import { ProjectViewStore } from './project-view.store';
import { ProjectStore } from './project.store';
import { TracksViewStore } from './tracks-view.store';

describe('ProjectViewStore', () => {
  let server: FakeAnodeServer;
  let project: ProjectStore;
  let view: TracksViewStore;
  let flex: FlexStore;

  beforeEach(() => {
    const stored = new Map<string, string>();
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => stored.get(key) ?? null,
      setItem: (key: string, value: string) => void stored.set(key, value)
    });
    server = setUpFakeServer();
    project = TestBed.inject(ProjectStore);
    view = TestBed.inject(TracksViewStore);
    flex = TestBed.inject(FlexStore);
    TestBed.inject(ProjectViewStore);
  });

  afterEach(() => vi.unstubAllGlobals());

  async function open(name: string): Promise<string> {
    const created = await server.createProject({ name });
    project.load(created);
    TestBed.tick();
    return created.path;
  }

  /** Closes the project in the window, as closing it does. */
  function close(): void {
    project.clear();
    TestBed.tick();
  }

  async function reopen(path: string): Promise<void> {
    project.load(await server.openProject(path));
    TestBed.tick();
  }

  it('does not carry the view from one project to the next', async () => {
    await open('First');
    flex.showFlex.set(true);
    view.pixelsPerQuarter.set(120);
    view.tool.set('pencil');
    view.originTick.set(9_600);
    view.expandedFolders.set(new Set(['tkf_1']));
    TestBed.tick();
    close();
    await open('Second');
    expect(flex.showFlex()).toBe(false);
    expect(view.pixelsPerQuarter()).toBe(48);
    expect(view.tool()).toBe('pointer');
    expect(view.originTick()).toBe(0);
    expect(view.expandedFolders().size).toBe(0);
  });

  it('gives a project the view it was left in', async () => {
    const path = await open('First');
    flex.showFlex.set(true);
    view.pixelsPerQuarter.set(120);
    view.snapMode.set('beat');
    view.globalLanes.set(new Set(['tempo']));
    TestBed.tick();
    close();
    await open('Second');
    expect(flex.showFlex()).toBe(false);
    close();
    await reopen(path);
    expect(flex.showFlex()).toBe(true);
    expect(view.pixelsPerQuarter()).toBe(120);
    expect(view.snapMode()).toBe('beat');
    expect([...view.globalLanes()]).toEqual(['tempo']);
  });

  it('also switches when one project replaces another without closing', async () => {
    await open('First');
    flex.showFlex.set(true);
    TestBed.tick();
    await open('Second');
    expect(flex.showFlex()).toBe(false);
  });

  it('keeps the Mixer’s view, which comes along with its pane', async () => {
    const path = await open('First');
    const mixer = TestBed.inject(MixerViewStore);
    mixer.view.set('buses');
    mixer.toggle('aux');
    TestBed.tick();
    close();
    expect(mixer.view()).toBe('all');
    await reopen(path);
    expect(mixer.view()).toBe('buses');
    expect([...mixer.hidden()]).toEqual(['aux']);
  });

  it('stores a view once it rests, and ignores what it cannot read', async () => {
    vi.useFakeTimers();
    try {
      await vi.advanceTimersByTimeAsync(0);
      const created = await server.createProject({ name: 'First' });
      project.load(created);
      TestBed.tick();
      flex.showFlex.set(true);
      TestBed.tick();
      await vi.advanceTimersByTimeAsync(1_000);
      expect(localStorage.getItem(`anode.view.${created.id}`)).toContain('"flex":true');
      close();
      localStorage.setItem(`anode.view.${created.id}`, '{"flex":tru');
      project.load(created);
      TestBed.tick();
      expect(flex.showFlex()).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it('falls back to the defaults for a stored view that makes no sense', async () => {
    const path = await open('First');
    const id = project.info()?.id ?? '';
    close();
    localStorage.setItem(
      `anode.view.${id}`,
      JSON.stringify({ tracks: { pixelsPerQuarter: 'wide', laneHeight: 9_999, tool: 'axe', globalLanes: 'all' } })
    );
    await reopen(path);
    expect(view.pixelsPerQuarter()).toBe(48);
    expect(view.laneHeight()).toBe(160);
    expect(view.tool()).toBe('pointer');
    expect(view.globalLanes().has('tempo')).toBe(true);
  });
});

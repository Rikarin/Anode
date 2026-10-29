import { TestBed } from '@angular/core/testing';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { nearestZeroCrossing } from './audio-edit-math';
import { AudioFileEditor, FetchDebounceMs } from './audio-file-editor';

/** A 2D context that counts what is drawn (jsdom has no canvas). */
function recordingContext() {
  const calls = { fillRect: 0, lineTo: 0, arc: 0, fillText: [] as string[] };
  const context = {
    calls,
    setTransform: () => undefined,
    fillRect: () => {
      calls.fillRect++;
    },
    fillText: (text: string) => {
      calls.fillText.push(text);
    },
    beginPath: () => undefined,
    moveTo: () => undefined,
    lineTo: () => {
      calls.lineTo++;
    },
    stroke: () => undefined,
    arc: () => {
      calls.arc++;
    },
    fill: () => undefined
  };
  return context;
}

/** A field or button of the editor (TestBed attaches it to the document). */
const field = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLInputElement;
const button = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLButtonElement;

describe('Audio File Editor', () => {
  let context: ReturnType<typeof recordingContext>;
  beforeEach(() => {
    context = recordingContext();
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(context as unknown as CanvasRenderingContext2D);
  });

  async function open() {
    const server = setUpFakeServer({ demo: true });
    const project = TestBed.inject(ProjectStore);
    await project.refetch();
    const region = project.regionList()[0];
    if (!region) {
      throw new Error('The demo has an audio region.');
    }
    TestBed.inject(SelectionStore).select([region.id]);
    const samples = vi.spyOn(server, 'fileSamples');
    const fixture = TestBed.createComponent(AudioFileEditor);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    await rest();
    const element = fixture.nativeElement as HTMLElement;
    const file = () => project.files().get(region.fileId);
    const byId = (id: string) => element.querySelector(`[data-testid="${id}"]`) as HTMLElement;
    return { server, project, region, fixture, element, editor: fixture.componentInstance, file, byId, samples };
  }

  /** Waits for the debounced fetch and its answers. */
  async function rest(): Promise<void> {
    await new Promise(resolve => setTimeout(resolve, FetchDebounceMs + 10));
    await settle();
  }

  function type(input: HTMLInputElement, value: number | string): void {
    input.value = String(value);
    input.dispatchEvent(new Event('change'));
  }

  function menuItem(id: string): HTMLButtonElement {
    return document.querySelector<HTMLButtonElement>(`[data-testid="${id}"]`) as HTMLButtonElement;
  }

  it('shows the selected region’s file and fetches what is visible at the zoom’s bucket', async () => {
    const { file, byId, samples, editor, fixture } = await open();
    const f = file();
    expect(f).toBeDefined();
    expect(byId('afe-title').textContent).toContain(f?.name);
    const waveform = byId('afe-waveform');
    expect(waveform.getAttribute('role')).toBe('application');
    // The whole file in 800 px: min/max buckets.
    const bucket = Number(waveform.dataset['bucket']);
    expect(bucket).toBeGreaterThan(1);
    expect(samples).toHaveBeenCalledWith(f?.id, 0, expect.any(Number), bucket);
    expect(samples.mock.calls.every(([, , frames, b]) => Math.ceil(frames / (b ?? 1)) <= 65_536)).toBe(true);
    // Drawn: the ruler's labels and a min/max column per pixel of each lane.
    expect(context.calls.fillText[0]).toBe('0:00.000');
    expect(context.calls.fillRect).toBeGreaterThan(800 * (f?.channels ?? 1) * 0.9);

    // Zoomed in to single samples: raw samples (bucket 1) of the visible range.
    samples.mockClear();
    for (let i = 0; i < 16; i++) {
      byId('afe-zoom-in').click();
    }
    await fixture.whenStable();
    await rest();
    expect(Number(waveform.dataset['bucket'])).toBe(1);
    expect(editor.shownView().framesPerPixel).toBeLessThan(1);
    expect(samples).toHaveBeenCalledWith(f?.id, expect.any(Number), expect.any(Number), 1);
    // Far in, the samples are a line with a point at each.
    expect(context.calls.lineTo).toBeGreaterThan(0);
    expect(context.calls.arc).toBeGreaterThan(0);
    // The same view asks for nothing again (cached).
    samples.mockClear();
    byId('afe-zoom-out').click();
    byId('afe-zoom-in').click();
    await fixture.whenStable();
    await rest();
    expect(samples).not.toHaveBeenCalled();
  });

  it('selects by dragging, extends with shift, and by typing exact frames', async () => {
    const { byId, editor, fixture, file } = await open();
    const waveform = byId('afe-waveform');
    const total = file()?.frames ?? 0;
    const perPixel = total / 800;
    const pointer = (type: string, x: number, shiftKey = false) =>
      waveform.dispatchEvent(
        new PointerEvent(type, { clientX: x, clientY: 60, button: 0, pointerId: 1, shiftKey, bubbles: true })
      );
    pointer('pointerdown', 100);
    pointer('pointermove', 300);
    pointer('pointerup', 300);
    await fixture.whenStable();
    expect(editor.selection()).toEqual({ start: Math.round(100 * perPixel), end: Math.round(300 * perPixel) });
    expect(byId('afe-selection')).not.toBeNull();
    pointer('pointerdown', 400, true);
    pointer('pointerup', 400, true);
    expect(editor.selection()).toEqual({ start: Math.round(100 * perPixel), end: Math.round(400 * perPixel) });

    type(field('afe-start'), 1000);
    type(field('afe-end'), 2000);
    await fixture.whenStable();
    expect(editor.selection()).toEqual({ start: 1000, end: 2000 });
    expect(byId('afe-selection-readout').textContent).toContain('length 1000 samples');
    byId('afe-select-all').click();
    expect(editor.selection()).toEqual({ start: 0, end: total });
  });

  it('snaps the selection’s edges to zero crossings', async () => {
    const { server, byId, editor, fixture, file } = await open();
    const toggle = byId('afe-snap-zero');
    expect(toggle.getAttribute('aria-pressed')).toBe('false');
    toggle.click();
    await fixture.whenStable();
    expect(toggle.getAttribute('aria-pressed')).toBe('true');
    type(field('afe-start'), 10_007);
    await settle();
    type(field('afe-end'), 30_011);
    await settle();
    await settle();
    const id = file()?.id ?? '';
    const around = async (frame: number) => {
      const range = await server.fileSamples(id, frame - 2048, 4096, 1);
      return nearestZeroCrossing(range.samples?.[0] ?? [], frame, range.startFrame, 2048);
    };
    const selection = editor.selection();
    expect(selection?.start).toBe(await around(10_007));
    expect(selection?.end).toBe(await around(30_011));
  });

  it('runs operations as audioEdit jobs with progress, refetches the new version, and undoes', async () => {
    const { server, byId, editor, fixture, file, samples } = await open();
    const editFile = vi.spyOn(server, 'editFile');
    type(field('afe-start'), 4800);
    type(field('afe-end'), 9600);
    await fixture.whenStable();

    // Normalize asks for its peak first (−1 dBFS by default).
    byId('afe-functions').click();
    await fixture.whenStable();
    menuItem('afe-op-normalize').click();
    await fixture.whenStable();
    expect(field('afe-op-value').valueAsNumber).toBe(-1);
    byId('afe-op-apply').click();
    await settle();
    await fixture.whenStable();
    expect(editFile).toHaveBeenLastCalledWith(file()?.id, {
      kind: 'normalize',
      startFrame: 4800,
      endFrame: 9600,
      value: -1
    });
    // While the job runs: progress, and the operations wait.
    expect(editor.busy()).toBe(true);
    expect(byId('afe-progress').querySelector('[role="progressbar"]')).not.toBeNull();
    expect(button('afe-functions').disabled).toBe(true);
    expect(button('afe-undo').disabled).toBe(true);

    samples.mockClear();
    server.runJobs();
    await settle();
    await fixture.whenStable();
    expect(editor.busy()).toBe(false);
    expect(byId('afe-progress')).toBeNull();
    expect(file()?.version).toBe(1);
    expect(byId('afe-waveform').dataset['version']).toBe('1');
    await rest();
    // The new version's peaks are fetched again.
    expect(samples).toHaveBeenCalled();

    // Fade In with a curve, Reverse at once, Sample Rate Conversion to a target rate.
    byId('afe-functions').click();
    await fixture.whenStable();
    menuItem('afe-op-fadeIn').click();
    await fixture.whenStable();
    expect(byId('afe-op-curve')).not.toBeNull();
    byId('afe-op-apply').click();
    await settle();
    expect(editFile).toHaveBeenLastCalledWith(file()?.id, {
      kind: 'fadeIn',
      startFrame: 4800,
      endFrame: 9600,
      curve: 0
    });
    server.runJobs();
    await settle();
    await fixture.whenStable();

    byId('afe-functions').click();
    await fixture.whenStable();
    menuItem('afe-op-reverse').click();
    await settle();
    expect(editFile).toHaveBeenLastCalledWith(file()?.id, { kind: 'reverse', startFrame: 4800, endFrame: 9600 });
    server.runJobs();
    await settle();
    await fixture.whenStable();
    expect(file()?.version).toBe(3);

    byId('afe-undo').click();
    await settle();
    await fixture.whenStable();
    expect(file()?.version).toBe(2);

    byId('afe-functions').click();
    await fixture.whenStable();
    menuItem('afe-op-convertRate').click();
    await fixture.whenStable();
    const rate = byId('afe-op-rate') as HTMLSelectElement;
    rate.value = '96000';
    rate.dispatchEvent(new Event('change'));
    byId('afe-op-apply').click();
    await settle();
    expect(editFile).toHaveBeenLastCalledWith(file()?.id, {
      kind: 'convertRate',
      startFrame: 0,
      endFrame: file()?.frames,
      targetRate: 96_000
    });
  });

  it('marks the loops it finds and selects one when clicked', async () => {
    const { server, byId, editor, element, fixture, file } = await open();
    const search = vi.spyOn(server, 'searchLoops');
    byId('afe-loop-search').click();
    await settle();
    await fixture.whenStable();
    expect(search).toHaveBeenCalledWith(file()?.id, { startFrame: 0, endFrame: file()?.frames, count: 5 });
    const results = [...element.querySelectorAll<HTMLButtonElement>('[data-testid="afe-loop-result"]')];
    expect(results.length).toBe(editor.loops().length);
    expect(results.length).toBeGreaterThan(0);
    expect(element.querySelectorAll('[data-testid="afe-loop-marker"]').length).toBe(results.length);
    const scores = editor.loops().map(l => l.score);
    expect([...scores].sort((a, b) => b - a)).toEqual(scores);
    results[1]?.click();
    await fixture.whenStable();
    const second = editor.loops()[1];
    expect(editor.selection()).toEqual({ start: second?.startFrame, end: second?.endFrame });
    expect(results[1]?.getAttribute('aria-pressed')).toBe('true');
  });

  it('previews the selection, looped on request, and stops', async () => {
    const { server, byId, fixture, file } = await open();
    type(field('afe-start'), 0);
    type(field('afe-end'), 4800);
    byId('afe-preview-loop').click();
    await fixture.whenStable();
    expect(byId('afe-preview-loop').getAttribute('aria-pressed')).toBe('true');
    byId('afe-play').click();
    await settle();
    expect(server.previewRange).toEqual({
      fileId: file()?.id,
      request: { startFrame: 0, endFrame: 4800, loop: true }
    });
    const stop = vi.spyOn(server, 'previewFile');
    byId('afe-stop').click();
    await settle();
    expect(stop).toHaveBeenCalledWith(null);
  });

  it('makes a groove template from the region', async () => {
    const { project, byId, fixture, region } = await open();
    const before = project.grooves().size;
    byId('afe-make-groove').click();
    await fixture.whenStable();
    type(field('afe-groove-bars'), 2);
    const name = field('afe-groove-name');
    name.value = 'Drum Feel';
    name.dispatchEvent(new Event('input'));
    const execute = vi.spyOn(project, 'execute');
    byId('afe-groove-make').click();
    await settle();
    expect(execute).toHaveBeenCalledWith(
      [{ type: 'makeGroove', regionId: region.id, name: 'Drum Feel', lengthBars: 2 }],
      'Make Groove Template'
    );
    expect(project.grooves().size).toBe(before + 1);
  });
});

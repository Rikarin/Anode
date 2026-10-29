import { TestBed } from '@angular/core/testing';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import { LoopBrowser } from './loop-browser';
import { LoopDragType } from './loops.store';

async function render() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Loops' }));
  await server.installPack('/Users/anode/Downloads/TestPack.anodepack');
  const fixture = TestBed.createComponent(LoopBrowser);
  await settle();
  await fixture.whenStable();
  const element = fixture.nativeElement as HTMLElement;
  const rows = () => [...element.querySelectorAll<HTMLElement>('[data-testid="loop-row"]')];
  const update = async () => {
    await settle();
    await fixture.whenStable();
  };
  return { server, project, element, rows, update };
}

describe('LoopBrowser', () => {
  it('lists the loops with the project’s tempo and key, and filters by keyword and key', async () => {
    const { element, rows, update } = await render();
    expect(rows().map(r => r.getAttribute('aria-label'))).toEqual([
      'Bass Line',
      'Beat Pattern',
      'Drum Loop 100',
      'Hat Groove',
      'Piano Riff',
      'Stab',
      'Synth Chords'
    ]);
    expect(rows()[0]?.dataset['loopId']).toBe('test-pack:bass-line');
    expect(element.querySelector('[data-testid="loop-sync"]')?.textContent).toContain('120 bpm · C major');
    expect(element.querySelector('[role="searchbox"]')?.getAttribute('aria-label')).toBe('Search loops');

    const drums = [...element.querySelectorAll<HTMLButtonElement>('[data-testid="loop-keyword"]')].find(
      b => b.textContent.trim() === 'Drums'
    );
    drums?.click();
    await update();
    expect(drums?.getAttribute('aria-pressed')).toBe('true');
    expect(rows().map(r => r.getAttribute('aria-label'))).toEqual(['Beat Pattern', 'Drum Loop 100', 'Hat Groove']);

    const key = element.querySelector('[data-testid="loop-key"]') as HTMLSelectElement;
    expect(key.getAttribute('aria-label')).toBe('Key');
    key.value = '0';
    key.dispatchEvent(new Event('change'));
    await update();
    expect(rows().map(r => r.getAttribute('aria-label'))).toEqual(['Drum Loop 100']);
  });

  it('previews on a click, adds on Return, drags with the loop’s id; opens the Sound Library', async () => {
    const { server, project, element, rows, update } = await render();
    const bass = rows()[0] as HTMLElement;
    bass.click();
    await update();
    expect(server.previewSync?.loopId).toBe('test-pack:bass-line');
    expect(bass.querySelector('[aria-label="Preview Bass Line"]')?.getAttribute('aria-pressed')).toBe('true');

    bass.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    await update();
    expect(project.midiRegionList().length).toBe(1);

    const data = new Map<string, string>();
    const drag = new Event('dragstart') as DragEvent;
    Object.defineProperty(drag, 'dataTransfer', {
      value: { setData: (type: string, value: string) => data.set(type, value), effectAllowed: '' }
    });
    bass.dispatchEvent(drag);
    expect(data.get(LoopDragType)).toBe('test-pack:bass-line');
    expect(data.get('text/plain')).toBe('Bass Line');

    (element.querySelector('[data-testid="open-sound-library"]') as HTMLButtonElement).click();
    expect(TestBed.inject(UiStore).soundLibraryOpen()).toBe(true);
  });
});

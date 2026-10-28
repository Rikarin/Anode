import { TestBed } from '@angular/core/testing';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';
import { EditorsPane } from './editors-pane';

describe('Editors pane', () => {
  async function open() {
    const server = setUpFakeServer({ demo: true });
    const project = TestBed.inject(ProjectStore);
    await project.refetch();
    const fixture = TestBed.createComponent(EditorsPane);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    return { server, project, fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('switches editors by tab and follows the selection', async () => {
    const { project, fixture, element } = await open();
    const ui = TestBed.inject(UiStore);
    expect(element.querySelector('[role="tab"][aria-selected="true"]')?.textContent.trim()).toBe('Piano Roll');
    const region = project.regionList()[0];
    expect(region).toBeDefined();
    TestBed.inject(SelectionStore).select([region?.id ?? '']);
    await fixture.whenStable();
    expect(ui.editorView()).toBe('audio');
    expect(element.querySelector('[aria-label="Audio Track Editor"]')).not.toBeNull();
    (element.querySelectorAll<HTMLButtonElement>('[role="tab"]')[2] as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(element.querySelector('[aria-label="Smart Tempo Editor"]')).not.toBeNull();
  });

  it('turns on Flex Pitch, shows the notes and edits them', async () => {
    const { server, project, fixture, element } = await open();
    const region = project.regionList()[0];
    TestBed.inject(SelectionStore).select([region?.id ?? '']);
    await fixture.whenStable();
    element.querySelector<HTMLButtonElement>('[data-testid="flex-pitch-enable"]')?.click();
    await settle();
    server.runJobs();
    await settle();
    await settle();
    await fixture.whenStable();
    const after = project.regions().get(region?.id ?? '');
    expect(after?.pitchNotes.length).toBeGreaterThan(0);
    const notes = element.querySelectorAll<HTMLElement>('[data-testid="pitch-note"]');
    expect(notes.length).toBe(after?.pitchNotes.length);
    const first = after?.pitchNotes[0];
    notes[0]?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true }));
    await settle();
    expect(project.regions().get(region?.id ?? '')?.pitchNotes[0]?.targetCents).toBe((first?.targetCents ?? 0) + 100);
    element.querySelector<HTMLButtonElement>('[data-testid="perfect-pitch-apply"]')?.click();
    await settle();
    const corrected = project.regions().get(region?.id ?? '')?.pitchNotes ?? [];
    expect(corrected.every(n => n.targetCents % 100 === 0)).toBe(true);
  });

  it('builds quick pitch edits on each other, before the Server answers', async () => {
    const { server, project, fixture, element } = await open();
    const region = project.regionList()[0];
    TestBed.inject(SelectionStore).select([region?.id ?? '']);
    await fixture.whenStable();
    element.querySelector<HTMLButtonElement>('[data-testid="flex-pitch-enable"]')?.click();
    await settle();
    server.runJobs();
    await settle();
    await settle();
    await fixture.whenStable();
    const first = project.regions().get(region?.id ?? '')?.pitchNotes[0];
    const note = () => element.querySelectorAll<HTMLElement>('[data-testid="pitch-note"]')[0];
    const vibrato = () => element.querySelectorAll<HTMLElement>('[data-testid="pitch-hotspot-vibrato"]')[0];
    // Two nudges and five vibrato steps in a row, none waiting for the last edit to come back.
    for (const target of [note, note]) {
      target()?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true }));
      fixture.detectChanges();
    }
    for (let i = 0; i < 5; i++) {
      vibrato()?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
      fixture.detectChanges();
    }
    await settle();
    await settle();
    const after = project.regions().get(region?.id ?? '')?.pitchNotes[0];
    expect(after?.targetCents).toBe((first?.targetCents ?? 0) + 200);
    expect(after?.vibrato).toBeCloseTo((first?.vibrato ?? 1) - 0.5, 5);
  });

  it('edits the file tempo in the Smart Tempo Editor', async () => {
    const { project, fixture, element } = await open();
    const region = project.regionList()[0];
    TestBed.inject(SelectionStore).select([region?.id ?? '']);
    TestBed.inject(UiStore).editorView.set('smartTempo');
    await settle();
    await fixture.whenStable();
    const bpm = element.querySelector('[data-testid="smart-tempo-bpm"]')?.textContent ?? '';
    expect(bpm).toMatch(/bpm/);
    expect(element.querySelectorAll('[data-testid="smart-tempo-beat"]').length).toBeGreaterThan(0);
    element.querySelector<HTMLButtonElement>('[data-testid="smart-tempo-double"]')?.click();
    await fixture.whenStable();
    const doubled = Number.parseFloat(element.querySelector('[data-testid="smart-tempo-bpm"]')?.textContent ?? '');
    expect(doubled).toBeCloseTo(2 * Number.parseFloat(bpm), 0);
    element.querySelector<HTMLButtonElement>('[data-testid="smart-tempo-keep"]')?.click();
    await settle();
    expect(project.files().get(region?.fileId ?? '')?.tempo?.bpm).toBeCloseTo(doubled, 1);
  });
});

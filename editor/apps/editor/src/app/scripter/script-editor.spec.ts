import { TestBed } from '@angular/core/testing';
import type { Insert } from '@anode/api-client';
import { EditorView } from '@codemirror/view';
import { targetsOf } from '../automation/targets';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { MixerStore } from '../state/mixer.store';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { ScriptLogStore } from '../state/script-log.store';
import { UiStore } from '../state/ui.store';
import { FactoryScripts } from './factory-scripts';
import { ScriptEditor } from './script-editor';
import { StarterScript, currentProblemLine, isRunShortcut, problemLine, scriptControl } from './script-model';
import { OctaveEchoScript } from './testing';

async function scripterSlot() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Scripter' }));
  await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: true, name: 'Synth' }], 'Track');
  const trackId = project.trackList()[0]?.id ?? '';
  await TestBed.inject(MixerStore).addInsert(trackId, 'anode.scripter', undefined, 'midiEffects');
  const slot = () => project.track(trackId)?.strip.midiEffects[0] as Insert;
  const slotId = slot().id;
  const ui = TestBed.inject(UiStore);
  ui.scriptEditor.set({ trackId, slotId });
  const fixture = TestBed.createComponent(ScriptEditor);
  await fixture.whenStable();
  const element = fixture.nativeElement as HTMLElement;
  const editor = fixture.componentInstance;
  const query = (testId: string) => element.querySelector<HTMLElement>(`[data-testid="${testId}"]`);
  const stable = async () => {
    await settle();
    await fixture.whenStable();
  };
  return { server, project, trackId, slotId, slot, ui, fixture, element, editor, query, stable };
}

describe('Script Editor', () => {
  beforeAll(() => {
    // jsdom lays nothing out: CodeMirror measures text ranges (selection, cursor) after updates.
    const range = Range.prototype as Partial<Range>;
    range.getClientRects ??= () => Object.assign([], { item: () => null });
    range.getBoundingClientRect ??= () => new DOMRect();
  });

  it('parses the line a problem names', () => {
    expect(problemLine('SyntaxError: unexpected token (line 3)')).toBe(3);
    expect(problemLine('ReferenceError: x is not defined at line 12:4')).toBe(12);
    expect(problemLine('The script ran out of memory.')).toBeNull();
    expect(problemLine('line 0')).toBeNull();
  });

  it('marks the latest problem since the last run', () => {
    const old = { text: 'Error (line 2)', kind: 'problem' as const };
    const trace = { text: 'hello', kind: 'trace' as const };
    const fresh = { text: 'Error (line 5)', kind: 'problem' as const };
    expect(currentProblemLine([old, trace], null)).toBe(2);
    expect(currentProblemLine([old, trace], trace)).toBeNull();
    expect(currentProblemLine([old, trace, fresh], trace)).toBe(5);
    // The mark was trimmed off the console: everything counts.
    expect(currentProblemLine([fresh], trace)).toBe(5);
  });

  it('shows menus as choices, checkboxes as toggles and the rest as knobs', () => {
    const base = { id: 'p0', name: 'P', min: 0, max: 1, default: 0, unit: '', steps: 0, valueNames: null, macro: null };
    expect(scriptControl({ ...base, steps: 1, valueNames: ['Up', 'Down'] })).toBe('choice');
    expect(scriptControl({ ...base, steps: 1 })).toBe('toggle');
    expect(scriptControl(base)).toBe('knob');
    expect(scriptControl({ ...base, max: 24, steps: 24 })).toBe('knob');
  });

  it('runs on ⌘↩ and Ctrl+↩ only', () => {
    expect(isRunShortcut(new KeyboardEvent('keydown', { key: 'Enter', metaKey: true }))).toBe(true);
    expect(isRunShortcut(new KeyboardEvent('keydown', { key: 'Enter', ctrlKey: true }))).toBe(true);
    expect(isRunShortcut(new KeyboardEvent('keydown', { key: 'Enter' }))).toBe(false);
    expect(isRunShortcut(new KeyboardEvent('keydown', { key: 'Enter', metaKey: true, shiftKey: true }))).toBe(false);
  });

  it('opens on a new slot with the starter script in a named code editor, and the window’s controls', async () => {
    const { element, editor, query } = await scripterSlot();
    expect(element.querySelector('[role="dialog"]')?.getAttribute('aria-label')).toBe('Script Editor');
    expect(query('script-editor')).not.toBeNull();
    expect(editor.source()).toBe(StarterScript);
    const code = query('script-editor-code');
    expect(code?.getAttribute('aria-label')).toBe('Script');
    expect(code?.getAttribute('contenteditable')).toBe('true');
    expect(code?.textContent).toContain('function HandleMIDI(event) {');
    expect(query('script-editor-run')?.textContent).toContain('Run');
    expect(query('script-editor-presets')?.getAttribute('aria-label')).toBe('Factory presets');
    expect((query('script-editor-revert') as HTMLButtonElement | null)?.disabled).toBe(true);
    const console = query('script-editor-console');
    expect(console?.getAttribute('role')).toBe('log');
    expect(console?.getAttribute('aria-live')).toBe('polite');
    expect(console?.textContent).toContain('Trace output and errors appear here.');
  });

  it('runs the text as one setScript step; the script’s parameters become controls; undo restores the text', async () => {
    const { project, slot, editor, element, query, stable } = await scripterSlot();
    editor.source.set(OctaveEchoScript);
    await stable();
    expect(query('script-editor-code')?.textContent).toContain('Octave echo');
    expect((query('script-editor-revert') as HTMLButtonElement | null)?.disabled).toBe(false);

    query('script-editor-run')?.click();
    await stable();
    expect(slot().source).toBe(OctaveEchoScript);
    expect(slot().runtimeParameters?.map(p => p.name)).toEqual(['Interval']);
    expect((query('script-editor-revert') as HTMLButtonElement | null)?.disabled).toBe(true);

    // Interval is a knob (a slider named by the parameter).
    const knob = query('script-parameter-Interval');
    const slider = knob?.querySelector('[role="slider"]');
    expect(slider?.getAttribute('aria-valuenow')).toBe('12');
    expect(knob?.textContent).toContain('Interval');

    // They automate and map to Smart Controls as the slot's parameters.
    const track = project.trackList()[0];
    const targets = track ? targetsOf(track, id => TestBed.inject(PluginsStore).plugin(id)) : [];
    expect(targets.find(t => t.target === `midiEffect:${slot().id}:Interval`)).toMatchObject({ name: 'Interval' });

    // The trace reached the console.
    expect(query('script-editor-console')?.textContent).toContain('Octave echo ready');
    expect(element.querySelector('[data-kind="trace"]')).not.toBeNull();

    await project.undo();
    await stable();
    expect(slot().source ?? null).toBeNull();
    expect(editor.source()).toBe(StarterScript);
  });

  it('takes typing from the code editor, keeps edits when the stored script changes, and reverts', async () => {
    const { project, trackId, slotId, slot, editor, element, query, stable } = await scripterSlot();
    const view = EditorView.findFromDOM(element.querySelector('.cm-editor') as HTMLElement);
    view?.dispatch({ changes: { from: 0, insert: '// mine\n' } });
    await stable();
    expect(editor.source()).toBe(`// mine\n${StarterScript}`);
    expect((query('script-editor-revert') as HTMLButtonElement | null)?.disabled).toBe(false);

    // Another client stores a script: the edits stay.
    await project.execute([{ type: 'setScript', trackId, slotId, source: OctaveEchoScript }], 'Run Script');
    await stable();
    expect(slot().source).toBe(OctaveEchoScript);
    expect(editor.source()).toBe(`// mine\n${StarterScript}`);

    query('script-editor-revert')?.click();
    await stable();
    expect(editor.source()).toBe(OctaveEchoScript);
    expect(view?.state.doc.toString()).toBe(OctaveEchoScript);
  });

  it('runs on ⌘↩ in the window', async () => {
    const { slot, editor, query, stable } = await scripterSlot();
    editor.source.set(OctaveEchoScript);
    await stable();
    query('script-editor-run')?.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Enter', metaKey: true, bubbles: true, cancelable: true })
    );
    await stable();
    expect(slot().source).toBe(OctaveEchoScript);
  });

  it('loads and runs a factory preset from the presets menu', async () => {
    const { slot, editor, query, stable } = await scripterSlot();
    query('script-editor-presets')?.click();
    await stable();
    const item = document.querySelector<HTMLElement>('[data-testid="script-preset-factory.chord"]');
    expect(item?.getAttribute('role')).toBe('menuitem');
    expect([...document.querySelectorAll('[data-testid^="script-preset-"]')].length).toBe(FactoryScripts.length);
    item?.click();
    await stable();
    const chord = FactoryScripts.find(s => s.id === 'factory.chord');
    expect(editor.source()).toBe(chord?.source);
    expect(slot().source).toBe(chord?.source);
    expect(query('script-editor-presets')?.textContent).toContain('Chord');
    // Its menu parameter is a choice.
    expect(query('script-parameter-Shape')?.tagName.toLowerCase()).toBe('an-choice');
  });

  it('shows checkboxes as toggles that set the slot’s parameter', async () => {
    const { slot, editor, query, stable } = await scripterSlot();
    const chord = FactoryScripts.find(s => s.id === 'factory.chord');
    if (chord) {
      await editor.loadPreset(chord);
    }
    await stable();
    const latch = query('script-parameter-Spread Velocity');
    expect(latch?.textContent).toContain('Spread Velocity');
    expect(latch?.getAttribute('aria-pressed')).toBe('false');
    latch?.click();
    await new Promise(resolve => setTimeout(resolve, 100));
    await stable();
    expect(slot().parameters['Spread Velocity']).toBe(1);
    expect(latch?.getAttribute('aria-pressed')).toBe('true');
  });

  it('shows a runaway script’s problem in the console and marks its line until the next run; Clear empties it', async () => {
    const { editor, element, query, stable } = await scripterSlot();
    editor.source.set('function HandleMIDI(event) {\n  while (true) {}\n}\n');
    await stable();
    await editor.runScript();
    await stable();
    const problem = element.querySelector('[data-kind="problem"]');
    expect(problem?.textContent).toContain('line 2');
    expect(problem?.classList).toContain('text-error');
    const marked = element.querySelector('.cm-an-error-line');
    expect(marked?.textContent).toContain('while (true) {}');

    editor.source.set(StarterScript);
    await editor.runScript();
    await stable();
    expect(element.querySelector('.cm-an-error-line')).toBeNull();

    query('script-editor-clear')?.click();
    await stable();
    expect(element.querySelector('[data-kind]')).toBeNull();
    expect((query('script-editor-clear') as HTMLButtonElement | null)?.disabled).toBe(true);
  });

  it('shows dropped lines from scriptLog', async () => {
    const { server, trackId, slotId, element, stable } = await scripterSlot();
    (server as unknown as { emit(m: unknown): void }).emit({
      type: 'scriptLog',
      trackId,
      slotId,
      lines: ['a', 'b'],
      dropped: 7
    });
    await stable();
    expect(TestBed.inject(ScriptLogStore).linesOf(trackId, slotId).length).toBe(3);
    expect(element.querySelector('[data-kind="dropped"]')?.textContent).toContain('7 more lines dropped');
  });

  it('says so when the slot is removed', async () => {
    const { trackId, slot, element, stable } = await scripterSlot();
    await TestBed.inject(MixerStore).removeInsert(trackId, slot());
    await stable();
    expect(element.textContent).toContain('This Scripter slot was removed.');
  });
});

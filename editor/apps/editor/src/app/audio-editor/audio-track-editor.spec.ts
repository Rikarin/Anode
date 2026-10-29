import { TestBed } from '@angular/core/testing';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { AudioTrackEditor } from './audio-track-editor';

describe('Audio Track Editor', () => {
  it('disables its tools while there is nothing to use them on (a tooltip does not take the binding)', async () => {
    const server = setUpFakeServer();
    TestBed.inject(ProjectStore).load(await server.createProject({ name: 'Pitch' }));
    const fixture = TestBed.createComponent(AudioTrackEditor);
    fixture.autoDetectChanges();
    await settle();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const button = (id: string) => element.querySelector<HTMLButtonElement>(`[data-testid="${id}"]`);
    for (const id of [
      'flex-pitch-enable',
      'perfect-pitch-apply',
      'scale-quantize-apply',
      'pitch-split',
      'pitch-join',
      'flex-pitch-to-midi'
    ]) {
      expect(button(id)?.disabled, id).toBe(true);
    }
  });
});

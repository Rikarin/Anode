import { TestBed } from '@angular/core/testing';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { GlobalTracks } from './global-lanes';

describe('Global lanes', () => {
  it('builds quick curve-shape key presses on each other, before the Server answers', async () => {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Curves' }));
    await project.execute(
      [
        {
          type: 'setTempoEvents',
          events: [
            { tick: 0, bpm: 100, curve: 'ramp', shape: 0 },
            { tick: 7680, bpm: 140, curve: 'step', shape: 0 }
          ]
        }
      ],
      'Tempo'
    );
    const fixture = TestBed.createComponent(GlobalTracks);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const handle = () => (fixture.nativeElement as HTMLElement).querySelector('[data-testid="tempo-curve-handle"]');
    // Six steps in a row, none waiting for the last edit to come back.
    for (let i = 0; i < 6; i++) {
      handle()?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowUp', bubbles: true }));
      fixture.detectChanges();
    }
    expect(handle()?.getAttribute('aria-valuenow')).toBe('0.3');
    await settle();
    await settle();
    expect(project.settings().tempoEvents[0]?.shape).toBe(0.3);
  });
});

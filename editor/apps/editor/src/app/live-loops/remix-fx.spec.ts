import { TestBed } from '@angular/core/testing';
import type { EditCommand } from '@anode/api-client';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { LiveLoopsStore } from './live-loops.store';
import { RemixFx, RemixFxParams } from './remix-fx';

async function open() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Remix' }));
  const sent: EditCommand[] = [];
  const apply = server.applyCommands.bind(server);
  server.applyCommands = request => {
    sent.push(...request.commands);
    return apply(request);
  };
  return { server, project, sent };
}

describe('Remix FX', () => {
  it('sends pad moves, holds and effect choices to the master’s Remix FX', async () => {
    const { project, sent } = await open();
    // Showing the grid the first time puts Remix FX on the master.
    await TestBed.inject(LiveLoopsStore).setShown(true);
    const fixture = TestBed.createComponent(RemixFx);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="remix-pad-1"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="remix-pad-2"]')).not.toBeNull();
    expect(element.querySelector('[aria-label="Pad 1 effect"]')?.textContent).toContain('Filter');
    expect(element.querySelector('[aria-label="Pad 2 effect"]')?.textContent).toContain('Repeater');

    const fx = fixture.componentInstance.insert();
    if (!fx) {
      throw new Error('no Remix FX');
    }
    const [pad1, pad2] = RemixFxParams.pads;
    fixture.componentInstance.engage(fx, pad1, true);
    fixture.componentInstance.move(fx, pad1, { x: 0.2, y: 0.5 });
    fixture.componentInstance.move(fx, pad2, { x: 0.9, y: 0.1 });
    fixture.componentInstance.engage(fx, pad1, false);
    fixture.componentInstance.setEffect(fx, pad2, 3);
    await settle();
    await settle();
    // y of pad 1 did not move, so only its x went out; the release follows the hold (coalesced per parameter, in
    // order); every send is on the master (no track).
    const params = sent.flatMap(c => (c.type === 'setInsertParameter' ? [`${c.parameterId}=${c.value}`] : []));
    expect([...params].sort()).toEqual(['xy1.on=0', 'xy1.on=1', 'xy1.x=0.2', 'xy2.mode=3', 'xy2.x=0.9', 'xy2.y=0.1']);
    expect(params.indexOf('xy1.on=1')).toBeLessThan(params.indexOf('xy1.on=0'));
    expect(sent.every(c => c.type !== 'setInsertParameter' || !c.trackId)).toBe(true);
    const stored = project.master().inserts.find(i => i.id === fx.id)?.parameters;
    expect(stored).toMatchObject({ 'xy1.x': 0.2, 'xy1.on': 0, 'xy2.x': 0.9, 'xy2.y': 0.1, 'xy2.mode': 3 });
    fixture.detectChanges();
    expect(element.querySelector('[aria-label="Pad 2 effect"]')?.textContent).toContain('Gater');
  });

  it('offers to add Remix FX when the master has none', async () => {
    const { project } = await open();
    const fixture = TestBed.createComponent(RemixFx);
    fixture.autoDetectChanges();
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toContain('Remix FX is not on the master');
    element.querySelector<HTMLButtonElement>('[data-testid="add-remix-fx"]')?.click();
    await settle();
    expect(project.master().inserts.map(i => i.pluginId)).toEqual([RemixFxParams.pluginId]);
  });
});

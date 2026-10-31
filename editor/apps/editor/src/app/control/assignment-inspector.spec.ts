import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ControlAssignment } from '@anode/api-client';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { ProjectStore } from '../state/project.store';
import { type AssignmentChange, AssignmentInspector } from './assignment-inspector';
import { blankAssignment } from './assignments';
import { ControlStore } from './control.store';

@Component({
  imports: [AssignmentInspector],
  template: `<an-assignment-inspector
    [assignment]="assignment()"
    [modeId]="modeId"
    (changed)="changes.push($event)"
    (moved)="moves.push($event)"
  />`
})
class Host {
  readonly assignment = signal<ControlAssignment>(blankAssignment('cas_1', 'Fader'));
  modeId = '';
  readonly changes: AssignmentChange[] = [];
  readonly moves: string[] = [];
}

async function setUp() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Inspector' }));
  await project.execute([{ type: 'addTrack', stereo: true, name: 'Keys' }], 'Track');
  const control = TestBed.inject(ControlStore);
  await control.addAssignment();
  await control.addMode(control.assignments().zones[0]?.id ?? '');
  await settle();
  const fixture = TestBed.createComponent(Host);
  const host = fixture.componentInstance;
  host.modeId = control.assignments().zones[0]?.modes[0]?.id ?? '';
  await fixture.whenStable();
  const element = fixture.nativeElement as HTMLElement;
  const field = (id: string): HTMLElement => {
    const found = element.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    if (!found) {
      throw new Error(`no ${id}`);
    }
    return found;
  };
  const choose = (id: string, value: string) => {
    const select = field(id) as HTMLSelectElement;
    select.value = value;
    select.dispatchEvent(new Event('change'));
  };
  const type = (id: string, value: string) => {
    const input = field(id) as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('change'));
  };
  const trackId = project.trackList()[0]?.id ?? '';
  return { fixture, host, element, field, choose, type, control, trackId };
}

describe('AssignmentInspector', () => {
  it('shows the input and edits it', async () => {
    const { host, field, choose, type, fixture } = await setUp();
    expect((field('assignment-name') as HTMLInputElement).value).toBe('Fader');
    // The ports include Anode's virtual input.
    expect([...(field('assignment-port') as HTMLSelectElement).options].map(o => o.text)).toContain('Anode In');
    choose('assignment-port', 'anode:in');
    choose('assignment-channel', '3');
    choose('assignment-kind', 'note');
    type('assignment-number', '36');
    type('assignment-number', '200'); // past a key's range: not reported
    const inputs = host.changes.map(c => c.input);
    expect(inputs).toEqual([
      expect.objectContaining({ port: 'anode:in' }),
      expect.objectContaining({ channel: 3 }),
      expect.objectContaining({ kind: 'note', number: 0 }),
      expect.objectContaining({ number: 36 })
    ]);
    // Pitch bend has no number.
    host.assignment.update(a => ({ ...a, input: { ...a.input, kind: 'pitchBend' } }));
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="assignment-number"]')).toBeNull();
  });

  it('picks a target by kind, track and parameter', async () => {
    const { host, field, choose, fixture, trackId } = await setUp();
    expect([...(field('assignment-target-track') as HTMLSelectElement).options].map(o => o.text)).toEqual([
      'Selected Track',
      'Keys'
    ]);
    choose('assignment-target-track', trackId);
    expect(host.changes.at(-1)?.target).toEqual({ kind: 'parameter', trackId, address: 'volume' });

    host.assignment.update(a => ({ ...a, target: { kind: 'parameter', trackId, address: 'volume' } }));
    await fixture.whenStable();
    choose('assignment-target-address', 'pan');
    expect(host.changes.at(-1)?.target).toEqual({ kind: 'parameter', trackId, address: 'pan' });

    choose('assignment-target-kind', 'trackSwitch');
    expect(host.changes.at(-1)?.target).toEqual({ kind: 'trackSwitch', trackId, switch: 'mute' });
    host.assignment.update(a => ({ ...a, target: { kind: 'trackSwitch', trackId: null, switch: 'mute' } }));
    await fixture.whenStable();
    choose('assignment-target-switch', 'solo');
    expect(host.changes.at(-1)?.target).toEqual({ kind: 'trackSwitch', trackId: null, switch: 'solo' });

    choose('assignment-target-kind', 'transport');
    host.assignment.update(a => ({ ...a, target: { kind: 'transport', transport: 'play' } }));
    await fixture.whenStable();
    choose('assignment-target-transport', 'stop');
    expect(host.changes.at(-1)?.target).toEqual({ kind: 'transport', transport: 'stop' });
  });

  it('sets the value mode, range and pickup, and moves between modes', async () => {
    const { host, field, choose, type, control } = await setUp();
    choose('assignment-value-mode', 'relativeSignedBit');
    type('assignment-min', '20');
    type('assignment-max', '150');
    (field('assignment-pickup') as HTMLInputElement).click();
    expect(host.changes).toEqual([{ mode: 'relativeSignedBit' }, { min: 0.2 }, { max: 1 }, { pickup: true }]);
    const second = control.assignments().zones[0]?.modes[1]?.id ?? '';
    expect([...(field('assignment-location') as HTMLSelectElement).options].map(o => o.text)).toEqual([
      'Zone 1 ▸ Mode 1',
      'Zone 1 ▸ Mode 2'
    ]);
    choose('assignment-location', second);
    expect(host.moves).toEqual([second]);
  });
});

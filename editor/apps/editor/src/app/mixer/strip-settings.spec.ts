import { TestBed } from '@angular/core/testing';
import type { Track } from '@anode/api-client';
import { StripDefaults } from '@anode/api-client';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { MixerStore } from '../state/mixer.store';
import { ProjectStore } from '../state/project.store';
import { MixerStrip } from './mixer-strip';
import { stripSummary } from './strip-settings';

async function strip(kind: 'audio' | 'aux' = 'audio') {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'Strip' }));
  await project.execute(
    [kind === 'aux' ? { type: 'addAux', name: 'Bus' } : { type: 'addTrack', stereo: true, name: 'Gtr' }],
    'Track'
  );
  const track = () => project.trackList()[0] as Track;
  const fixture = TestBed.createComponent(MixerStrip);
  fixture.componentRef.setInput('track', track());
  await fixture.whenStable();
  return { server, project, track, fixture, element: fixture.nativeElement as HTMLElement };
}

describe('the strip input stage', () => {
  it('summarises only what is set', async () => {
    const plain = (await strip()).track().strip;
    expect(stripSummary(plain)).toBe('');
    expect(stripSummary({ ...plain, trimDb: 3, polarityInverted: true })).toBe('+3.0 dB ø');
    expect(stripSummary({ ...plain, trimDb: -1.5, delayMs: -12, dualPan: true })).toBe('-1.5 dB -12 ms Dual');
  });

  it('sends a change, shows it on the strip, and undoes as one step', async () => {
    const { project, track, fixture, element } = await strip();
    const mixer = TestBed.inject(MixerStore);
    await mixer.setStripInput(track().id, { trimDb: 6, polarityInverted: true, delayMs: 20 });
    expect(track().strip).toMatchObject({ trimDb: 6, polarityInverted: true, delayMs: 20 });

    fixture.componentRef.setInput('track', track());
    await fixture.whenStable();
    expect(element.querySelector('[data-testid="strip-settings"]')?.textContent).toContain('+6.0 dB ø +20 ms');

    await project.undo();
    expect(track().strip).toMatchObject(StripDefaults);
  });

  it('is refused on the master, a VCA and out of range, as the Server does', async () => {
    const { server, track } = await strip();
    await expect(server.applyCommands({ commands: [{ type: 'setStrip', trimDb: 3 }], label: 'x' })).rejects.toThrow(
      'master'
    );
    await expect(
      server.applyCommands({ commands: [{ type: 'setStrip', trackId: track().id, trimDb: 30 }], label: 'x' })
    ).rejects.toThrow('24');
    await expect(
      server.applyCommands({ commands: [{ type: 'setStrip', trackId: track().id, delayMs: 900 }], label: 'x' })
    ).rejects.toThrow('500');
    await server.applyCommands({ commands: [{ type: 'addVca', name: 'V' }], label: 'x' });
    const vca = server.current?.tracks.at(-1)?.id ?? '';
    await expect(
      server.applyCommands({ commands: [{ type: 'setStrip', trackId: vca, dualPan: true }], label: 'x' })
    ).rejects.toThrow('no trim');
    await settle();
  });

  it('keeps the pan law as a project setting, undoable', async () => {
    const { project } = await strip();
    const mixer = TestBed.inject(MixerStore);
    expect(project.settings().mixer.panLaw).toBe('minus3DbCompensated');
    await mixer.setMixerSettings({ panLaw: 'minus6Db' });
    expect(project.settings().mixer.panLaw).toBe('minus6Db');
    await project.undo();
    expect(project.settings().mixer.panLaw).toBe('minus3DbCompensated');
  });

  it('offers the row on full strips and the master, not a VCA', async () => {
    const { element, fixture, track, project } = await strip('aux');
    expect(track().kind).toBe('aux');
    expect(element.querySelector('[data-testid="strip-settings"]')).not.toBeNull();
    fixture.componentRef.setInput('track', null);
    await fixture.whenStable();
    expect(element.querySelector('[data-testid="strip-settings"]')).not.toBeNull();
    await project.execute([{ type: 'addVca', name: 'V' }], 'VCA');
    fixture.componentRef.setInput('track', project.trackList().at(-1));
    await fixture.whenStable();
    expect(element.querySelector('[data-testid="strip-settings"]')).toBeNull();
  });

  it('keeps a meter type and tap on the strip, the master too', async () => {
    const { project, track } = await strip();
    const mixer = TestBed.inject(MixerStore);
    await mixer.setMeter(track().id, { meterType: 'k14' });
    await mixer.setMeter(track().id, { meterTap: 'preFader' });
    await mixer.setMeter(null, { meterType: 'lufs' });
    expect(track().strip.meter).toEqual({ type: 'k14', tap: 'preFader' });
    expect(project.master().meter).toEqual({ type: 'lufs', tap: 'postFader' });
    expect(stripSummary(track().strip)).toBe('K-14 Pre');
    await project.undo();
    expect(project.master().meter.type).toBe('peak');
  });
});

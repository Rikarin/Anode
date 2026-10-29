import { TestBed } from '@angular/core/testing';
import type { PluginInfo, Track } from '@anode/api-client';
import { targetsOf } from '../automation/targets';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { MixerStore } from '../state/mixer.store';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';
import { stripEntries } from './mixer-pane';
import { MixerStrip } from './mixer-strip';
import { effectMenu, isMidiEffect, midiEffectMenu } from './plugin-menu';

function plugin(id: string, name: string, category: string, change: Partial<PluginInfo> = {}): PluginInfo {
  return {
    id,
    name,
    vendor: 'Anode',
    version: '1',
    format: 'builtin',
    isInstrument: category === 'Instrument',
    fileTypes: [],
    parameters: [],
    category,
    hasSidechain: false,
    hasEditor: false,
    ...change
  };
}

async function synthStrip() {
  const server = setUpFakeServer();
  const project = TestBed.inject(ProjectStore);
  project.load(await server.createProject({ name: 'MIDI FX' }));
  await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: true, name: 'Synth' }], 'Track');
  await TestBed.inject(PluginsStore).refetchPlugins();
  const track = () => project.trackList()[0] as Track;
  const fixture = TestBed.createComponent(MixerStrip);
  fixture.componentRef.setInput('track', track());
  fixture.componentRef.setInput('layout', { input: false, midiEffects: 1, instrument: true, inserts: 1, sends: 1 });
  await fixture.whenStable();
  const refresh = async () => {
    fixture.componentRef.setInput('track', track());
    await fixture.whenStable();
  };
  return { server, project, track, fixture, element: fixture.nativeElement as HTMLElement, refresh };
}

describe('MIDI FX slots', () => {
  const catalogue = [
    plugin('anode.arpeggiator', 'Arpeggiator', 'MIDI Effect'),
    plugin('vendor.chorder', 'Chorder', 'MIDI Effect', { format: 'clap' }),
    plugin('anode.chord-trigger', 'Chord Trigger', 'MIDI Effect'),
    plugin('anode.eq', 'Channel EQ', 'EQ'),
    plugin('anode.synth', 'Synth', 'Instrument')
  ];

  it('lists MIDI effects for MIDI FX slots only, built-in first, and keeps them out of the Audio FX menu', () => {
    expect(midiEffectMenu(catalogue).map(p => p.name)).toEqual(['Arpeggiator', 'Chord Trigger', 'Chorder']);
    expect(effectMenu(catalogue).map(g => g.category)).toEqual(['EQ']);
    expect(isMidiEffect(catalogue[0] as PluginInfo)).toBe(true);
    expect(isMidiEffect(catalogue[4] as PluginInfo)).toBe(false);
  });

  it('gathers a DMD’s pads after its strip; other stacks’ members stay apart', () => {
    const t = (id: string, change: Partial<Track> = {}) =>
      ({ id, parentId: null, drumMachine: null, ...change }) as unknown as Track;
    const entries = stripEntries([
      t('dmd', { drumMachine: { pads: [] } }),
      t('kick', { parentId: 'dmd' }),
      t('snare', { parentId: 'dmd' }),
      t('stack'),
      t('member', { parentId: 'stack' }),
      t('audio')
    ]);
    expect(entries.map(e => [e.track.id, e.pads.map(p => p.id)])).toEqual([
      ['dmd', ['kick', 'snare']],
      ['stack', []],
      ['member', []],
      ['audio', []]
    ]);
  });

  it('automates a slot’s parameters as midiEffect:{slotId}:{parameterId}, under its name', () => {
    const arp = plugin('anode.arpeggiator', 'Arpeggiator', 'MIDI Effect', {
      parameters: [{ id: 'rate', name: 'Rate', min: 0, max: 5, default: 3, unit: '', steps: 5, valueNames: null }]
    });
    const track = {
      kind: 'instrument',
      instrument: null,
      strip: {
        volumeDb: 0,
        pan: 0,
        mute: false,
        solo: false,
        inserts: [],
        sends: [],
        output: 'master',
        groups: [],
        vcaId: null,
        midiEffects: [
          {
            id: 'ins_1',
            pluginId: arp.id,
            name: 'Arpeggiator',
            bypassed: false,
            parameters: { rate: 2 },
            sidechain: null
          }
        ]
      }
    } as unknown as Track;
    const target = targetsOf(track, id => (id === arp.id ? arp : undefined)).find(t =>
      t.target.startsWith('midiEffect')
    );
    expect(target).toMatchObject({ target: 'midiEffect:ins_1:rate', name: 'Rate', group: 'Arpeggiator', default: 2 });
  });

  it('shows MIDI FX slots and the instrument on an instrument strip; adds, opens and removes a slot', async () => {
    const { project, track, element, refresh } = await synthStrip();
    const group = element.querySelector('[role="group"][aria-label="MIDI FX of Synth"]');
    expect(group).not.toBeNull();
    expect(group?.querySelector('[data-testid="midi-fx-empty"]')?.textContent).toContain('MIDI FX');
    const instrument = element.querySelector('[data-testid="instrument-slot"]');
    expect(instrument?.getAttribute('aria-label')).toMatch(/^Instrument of Synth: /);

    const mixer = TestBed.inject(MixerStore);
    await mixer.addInsert(track().id, 'anode.arpeggiator', undefined, 'midiEffects');
    await mixer.addInsert(track().id, 'anode.chord-trigger', undefined, 'midiEffects');
    await refresh();
    expect(track().strip.midiEffects.map(i => i.name)).toEqual(['Arpeggiator', 'Chord Trigger']);
    expect(track().strip.inserts).toEqual([]);
    const slots = element.querySelectorAll('[data-testid="midi-fx-slot"]');
    expect(slots.length).toBe(2);

    // The name opens the slot's generic window.
    const name = element.querySelector<HTMLButtonElement>('button[aria-label="MIDI FX 1 on Synth: Arpeggiator"]');
    name?.click();
    await settle();
    expect(TestBed.inject(UiStore).pluginWindows()).toEqual([
      { trackId: track().id, insertId: track().strip.midiEffects[0]?.id, chain: 'midiEffects' }
    ]);

    // ⌥↓ moves it below the Chord Trigger.
    name?.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', altKey: true }));
    await settle();
    await refresh();
    expect(track().strip.midiEffects.map(i => i.name)).toEqual(['Chord Trigger', 'Arpeggiator']);

    const arp = track().strip.midiEffects[1];
    if (arp) {
      await mixer.removeInsert(track().id, arp);
    }
    expect(project.trackList()[0]?.strip.midiEffects.map(i => i.name)).toEqual(['Chord Trigger']);
  });

  it('has no MIDI FX section on audio strips, only a spacer for the rows', async () => {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Audio' }));
    await project.execute([{ type: 'addTrack', kind: 'audio', stereo: false }], 'Track');
    const fixture = TestBed.createComponent(MixerStrip);
    fixture.componentRef.setInput('track', project.trackList()[0]);
    fixture.componentRef.setInput('layout', { input: false, midiEffects: 2, instrument: true, inserts: 1, sends: 1 });
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="midi-fx-empty"]')).toBeNull();
    expect(element.querySelector('[data-testid="instrument-slot"]')).toBeNull();
    expect(element.querySelector('[data-testid="insert-empty"]')).not.toBeNull();
  });
});

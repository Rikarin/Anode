import { TestBed } from '@angular/core/testing';
import type { LiveMidiEvent } from '@anode/api-client';
import { settle, setUpFakeServer } from '../core/fake/testing';
import { LiveMidiStore } from './live-midi.store';
import { ProjectStore } from './project.store';
import { SelectionStore } from './selection.store';

describe('LiveMidiStore', () => {
  it('sends live MIDI one request at a time, in order, batching what comes meanwhile', async () => {
    const server = setUpFakeServer();
    const project = TestBed.inject(ProjectStore);
    project.load(await server.createProject({ name: 'Live' }));
    await project.execute([{ type: 'addTrack', kind: 'instrument', stereo: false }], 'Track');
    TestBed.inject(SelectionStore).selectTrack(project.trackIds()[0] ?? '');
    const live = TestBed.inject(LiveMidiStore);
    await settle();

    // The first request answers last: the later notes wait for it rather than overtaking it.
    const sent: LiveMidiEvent[][] = [];
    let release: () => void = () => undefined;
    const send = server.sendLiveMidi.bind(server);
    server.sendLiveMidi = (trackId, events) => {
      sent.push(events.map(e => ({ ...e })));
      return sent.length === 1
        ? new Promise<void>(resolve => (release = resolve)).then(() => send(trackId, events))
        : send(trackId, events);
    };
    live.noteOn(50, 90);
    live.noteOff(50);
    live.noteOn(53, 80);
    live.noteOn(57, 100);
    await settle();
    expect(sent).toHaveLength(1);
    release();
    await settle();
    expect(sent.map(batch => batch.map(e => [e.status & 0xf0, e.data1]))).toEqual([
      [[0x90, 50]],
      [
        [0x80, 50],
        [0x90, 53],
        [0x90, 57]
      ]
    ]);
  });
});

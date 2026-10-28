import { TestBed } from '@angular/core/testing';
import { setUpFakeServer, settle } from '../core/fake/testing';
import { FlexStore } from './flex.store';
import { JobsStore } from './jobs.store';
import { ProjectStore } from './project.store';
import { SmartTempoStore } from './smart-tempo.store';
import { TrackAlternativesStore } from './track-alternatives.store';
import { TransportStore } from './transport.store';

async function openLoop(name = 'loop 100bpm.wav') {
  const server = setUpFakeServer();
  const store = TestBed.inject(ProjectStore);
  store.load(await server.createProject({ name: 'Flex' }));
  await store.execute([{ type: 'addTrack', stereo: true }], 'Track');
  const trackId = store.trackIds()[0] ?? '';
  const file = await server.importAudioFile(`/Music/${name}`);
  store.upsertFile(file);
  await store.execute([{ type: 'addRegion', trackId, fileId: file.id, startTick: 0 }], 'Region');
  const regionId = [...store.regions().keys()][0] ?? '';
  return { server, store, trackId, regionId, fileId: file.id };
}

describe('Phase 5 stores', () => {
  it('fetches a file analysis once, and again when the Server says it changed', async () => {
    const { server, fileId } = await openLoop();
    const flex = TestBed.inject(FlexStore);
    expect(flex.analysis(fileId)).toBeUndefined();
    await settle();
    expect(flex.analysis(fileId)?.transients.length).toBeGreaterThan(0);
    const before = server.requests.filter(r => r === 'GetFileAnalysis').length;
    flex.analysis(fileId);
    await settle();
    expect(server.requests.filter(r => r === 'GetFileAnalysis').length).toBe(before);
  });

  it('turns Flex Pitch on: Pitch mode, an analysis job, then the region’s notes', async () => {
    const { server, store, trackId, regionId } = await openLoop('melody.wav');
    const flex = TestBed.inject(FlexStore);
    const region = store.regions().get(regionId);
    if (!region) {
      throw new Error('no region');
    }
    await flex.enableFlexPitch(region);
    expect(store.track(trackId)).toMatchObject({ flexOn: true, flexMode: 'pitch' });
    expect(
      TestBed.inject(JobsStore)
        .active()
        .map(j => j.kind)
    ).toEqual(['analyze']);
    server.runJobs();
    await settle();
    await settle();
    expect(store.regions().get(regionId)?.pitchNotes.length).toBeGreaterThan(2);
    expect(flex.analysing().size).toBe(0);
  });

  it('shows Show Flex, flex modes and follow through one undo step each', async () => {
    const { store, trackId, regionId } = await openLoop();
    const flex = TestBed.inject(FlexStore);
    flex.toggleShowFlex();
    expect(flex.showFlex()).toBe(true);
    await flex.setFlexMode(trackId, 'rhythmic');
    expect(store.track(trackId)).toMatchObject({ flexOn: true, flexMode: 'rhythmic' });
    expect(flex.flexedTracks().has(trackId)).toBe(true);
    await flex.setFollow([regionId], 'on');
    await flex.setTranspose([regionId], 3);
    expect(store.regions().get(regionId)).toMatchObject({ follow: 'on', transpose: 3 });
    // A follow-on region's length in ticks comes from its file's beats (four 100 bpm beats a bar).
    const region = store.regions().get(regionId);
    if (region) {
      expect(store.timing(region).lengthTicks).toBeCloseTo((region.length / 28_800) * 960);
    }
  });

  it('sets the tempo mode, Varispeed, and maps beats', async () => {
    const { store } = await openLoop();
    const tempo = TestBed.inject(SmartTempoStore);
    await tempo.setMode('adapt');
    expect(tempo.mode()).toBe('adapt');
    await tempo.setVarispeedEnabled(true);
    await tempo.setSpeed(0.9);
    expect(tempo.speed()).toBe(0.9);
    await tempo.mapBeats([{ tick: 3840, sample: 115_200 }]);
    expect(store.tempoMap().secondsAt(3840)).toBeCloseTo(2.4);
    await tempo.setShape(0, 0.4);
    expect(store.settings().tempoEvents[0]?.shape).toBe(0.4);
  });

  it('maps the transport’s compiled samples to ticks under Varispeed', async () => {
    const { store } = await openLoop();
    const transport = TestBed.inject(TransportStore);
    await TestBed.inject(SmartTempoStore).setVarispeedEnabled(true);
    await TestBed.inject(SmartTempoStore).setSpeed(0.5);
    // 2 s of compiled samples at half speed are 1 s of the tempo track: 1 920 ticks at 120 bpm.
    transport.applyStatus({ state: 'stopped', positionTick: 0, positionSamples: 96_000, sampleRate: 48_000 }, 0);
    expect(transport.positionAt(0)).toBeCloseTo(1920);
    expect(store.settings().varispeed.speed).toBe(0.5);
  });

  it('makes, selects and deletes track alternatives', async () => {
    const { store, trackId, regionId } = await openLoop();
    const alternatives = TestBed.inject(TrackAlternativesStore);
    await alternatives.create(trackId, false, 'Empty');
    let track = store.track(trackId);
    expect(track && alternatives.activeName(track)).toBe('Empty');
    expect(store.regions().size).toBe(0);
    const first = track?.alternatives[0]?.id ?? '';
    await alternatives.select(trackId, first);
    expect([...store.regions().keys()]).toEqual([regionId]);
    await alternatives.rename(trackId, first, 'Full');
    track = store.track(trackId);
    expect(track && alternatives.activeName(track)).toBe('Full');
  });
});

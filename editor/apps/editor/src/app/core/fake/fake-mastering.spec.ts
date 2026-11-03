import {
  ApiError,
  DefaultMasteringSettings,
  type EditCommand,
  MasteringBands,
  MasteringParameters,
  type ServerMessage
} from '@anode/api-client';
import { characterCurve, fakeMasteringAnalysis } from './fake-mastering';
import { FakeT3Effects, FakeT3Instruments } from './fake-plugins-phase11';
import { FakeAnodeServer } from './fake-server';

async function status(run: () => Promise<unknown>): Promise<number> {
  try {
    await run();
    return 200;
  } catch (error) {
    return error instanceof ApiError ? error.status : 500;
  }
}

async function setUp() {
  let now = 0;
  const server = new FakeAnodeServer({ now: () => now });
  await server.createProject({ name: 'Song' });
  await server.applyCommands({ commands: [{ type: 'addTrack', kind: 'instrument', stereo: true }] });
  const run = (command: EditCommand) => server.applyCommands({ commands: [command] });
  const assistant = () => server.current?.master.inserts.find(i => i.pluginId === MasteringParameters.pluginId);
  return { server, run, assistant, later: (ms: number) => (now += ms) };
}

describe('FakeAnodeServer, the Mastering Assistant (Phase 11)', () => {
  it('has the T3 instruments and effects and the assistant in the catalogue', async () => {
    const { server } = await setUp();
    const ids = (await server.plugins()).map(p => p.id);
    for (const plugin of [...FakeT3Instruments, ...FakeT3Effects]) {
      expect(ids).toContain(plugin.id);
    }
    const info = FakeT3Effects.find(p => p.id === MasteringParameters.pluginId);
    expect(info?.parameters.map(p => p.id)).toEqual(Object.values(MasteringParameters).slice(1));
    expect(info?.parameters.find(p => p.id === 'targetLufs')?.default).toBe(-14);
  });

  it('answers 404 without an assistant; analyse adds one last on the master and stores its analysis', async () => {
    const { server, assistant } = await setUp();
    expect(await status(() => server.mastering())).toBe(404);
    expect(await status(() => server.analyseMastering({ add: false }))).toBe(404);
    const job = await server.analyseMastering({ add: true });
    expect(job.kind).toBe('masteringAnalysis');
    expect(server.current?.master.inserts.at(-1)?.pluginId).toBe(MasteringParameters.pluginId);
    expect((await server.mastering()).analysis).toBeNull();
    server.runJobs();
    const state = await server.mastering();
    expect(state.insertId).toBe(assistant()?.id);
    expect(state.settings).toEqual(DefaultMasteringSettings);
    expect(state.analysis?.spectrumDb).toHaveLength(MasteringBands.length);
    expect(state.analysis?.revision).toBe(1);
    // Analysing again adds no second assistant, and counts the revision up.
    await server.analyseMastering({ add: true });
    server.runJobs();
    expect(server.current?.master.inserts.filter(i => i.pluginId === MasteringParameters.pluginId)).toHaveLength(1);
    expect((await server.mastering()).analysis?.revision).toBe(2);
    // Adding the assistant and the analysis are two undo steps.
    await server.undo();
    expect((await server.mastering()).analysis?.revision).toBe(1);
  });

  it('sets the settings as the insert’s parameters, in their ranges', async () => {
    const { server, run, assistant } = await setUp();
    expect(await status(() => run({ type: 'setMastering', character: 'valve' }))).toBe(422);
    await run({ type: 'addInsert', pluginId: MasteringParameters.pluginId });
    await run({ type: 'setMastering', character: 'valve', targetLufs: -11, matchedBypass: true });
    expect(assistant()?.parameters).toMatchObject({ character: 1, targetLufs: -11, matchedBypass: 1, width: 1 });
    for (const bad of [{ targetLufs: -30 }, { ceilingDbtp: 1 }, { width: 3 }, { eq: -0.1 }]) {
      expect(await status(() => run({ type: 'setMastering', ...bad }))).toBe(422);
    }
    expect(await status(() => run({ type: 'setMastering', insertId: 'ins_nope', width: 1 }))).toBe(422);
    await server.setMastering({ ...DefaultMasteringSettings, character: 'punch' });
    expect((await server.mastering()).settings.character).toBe('punch');
    const current = server.current;
    if (current) {
      const analysis = { ...fakeMasteringAnalysis(current, DefaultMasteringSettings, 0, 3840, 1), spectrumDb: [0] };
      const insertId = assistant()?.id ?? '';
      expect(await status(() => run({ type: 'setMasteringAnalysis', insertId, analysis }))).toBe(422);
    }
  });

  it('reports live loudness and gain reduction while playing', async () => {
    const { server, later } = await setUp();
    const readings: Extract<ServerMessage, { type: 'mastering' }>[] = [];
    server.subscribe(m => m.type === 'mastering' && readings.push(m));
    await server.controlTransport({ command: 'play' });
    later(50);
    server.advance();
    expect(readings).toHaveLength(0);
    await server.analyseMastering({ add: true });
    server.runJobs();
    later(50);
    server.advance();
    expect(readings.at(-1)?.integratedLufs).toBe(-14);
    expect(readings.at(-1)?.limiterReductionDb).toBeLessThanOrEqual(0);
    await server.controlTransport({ command: 'stop' });
  });

  it('draws a curve per character', () => {
    const clean = characterCurve('clean');
    expect(clean).toHaveLength(30);
    expect(characterCurve('valve')).not.toEqual(clean);
    // Valve lifts the lows against Clean, Transparent stays near flat.
    expect(characterCurve('valve')[5] ?? 0).toBeGreaterThan(clean[5] ?? 0);
    expect(Math.max(...characterCurve('transparent').map(Math.abs))).toBeLessThan(1.5);
  });
});

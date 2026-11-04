import type { EditCommand, Project } from '@anode/api-client';
import { FakeAnodeServer } from '../core/fake/fake-server';
import { moveIndex } from './track-commands';

const tracks = [
  { id: 'x', parentId: null },
  { id: 's', parentId: null },
  { id: 'a', parentId: 's' },
  { id: 'b', parentId: 's' },
  { id: 'y', parentId: null }
];

describe('moveIndex', () => {
  it('moves past a stack and its members in one step', () => {
    expect(moveIndex(tracks, 'x', 1)).toBe(1);
    expect(moveIndex(tracks, 'y', -1)).toBe(1);
    expect(moveIndex(tracks, 's', -1)).toBe(0);
    expect(moveIndex(tracks, 's', 1)).toBe(4);
  });

  it('keeps a member among its stack’s members', () => {
    expect(moveIndex(tracks, 'a', 1)).toBe(3);
    expect(moveIndex(tracks, 'b', -1)).toBe(2);
    expect(moveIndex(tracks, 'a', -1)).toBeNull();
    expect(moveIndex(tracks, 'b', 1)).toBeNull();
  });

  it('stops at the ends, and knows no other track', () => {
    expect(moveIndex(tracks, 'x', -1)).toBeNull();
    expect(moveIndex(tracks, 'y', 1)).toBeNull();
    expect(moveIndex(tracks, 'nope', 1)).toBeNull();
  });
});

describe('moving tracks around stacks (the fake Server, as the Model)', () => {
  async function stacked(): Promise<{ server: FakeAnodeServer; id: (name: string) => string }> {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Test' });
    for (const name of ['A', 'B', 'C', 'D']) {
      await server.applyCommands({ commands: [{ type: 'addTrack', stereo: false, name }] });
    }
    const id = (name: string): string => server.current?.tracks.find(t => t.name === name)?.id ?? '';
    await server.applyCommands({ commands: [{ type: 'createStack', trackIds: [id('A'), id('B')], name: 'Stack' }] });
    return { server, id };
  }

  async function names(server: FakeAnodeServer, command: EditCommand): Promise<string[]> {
    await server.applyCommands({ commands: [command] });
    return (server.current as Project).tracks.map(t => t.name);
  }

  it('puts a track moved among a stack’s members after them', async () => {
    const { server, id } = await stacked();
    expect(await names(server, { type: 'moveTrack', trackId: id('D'), index: 2 })).toEqual([
      'Stack',
      'A',
      'B',
      'D',
      'C'
    ]);
  });

  it('moves a stack with its members, and a member within its stack', async () => {
    const { server, id } = await stacked();
    expect(await names(server, { type: 'moveTrack', trackId: id('Stack'), index: 4 })).toEqual([
      'C',
      'D',
      'Stack',
      'A',
      'B'
    ]);
    expect(await names(server, { type: 'moveTrack', trackId: id('A'), index: 4 })).toEqual([
      'C',
      'D',
      'Stack',
      'B',
      'A'
    ]);
  });

  it('keeps a Drummer’s pads with it', async () => {
    const server = new FakeAnodeServer();
    await server.createProject({ name: 'Test' });
    await server.applyCommands({ commands: [{ type: 'addTrack', stereo: false, name: 'A' }] });
    await server.applyCommands({
      commands: [{ type: 'addSessionPlayerTrack', player: 'drummer', styleId: 'drums.rock', regions: false }]
    });
    const before = server.current?.tracks ?? [];
    const drummer = before.find(t => t.kind === 'drummer');
    await server.applyCommands({ commands: [{ type: 'moveTrack', trackId: drummer?.id ?? '', index: 0 }] });
    const after = server.current?.tracks ?? [];
    expect(after).toHaveLength(before.length);
    expect(after[0]?.id).toBe(drummer?.id);
    expect(after.at(-1)?.name).toBe('A');
  });
});

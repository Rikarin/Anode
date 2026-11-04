import type { Provider } from '@angular/core';
import { ANODE_API } from '../api';
import { FakeAnodeServer } from './fake-server';
import { FAKE_SERVER } from './fake-token';

export { FAKE_SERVER, fakeMode } from './fake-token';

/** Runs the editor against `server` instead of a real one: its REST API and its `/ws` messages. */
export function provideFakeServer(server: FakeAnodeServer = new FakeAnodeServer()): Provider[] {
  return [
    { provide: FAKE_SERVER, useValue: server },
    { provide: ANODE_API, useValue: server }
  ];
}

/** `?fake`'s providers (main.ts loads this module only then): a realtime fake Server, with the demo project or none. */
export function fakeProviders(mode: 'demo' | 'offline' | 'empty'): Provider[] {
  return provideFakeServer(
    new FakeAnodeServer({ realtime: true, demo: mode !== 'empty', offline: mode === 'offline' })
  );
}

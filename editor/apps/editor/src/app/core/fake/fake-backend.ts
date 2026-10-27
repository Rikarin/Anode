import { InjectionToken, type Provider } from '@angular/core';
import { ANODE_API } from '../api';
import { FakeAnodeServer } from './fake-server';

/** The in-memory Server, when the editor runs against one (unit tests, `?fake`); absent otherwise. */
export const FAKE_SERVER = new InjectionToken<FakeAnodeServer>('FakeAnodeServer');

/** Runs the editor against `server` instead of a real one: its REST API and its `/ws` messages. */
export function provideFakeServer(server: FakeAnodeServer = new FakeAnodeServer()): Provider[] {
  return [
    { provide: FAKE_SERVER, useValue: server },
    { provide: ANODE_API, useValue: server }
  ];
}

const FakeKey = 'anode.fake';

/**
 * Whether the page asks for the fake Server, and with what: `?fake` in the address (`http://localhost:4200/?fake`)
 * opens a demo project, `?fake&empty` starts at the project chooser. For working on the editor without anode-server;
 * never in the desktop app. Remembered for the tab, since the router drops the query from the address.
 */
export function fakeMode(location: Pick<Location, 'search'> = window.location): 'demo' | 'empty' | null {
  if (window.anode) {
    return null;
  }

  const query = new URLSearchParams(location.search);
  if (query.has('fake')) {
    const mode = query.has('empty') ? 'empty' : 'demo';
    try {
      sessionStorage.setItem(FakeKey, mode);
    } catch {
      // blocked storage: fake mode for this load only
    }
    return mode;
  }

  try {
    const stored = sessionStorage.getItem(FakeKey);
    return stored === 'demo' || stored === 'empty' ? stored : null;
  } catch {
    return null;
  }
}

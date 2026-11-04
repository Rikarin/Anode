import { InjectionToken } from '@angular/core';
import type { FakeAnodeServer } from './fake-server';

/**
 * The in-memory Server, when the editor runs against one (unit tests, `?fake`); absent otherwise. Its own module, with
 * {@link fakeMode}, so the app's code that asks for it does not pull the fake Server into the initial bundle (main.ts
 * loads it only in fake mode).
 */
export const FAKE_SERVER = new InjectionToken<FakeAnodeServer>('FakeAnodeServer');

const FakeKey = 'anode.fake';

/**
 * Whether the page asks for the fake Server, and with what: `?fake` in the address (`http://localhost:4200/?fake`)
 * opens a demo project, `?fake&offline` the demo with its audio files offline, `?fake&empty` starts at the project
 * chooser. For working on the editor without anode-server;
 * never in the desktop app. Remembered for the tab, since the router drops the query from the address.
 */
export function fakeMode(location: Pick<Location, 'search'> = window.location): 'demo' | 'offline' | 'empty' | null {
  if (window.anode) {
    return null;
  }

  const query = new URLSearchParams(location.search);
  if (query.has('fake')) {
    const mode = query.has('empty') ? 'empty' : query.has('offline') ? 'offline' : 'demo';
    try {
      sessionStorage.setItem(FakeKey, mode);
    } catch {
      // blocked storage: fake mode for this load only
    }
    return mode;
  }

  try {
    const stored = sessionStorage.getItem(FakeKey);
    return stored === 'demo' || stored === 'offline' || stored === 'empty' ? stored : null;
  } catch {
    return null;
  }
}

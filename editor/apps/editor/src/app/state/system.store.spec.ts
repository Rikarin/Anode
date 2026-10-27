import { TestBed } from '@angular/core/testing';
import { SystemStore } from './system.store';

describe('SystemStore', () => {
  it('applies engine events to the fetched system info', () => {
    const store = TestBed.inject(SystemStore);
    store.applyEngine({ state: 'connected' });
    expect(store.engineState()).toBeNull(); // nothing fetched yet: nothing to update

    store.info.set({ version: '0.1.0-dev', engine: { state: 'disconnected', message: 'Not connected yet.' } });
    store.applyEngine({ state: 'connected', hostVersion: '0.1.0-dev', backend: 'coreaudio' });
    expect(store.engineState()).toBe('connected');
    expect(store.info()?.version).toBe('0.1.0-dev');
  });
});

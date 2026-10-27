import { type ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withHashLocation } from '@angular/router';
import { appRoutes } from './app.routes';
import { fakeMode, provideFakeServer } from './core/fake/fake-backend';
import { FakeAnodeServer } from './core/fake/fake-server';

/**
 * `?fake` runs the editor against the in-memory Server (with a demo project; `?fake&empty` starts at the project
 * chooser), for working on the editor without anode-server.
 */
function fakeProviders() {
  const mode = fakeMode();
  return mode ? provideFakeServer(new FakeAnodeServer({ realtime: true, demo: mode === 'demo' })) : [];
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // Hash URLs: the desktop app serves the editor from a custom protocol with no server-side fallback route.
    provideRouter(appRoutes, withHashLocation()),
    ...fakeProviders()
  ]
};

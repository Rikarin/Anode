import type { Provider } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { appConfig } from './app/app.config';
import { fakeMode } from './app/core/fake/fake-token';

/** `?fake` runs the editor against the in-memory Server, loaded only then (it is not in the initial bundle). */
async function fakeProviders(): Promise<Provider[]> {
  const mode = fakeMode();
  return mode ? (await import('./app/core/fake/fake-backend')).fakeProviders(mode) : [];
}

fakeProviders()
  .then(extra => bootstrapApplication(App, { ...appConfig, providers: [...appConfig.providers, ...extra] }))
  .catch((err: unknown) => console.error(err));

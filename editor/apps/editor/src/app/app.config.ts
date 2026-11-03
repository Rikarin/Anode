import { type ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter, withHashLocation } from '@angular/router';
import { appRoutes } from './app.routes';

/**
 * The app's providers. `?fake` (core/fake/fake-token.ts) adds the in-memory Server's, which main.ts loads lazily so the
 * fake Server stays out of the initial bundle.
 */
export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // Hash URLs: the desktop app serves the editor from a custom protocol with no server-side fallback route.
    provideRouter(appRoutes, withHashLocation())
  ]
};

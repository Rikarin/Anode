import type { Route } from '@angular/router';

export const appRoutes: Route[] = [
  { path: '', loadComponent: () => import('./pages/main-window').then(m => m.MainWindow), title: 'Anode' },
  // Phase 10 (docs/11-phase-10.md): the touch remote for phones and tablets on the LAN.
  {
    path: 'remote',
    loadComponent: () => import('./remote/remote-page').then(m => m.RemotePage),
    title: 'Anode Remote'
  },
  { path: '**', redirectTo: '' }
];

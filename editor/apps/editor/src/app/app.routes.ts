import type { Route } from '@angular/router';

export const appRoutes: Route[] = [
  { path: '', loadComponent: () => import('./pages/main-window').then(m => m.MainWindow), title: 'Anode' },
  { path: '**', redirectTo: '' }
];

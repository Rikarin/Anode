import { TestBed } from '@angular/core/testing';
import { appRoutes } from '../app.routes';
import { RemotePage } from './remote-page';

describe('the remote route (Phase 10 placeholder)', () => {
  it('is its own lazily loaded route', async () => {
    const route = appRoutes.find(r => r.path === 'remote');
    expect(route?.loadComponent).toBeDefined();
    expect(await route?.loadComponent?.()).toBe(RemotePage);
  });

  it('renders', () => {
    const fixture = TestBed.createComponent(RemotePage);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="remote"]')).not.toBeNull();
  });
});

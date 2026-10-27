import { TestBed } from '@angular/core/testing';
import { XuiToastService } from '@xui/toast';
import { EventStream } from '../events';
import { provideFakeServer } from './fake-backend';
import { FakeAnodeServer, type FakeServerOptions } from './fake-server';

/**
 * Sets up TestBed with an in-memory Server whose `/ws` messages reach the stores, and no toasts. Returns the server;
 * `now` in the options drives its clock.
 */
export function setUpFakeServer(options: FakeServerOptions = {}): FakeAnodeServer {
  const server = new FakeAnodeServer(options);
  TestBed.configureTestingModule({
    providers: [...provideFakeServer(server), { provide: XuiToastService, useValue: null }]
  });
  TestBed.inject(EventStream).start();
  return server;
}

/** Lets pending promises (the fake answers asynchronously) settle. */
export async function settle(): Promise<void> {
  for (let i = 0; i < 5; i++) {
    await Promise.resolve();
  }
  await new Promise(resolve => setTimeout(resolve, 0));
}

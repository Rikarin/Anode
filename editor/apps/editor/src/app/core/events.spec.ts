import { TestBed } from '@angular/core/testing';
import { EventStream, clientEventsUrl } from './events';

describe('EventStream', () => {
  it('adds the client id to the events URL, keeping the token', () => {
    expect(clientEventsUrl('ws://127.0.0.1:5190/ws?access_token=t', 'abc')).toBe(
      'ws://127.0.0.1:5190/ws?access_token=t&client=abc'
    );
  });

  it('opens on the connected message and asks the stores to resync on resync', () => {
    const events = TestBed.inject(EventStream);
    let resyncs = 0;
    events.connected$.subscribe(() => resyncs++);

    events.receive(JSON.stringify({ type: 'connected' }));
    events.receive(JSON.stringify({ type: 'resync' }));
    events.receive('not json');

    expect(events.status()).toBe('open');
    expect(resyncs).toBe(2);
  });
});

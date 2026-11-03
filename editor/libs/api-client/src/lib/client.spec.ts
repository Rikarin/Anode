import { ApiError, createAnodeClient, eventsUrl, listenInUrl, mediaUrl, unwrap } from './client';
import { ListenIn, readListenInFrame } from './models';

describe('api-client', () => {
  it('sends the bearer token on every request', async () => {
    const seen: Request[] = [];
    const client = createAnodeClient({ baseUrl: 'http://127.0.0.1:5190', token: 't0ken' }, request => {
      seen.push(request as Request);
      return Promise.resolve(
        new Response(JSON.stringify({ version: '1', engine: {} }), {
          headers: { 'Content-Type': 'application/json' }
        })
      );
    });

    await client.GET('/api/v1/system');

    expect(seen[0]?.url).toBe('http://127.0.0.1:5190/api/v1/system');
    expect(seen[0]?.headers.get('Authorization')).toBe('Bearer t0ken');
  });

  it('turns ProblemDetails into an ApiError with the reason', () => {
    const response = new Response(null, { status: 422 });
    expect(() =>
      unwrap({ response, error: { detail: 'The engine could not load the result.', errors: ['no cable'] } })
    ).toThrow(ApiError);
    try {
      unwrap({ response, error: { detail: 'Nope.', errors: ['x'] } });
    } catch (error) {
      expect((error as ApiError).status).toBe(422);
      expect((error as ApiError).errors).toEqual(['x']);
    }
  });

  it('rejects a call the Server refuses, so a caller that does not unwrap still sees the failure', async () => {
    const client = createAnodeClient({ baseUrl: 'http://127.0.0.1:5190', token: null }, () =>
      Promise.resolve(
        Response.json({ title: 'Conflict', detail: 'The engine is not connected.', errors: ['ipc'] }, { status: 503 })
      )
    );

    const error = await client.GET('/api/v1/system').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(503);
    expect((error as ApiError).message).toBe('The engine is not connected.');
    expect((error as ApiError).errors).toEqual(['ipc']);
  });

  it('names the status when a refusal has no ProblemDetails, and the text when it is a plain string', async () => {
    const empty = createAnodeClient({ baseUrl: 'http://127.0.0.1:5190', token: null }, () =>
      Promise.resolve(new Response(null, { status: 401, statusText: 'Unauthorized' }))
    );
    const text = createAnodeClient({ baseUrl: 'http://127.0.0.1:5190', token: null }, () =>
      Promise.resolve(Response.json('Unknown project.', { status: 400 }))
    );

    await expect(empty.GET('/api/v1/system')).rejects.toThrow('401 Unauthorized');
    await expect(text.GET('/api/v1/system')).rejects.toThrow('Unknown project.');
  });

  it('resolves a successful call as before', async () => {
    const client = createAnodeClient({ baseUrl: 'http://127.0.0.1:5190', token: null }, () =>
      Promise.resolve(Response.json({ version: '0.1.0', engine: { state: 'inprocess' } }))
    );

    const result = await client.GET('/api/v1/system');

    expect(result.response.status).toBe(200);
    expect(result.data?.engine.state).toBe('inprocess');
  });

  it('builds the WebSocket URL with the token as a query parameter', () => {
    expect(eventsUrl({ baseUrl: 'https://studio.local:5190', token: 'a b' })).toBe(
      'wss://studio.local:5190/ws?access_token=a+b'
    );
    expect(eventsUrl({ baseUrl: 'http://127.0.0.1:5190', token: null })).toBe('ws://127.0.0.1:5190/ws');
  });

  it('builds the listen-in and media URLs with the token as a query parameter (Phase 10)', () => {
    expect(listenInUrl({ baseUrl: 'https://studio.local:5190', token: 't' })).toBe(
      'wss://studio.local:5190/api/v1/remote/listen?access_token=t'
    );
    expect(
      mediaUrl({ baseUrl: 'http://127.0.0.1:5190', token: null }, '/api/v1/project/movie/frames/12', { height: 64 })
    ).toBe('http://127.0.0.1:5190/api/v1/project/movie/frames/12?height=64');
  });

  it('reads a listen-in frame as the engine writes it (Phase 10, ListenIn)', () => {
    const message = new ArrayBuffer(ListenIn.headerBytes + 3);
    const view = new DataView(message);
    view.setUint32(0, 7, true);
    view.setBigInt64(4, 123_456_789_012n, true);
    view.setUint16(12, 3, true);
    view.setUint8(14, ListenIn.flagPlaying);
    new Uint8Array(message, ListenIn.headerBytes).fill(9);
    const frame = readListenInFrame(message);
    expect(frame && { ...frame, packet: [...frame.packet] }).toEqual({
      sequence: 7,
      transportSample: 123_456_789_012,
      flags: ListenIn.flagPlaying,
      packet: [9, 9, 9]
    });
    expect(readListenInFrame(message.slice(0, ListenIn.headerBytes + 2))).toBeNull();
  });
});

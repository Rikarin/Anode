import { failure } from './startup';

describe('failure', () => {
  it('drops the wrapping Electron adds to an error from the main process', () => {
    expect(failure(new Error("Error invoking remote method 'anode:connection': Error: no engine"))).toBe('no engine');
  });

  it('names the Server a failed fetch could not reach', () => {
    expect(failure(new TypeError('Failed to fetch'), 'http://127.0.0.1:5190')).toBe(
      'No answer from http://127.0.0.1:5190. Is anode-server running?'
    );
  });
});

import { fromLocation } from './connection';

const at = (search: string, origin = 'http://studio.local:5190') => ({ search, origin });

describe('fromLocation', () => {
  it('uses the development Server when the page was not served by one', () => {
    expect(fromLocation(at(''))).toEqual({ baseUrl: 'http://127.0.0.1:5190', token: null });
  });

  it('talks to the origin that served the editor, with the token remembered for it', () => {
    const stored = (baseUrl: string) => (baseUrl === 'http://studio.local:5190' ? 'kept' : null);
    expect(fromLocation(at(''), true, stored)).toEqual({ baseUrl: 'http://studio.local:5190', token: 'kept' });
  });

  it('lets the query override both, trimming a trailing slash', () => {
    expect(fromLocation(at('?server=http://other:5190/&token=typed'), true, () => 'kept')).toEqual({
      baseUrl: 'http://other:5190',
      token: 'typed'
    });
  });
});

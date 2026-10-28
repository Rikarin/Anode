import {
  bindingIndex,
  bindingOf,
  conflicts,
  effectiveBindings,
  formatBinding,
  KeyCommands,
  type KeyStroke,
  normalizeBinding,
  rebind,
  resolveKey
} from './key-commands';

const key = (code: string, key: string, mods: Partial<KeyStroke> = {}): KeyStroke => ({
  code,
  key,
  metaKey: false,
  ctrlKey: false,
  altKey: false,
  shiftKey: false,
  ...mods
});

describe('key commands', () => {
  it('has unique ids and no clashing defaults', () => {
    const ids = KeyCommands.map(c => c.id);
    expect(new Set(ids).size).toBe(ids.length);
    const defaults = KeyCommands.flatMap(c => c.defaults.map(normalizeBinding));
    expect(new Set(defaults).size).toBe(defaults.length);
  });

  it('reads bindings from the physical key, in modifier order', () => {
    expect(bindingOf(key('KeyZ', 'Ω', { metaKey: true, shiftKey: true, altKey: true }), true)).toBe('Meta+Alt+Shift+Z');
    expect(bindingOf(key('Space', ' '), true)).toBe('Space');
    expect(bindingOf(key('Digit3', '£', { shiftKey: true }), true)).toBe('Shift+3');
    expect(bindingOf(key('ShiftLeft', 'Shift', { shiftKey: true }), true)).toBeNull();
    // Off a Mac, Ctrl is the command key.
    expect(bindingOf(key('KeyS', 's', { ctrlKey: true }), false)).toBe('Meta+S');
    expect(bindingOf(key('KeyS', 's', { ctrlKey: true }), true)).toBe('Ctrl+S');
  });

  it('resolves presses to commands', () => {
    const index = bindingIndex(effectiveBindings({}));
    expect(resolveKey(index, key('KeyZ', 'z', { metaKey: true }), true)).toBe('edit.undo');
    expect(resolveKey(index, key('KeyZ', 'z', { metaKey: true, shiftKey: true }), true)).toBe('edit.redo');
    expect(resolveKey(index, key('KeyR', 'R', { shiftKey: true }), true)).toBe('transport.flashback');
    expect(resolveKey(index, key('Digit2', '2'), true)).toBe('screenset.recall2');
    expect(resolveKey(index, key('Backspace', 'Backspace'), true)).toBe('edit.delete');
    expect(resolveKey(index, key('KeyQ', 'q'), true)).toBeNull();
  });

  it('lets the user rebind, taking the binding from whoever had it', () => {
    const user = rebind({}, 'transport.cycle', 'L');
    expect(user).toEqual({ 'transport.cycle': ['L'] });
    const stolen = rebind(user, 'transport.metronome', 'Shift+R');
    expect(stolen['transport.flashback']).toEqual([]);
    const index = bindingIndex(effectiveBindings(stolen));
    expect(resolveKey(index, key('KeyR', 'R', { shiftKey: true }), true)).toBe('transport.metronome');
    expect(resolveKey(index, key('KeyC', 'c'), true)).toBeNull();
    expect(conflicts(effectiveBindings({}), 'Space', 'transport.cycle')).toEqual(['transport.play']);
    // Back to the default: nothing stored.
    expect(rebind(user, 'transport.cycle', 'C')).toEqual({});
  });

  it('formats bindings as macOS does', () => {
    expect(formatBinding('Meta+Shift+Z')).toBe('⇧⌘Z');
    expect(formatBinding('Ctrl+Alt+Enter')).toBe('⌃⌥↩');
  });
});

/**
 * The key command registry (Logic's Key Commands): every command the editor's keys run, with Logic-like default
 * bindings. A binding is a key with its modifiers in a fixed order, `Meta+Shift+Z` (the contract's `KeyCommandSet`
 * form); the user's bindings for a command replace its defaults.
 */
export interface KeyCommand {
  readonly id: string;
  readonly label: string;
  readonly group: KeyCommandGroup;
  readonly defaults: readonly string[];
}

export type KeyCommandGroup = 'Transport' | 'Edit' | 'Regions' | 'Tracks' | 'Tools' | 'View' | 'Screensets' | 'Project';

const screensets: KeyCommand[] = Array.from({ length: 9 }, (_, i) => ({
  id: `screenset.recall${i + 1}`,
  label: `Recall Screenset ${i + 1}`,
  group: 'Screensets' as const,
  defaults: [String(i + 1)]
}));

export const KeyCommands: readonly KeyCommand[] = [
  { id: 'transport.play', label: 'Play or Stop', group: 'Transport', defaults: ['Space'] },
  { id: 'transport.record', label: 'Record', group: 'Transport', defaults: ['R'] },
  { id: 'transport.cycle', label: 'Cycle Mode', group: 'Transport', defaults: ['C'] },
  { id: 'transport.goToStart', label: 'Go to Beginning', group: 'Transport', defaults: ['Enter'] },
  { id: 'transport.flashback', label: 'Capture as Recording (Flashback)', group: 'Transport', defaults: ['Shift+R'] },
  { id: 'transport.autopunch', label: 'Autopunch Mode', group: 'Transport', defaults: ['Meta+Ctrl+P'] },
  { id: 'transport.replace', label: 'Replace Mode', group: 'Transport', defaults: ['Alt+R'] },
  { id: 'transport.metronome', label: 'Metronome Click', group: 'Transport', defaults: ['K'] },
  { id: 'edit.undo', label: 'Undo', group: 'Edit', defaults: ['Meta+Z'] },
  { id: 'edit.redo', label: 'Redo', group: 'Edit', defaults: ['Meta+Shift+Z', 'Meta+Y'] },
  { id: 'edit.delete', label: 'Delete', group: 'Edit', defaults: ['Delete', 'Backspace'] },
  { id: 'edit.selectAll', label: 'Select All', group: 'Edit', defaults: ['Meta+A'] },
  { id: 'region.mute', label: 'Mute/Unmute Regions', group: 'Regions', defaults: ['Ctrl+M'] },
  { id: 'region.join', label: 'Join Regions', group: 'Regions', defaults: ['Meta+J'] },
  { id: 'region.split', label: 'Split Regions at Playhead', group: 'Regions', defaults: ['Meta+T'] },
  { id: 'region.repeat', label: 'Repeat Regions Once', group: 'Regions', defaults: ['Meta+R'] },
  { id: 'region.pack', label: 'Pack Take Folder', group: 'Regions', defaults: ['Meta+Ctrl+F'] },
  { id: 'region.flatten', label: 'Flatten Take Folder', group: 'Regions', defaults: ['Meta+Ctrl+Shift+F'] },
  { id: 'region.toggleTakes', label: 'Open/Close Take Folder', group: 'Regions', defaults: ['Ctrl+F'] },
  { id: 'track.newAudio', label: 'New Audio Track', group: 'Tracks', defaults: ['Meta+Alt+A'] },
  { id: 'track.newInstrument', label: 'New Software Instrument Track', group: 'Tracks', defaults: ['Meta+Alt+S'] },
  { id: 'track.stack', label: 'Create Track Stack', group: 'Tracks', defaults: ['Meta+Shift+D'] },
  { id: 'track.marker', label: 'Create Marker', group: 'Tracks', defaults: ["Alt+'"] },
  { id: 'tools.menu', label: 'Show Tool Menu', group: 'Tools', defaults: ['T'] },
  { id: 'view.globalTracks', label: 'Show/Hide Global Tracks', group: 'View', defaults: ['G'] },
  { id: 'view.zoomIn', label: 'Zoom Horizontal In', group: 'View', defaults: ['Meta+ArrowRight'] },
  { id: 'view.zoomOut', label: 'Zoom Horizontal Out', group: 'View', defaults: ['Meta+ArrowLeft'] },
  { id: 'view.mixer', label: 'Show Mixer', group: 'View', defaults: ['X'] },
  { id: 'view.editors', label: 'Show Editors', group: 'View', defaults: ['E'] },
  { id: 'view.browsers', label: 'Show Browsers', group: 'View', defaults: ['F'] },
  { id: 'view.lists', label: 'Show List Editors', group: 'View', defaults: ['D'] },
  { id: 'view.notes', label: 'Show Note Pad', group: 'View', defaults: ['Meta+Alt+P'] },
  { id: 'view.keyCommands', label: 'Key Commands…', group: 'View', defaults: ['Alt+K'] },
  { id: 'view.theme', label: 'Toggle Light/Dark Appearance', group: 'View', defaults: ['Meta+Ctrl+T'] },
  ...screensets,
  { id: 'project.save', label: 'Save', group: 'Project', defaults: ['Meta+S'] },
  { id: 'project.saveAlternative', label: 'Save as Alternative…', group: 'Project', defaults: [] }
];

/** Modifier order in a binding. */
const Modifiers = ['Meta', 'Ctrl', 'Alt', 'Shift'] as const;

/** The keys of a keyboard event this module reads. */
export interface KeyStroke {
  key: string;
  code: string;
  metaKey: boolean;
  ctrlKey: boolean;
  altKey: boolean;
  shiftKey: boolean;
}

/**
 * A key event as a binding: `Meta+Shift+Z`. Letters and digits come from the physical key (`event.code`), so ⌥ and ⇧
 * do not change them; off a Mac, Ctrl is the command key and reads as `Meta`. Null for a lone modifier.
 */
export function bindingOf(event: KeyStroke, mac = isMac()): string | null {
  const key = keyName(event);
  if (!key) {
    return null;
  }
  const parts: string[] = [];
  if (event.metaKey || (event.ctrlKey && !mac)) {
    parts.push('Meta');
  }
  if (event.ctrlKey && mac) {
    parts.push('Ctrl');
  }
  if (event.altKey) {
    parts.push('Alt');
  }
  if (event.shiftKey) {
    parts.push('Shift');
  }
  parts.push(key);
  return parts.join('+');
}

function keyName(event: KeyStroke): string | null {
  if (['Meta', 'Control', 'Alt', 'Shift', 'CapsLock', 'Fn'].includes(event.key)) {
    return null;
  }
  const letter = /^Key([A-Z])$/.exec(event.code);
  if (letter?.[1]) {
    return letter[1];
  }
  const digit = /^(?:Digit|Numpad)(\d)$/.exec(event.code);
  if (digit?.[1]) {
    return digit[1];
  }
  if (event.key === ' ' || event.code === 'Space') {
    return 'Space';
  }
  if (event.code === 'Quote') {
    return "'";
  }
  if (event.key.length === 1) {
    return event.key.toUpperCase();
  }
  return event.key; // Enter, Delete, ArrowLeft, F1…
}

/** Puts a binding's modifiers in order (`Shift+Meta+z` → `Meta+Shift+Z`), so typed or stored ones compare equal. */
export function normalizeBinding(binding: string): string {
  const parts = binding.split('+').filter(Boolean);
  const key = parts.pop() ?? '';
  const mods = Modifiers.filter(m => parts.some(p => p.toLowerCase() === m.toLowerCase()));
  return [...mods, key.length === 1 ? key.toUpperCase() : key].join('+');
}

/** The bindings in force per command: the user's for the commands they rebound, the defaults for the rest. */
export function effectiveBindings(
  user: Readonly<Record<string, readonly string[]>>,
  registry: readonly KeyCommand[] = KeyCommands
): Map<string, string[]> {
  const result = new Map<string, string[]>();
  const taken = new Set(Object.values(user).flatMap(list => list.map(normalizeBinding)));
  for (const command of registry) {
    const own = user[command.id];
    // A default another command was given by the user is no longer this command's.
    result.set(
      command.id,
      own ? own.map(normalizeBinding) : command.defaults.map(normalizeBinding).filter(b => !taken.has(b))
    );
  }
  return result;
}

/** Binding → command id, for resolving key presses (the first command listed wins a shared binding). */
export function bindingIndex(bindings: ReadonlyMap<string, readonly string[]>): Map<string, string> {
  const index = new Map<string, string>();
  for (const [id, list] of bindings) {
    for (const binding of list) {
      if (!index.has(binding)) {
        index.set(binding, id);
      }
    }
  }
  return index;
}

/** The command a key press runs, or null. */
export function resolveKey(index: ReadonlyMap<string, string>, event: KeyStroke, mac = isMac()): string | null {
  const binding = bindingOf(event, mac);
  return binding ? (index.get(binding) ?? null) : null;
}

/** The commands other than `id` that use `binding` (a rebinding conflict). */
export function conflicts(bindings: ReadonlyMap<string, readonly string[]>, binding: string, id: string): string[] {
  const wanted = normalizeBinding(binding);
  return [...bindings].filter(([other, list]) => other !== id && list.includes(wanted)).map(([other]) => other);
}

/**
 * The user's bindings after giving `binding` to `id` (or clearing it with null): commands that had it lose it, and
 * only commands that differ from their defaults are stored.
 */
export function rebind(
  user: Readonly<Record<string, readonly string[]>>,
  id: string,
  binding: string | null,
  registry: readonly KeyCommand[] = KeyCommands
): Record<string, string[]> {
  const current = effectiveBindings(user, registry);
  const wanted = binding ? normalizeBinding(binding) : null;
  const next: Record<string, string[]> = {};
  for (const command of registry) {
    let list = current.get(command.id) ?? [];
    if (command.id === id) {
      list = wanted ? [wanted] : [];
    } else if (wanted && list.includes(wanted)) {
      list = list.filter(b => b !== wanted);
    }
    const defaults = command.defaults.map(normalizeBinding);
    if (list.length !== defaults.length || list.some((b, i) => b !== defaults[i])) {
      next[command.id] = list;
    }
  }
  return next;
}

/** A binding as macOS menus show it: `Meta+Shift+Z` → `⇧⌘Z`. */
export function formatBinding(binding: string): string {
  const symbols: Record<string, string> = { Ctrl: '⌃', Alt: '⌥', Shift: '⇧', Meta: '⌘' };
  const keys: Record<string, string> = {
    Space: 'Space',
    Enter: '↩',
    Delete: '⌦',
    Backspace: '⌫',
    Escape: '⎋',
    ArrowLeft: '←',
    ArrowRight: '→',
    ArrowUp: '↑',
    ArrowDown: '↓'
  };
  const parts = binding.split('+');
  const key = parts.pop() ?? '';
  const order = ['Ctrl', 'Alt', 'Shift', 'Meta'];
  const mods = [...parts].sort((a, b) => order.indexOf(a) - order.indexOf(b));
  return mods.map(p => symbols[p] ?? p).join('') + (keys[key] ?? key);
}

function isMac(): boolean {
  if (typeof navigator === 'undefined') {
    return true;
  }
  return /mac/i.test(navigator.platform) || /mac os/i.test(navigator.userAgent);
}

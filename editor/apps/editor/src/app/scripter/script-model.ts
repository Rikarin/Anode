import type { ParameterInfo } from '@anode/api-client';
import type { ScriptLine } from '../state/script-log.store';

/** The script a new Scripter slot shows: Logic's pass-through. */
export const StarterScript = `// Every MIDI event passes through HandleMIDI. Change it, then Run (⌘↩).
function HandleMIDI(event) {
  event.send();
}
`;

/**
 * The line a problem names (`pluginProblem`'s message: "SyntaxError: unexpected token (line 3)", "… at line 3:7"),
 * 1-based; null when it names none.
 */
export function problemLine(message: string): number | null {
  const match = /\bline\s+(\d+)/i.exec(message);
  const line = match?.[1] ? Number.parseInt(match[1], 10) : Number.NaN;
  return Number.isFinite(line) && line > 0 ? line : null;
}

/**
 * The line of the latest problem reported since a run: `since` is the last console line when Run was pressed (null:
 * none yet, or the console was empty), so a problem the run fixed stops marking its line. Lines trimmed off the
 * console's start, or a cleared console, count as all new.
 */
export function currentProblemLine(lines: readonly ScriptLine[], since: ScriptLine | null): number | null {
  const start = since === null ? 0 : lines.lastIndexOf(since) + 1;
  for (let i = lines.length - 1; i >= start; i--) {
    const line = lines[i];
    if (line?.kind === 'problem') {
      return problemLine(line.text);
    }
  }
  return null;
}

/**
 * How a script's parameter is shown: a menu as a choice, a checkbox as a toggle, a momentary as a button held down, a
 * text as a label, else (lin, log) a knob.
 */
export type ScriptControl = 'choice' | 'toggle' | 'momentary' | 'label' | 'knob';

/**
 * A run-time parameter's control, by its `kind` (the `PluginParameters` type the host reports). Without one (an older
 * Server) it is told by shape: named values make a menu; an unnamed 0…1 switch with one or two steps a checkbox.
 */
export function scriptControl(parameter: ParameterInfo): ScriptControl {
  switch (parameter.kind) {
    case 'menu':
      return 'choice';
    case 'checkbox':
      return 'toggle';
    case 'momentary':
      return 'momentary';
    case 'text':
      return 'label';
    case 'slider':
      return 'knob';
    default:
      break;
  }
  if ((parameter.valueNames?.length ?? 0) > 0) {
    return 'choice';
  }
  if (parameter.min === 0 && parameter.max === 1 && (parameter.steps === 1 || parameter.steps === 2)) {
    return 'toggle';
  }
  return 'knob';
}

/** ⌘↩ (Ctrl+↩ off macOS): Run. */
export function isRunShortcut(event: KeyboardEvent): boolean {
  return event.key === 'Enter' && (event.metaKey || event.ctrlKey) && !event.altKey && !event.shiftKey;
}

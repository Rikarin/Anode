/**
 * Test fixtures for Scripter: the golden path's script (docs/08-phase-7.md, step 8) and a tiny stand-in for the
 * engine's API subset, enough to run a script's callbacks in a spec and see what it sends. Not the engine: it checks
 * that the factory scripts are valid JavaScript against the API's shape, not their timing.
 */

/** The golden path's script: each note plus another an interval above it 100 ms later (a slider, an octave). */
export const OctaveEchoScript = `// Octave echo: each note, then the note an interval above it 100 ms later.
var PluginParameters = [
  { name: 'Interval', type: 'lin', minValue: 0, maxValue: 24, numberOfSteps: 24, defaultValue: 12 }
];

Trace('Octave echo ready');

function HandleMIDI(event) {
  event.send();
  if (event instanceof NoteOn || event instanceof NoteOff) {
    var echo = event instanceof NoteOn ? new NoteOn(event) : new NoteOff(event);
    echo.pitch = MIDI.normalizeData(event.pitch + GetParameter('Interval'));
    echo.sendAfterMilliseconds(100);
  }
}
`;

/** What a script sent: the event's kind and fields, and when (`ms` after now, or at `beat`). */
export interface SentEvent {
  kind: string;
  pitch?: number;
  velocity?: number;
  number?: number;
  value?: number;
  ms?: number;
  beat?: number;
}

export interface ScriptRun {
  /** Calls `HandleMIDI` with a new event of the kind ('NoteOn', 'NoteOff', 'ControlChange'…). */
  handle(kind: string, fields: Record<string, number>): void;
  /** Calls `ProcessMIDI` for a block, with the transport playing from `start` to `end` beats. */
  process(start: number, end: number): void;
  sent: SentEvent[];
  traces: string[];
  /** The `PluginParameters` the script declared. */
  parameters: { name: string; defaultValue?: number; valueStrings?: string[] }[];
}

interface ScriptCallbacks<E> {
  HandleMIDI?: (event: E) => void;
  ProcessMIDI?: () => void;
  parameters?: ScriptRun['parameters'];
}

/** Runs a script against a minimal Scripter API (parameters at their defaults, unless `values` names them). */
export function runScript(source: string, values: Record<string, number> = {}): ScriptRun {
  const sent: SentEvent[] = [];
  const traces: string[] = [];
  let parameters: ScriptRun['parameters'] = [];

  class Event {
    channel = 1;
    pitch = 60;
    velocity = 100;
    number = 0;
    value = 0;
    constructor(from?: Event) {
      if (from) {
        Object.assign(this, from);
      }
    }
    private record(extra: Partial<SentEvent>): void {
      sent.push({ kind: this.constructor.name, ...this.fields(), ...extra });
    }
    protected fields(): Partial<SentEvent> {
      return {};
    }
    send(): void {
      this.record({});
    }
    sendAfterMilliseconds(ms: number): void {
      this.record({ ms });
    }
    sendAtBeat(beat: number): void {
      this.record({ beat });
    }
    sendAfterBeats(beats: number): void {
      this.record({ beat: beats });
    }
    trace(): void {
      traces.push(`[${this.constructor.name}]`);
    }
  }
  class Note extends Event {
    protected override fields(): Partial<SentEvent> {
      return { pitch: this.pitch, velocity: this.velocity };
    }
  }
  class NoteOn extends Note {}
  class NoteOff extends Note {}
  class ControlChange extends Event {
    protected override fields(): Partial<SentEvent> {
      return { number: this.number, value: this.value };
    }
  }
  class ProgramChange extends Event {}
  class PitchBend extends Event {}
  class PolyPressure extends Note {}
  class ChannelPressure extends Event {}
  class TargetEvent extends Event {}
  const kinds: Record<string, typeof Event> = {
    Event,
    Note,
    NoteOn,
    NoteOff,
    ControlChange,
    ProgramChange,
    PitchBend,
    PolyPressure,
    ChannelPressure,
    TargetEvent
  };

  const Names = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];
  const MIDI = {
    normalizeData: (value: number) => Math.max(0, Math.min(127, Math.round(value))),
    noteName: (pitch: number) => `${Names[pitch % 12]}${Math.floor(pitch / 12) - 2}`
  };
  const GetParameter = (name: string) => {
    if (name in values) {
      return values[name] ?? 0;
    }
    const parameter = parameters.find(p => p.name === name);
    if (!parameter) {
      throw new Error(`No parameter ${name}`);
    }
    return parameter.defaultValue ?? 0;
  };
  let timing = { playing: false, blockStartBeat: 0, blockEndBeat: 0, tempo: 120 };
  const GetTimingInfo = () => timing;
  const Trace = (value: unknown) => {
    traces.push(String(value));
  };

  const body = `${source}
;return {
  HandleMIDI: typeof HandleMIDI === 'function' ? HandleMIDI : undefined,
  ProcessMIDI: typeof ProcessMIDI === 'function' ? ProcessMIDI : undefined,
  parameters: typeof PluginParameters !== 'undefined' ? PluginParameters : []
};`;
  const names = [
    'Event',
    'Note',
    'NoteOn',
    'NoteOff',
    'ControlChange',
    'ProgramChange',
    'PitchBend',
    'PolyPressure',
    'ChannelPressure',
    'TargetEvent',
    'MIDI'
  ];
  // The script runs as a function of the API's globals (test code only: the engine runs scripts in QuickJS).
  // eslint-disable-next-line @typescript-eslint/no-implied-eval
  const factory = new Function(...names, 'GetParameter', 'GetTimingInfo', 'Trace', body) as (
    ...args: unknown[]
  ) => ScriptCallbacks<Event>;
  // `PluginParameters` is read after the script's top level ran; GetParameter at top level sees none yet.
  const callbacks = factory(...names.slice(0, -1).map(n => kinds[n]), MIDI, GetParameter, GetTimingInfo, Trace);
  parameters = callbacks.parameters ?? [];

  return {
    handle(kind, fields) {
      const Kind = kinds[kind] ?? Event;
      const event = Object.assign(new Kind(), fields);
      if (callbacks.HandleMIDI) {
        callbacks.HandleMIDI(event);
      } else {
        event.send();
      }
    },
    process(start, end) {
      timing = { playing: true, blockStartBeat: start, blockEndBeat: end, tempo: 120 };
      callbacks.ProcessMIDI?.();
    },
    sent,
    traces,
    parameters
  };
}

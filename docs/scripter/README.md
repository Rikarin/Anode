# Scripter

Scripter is Anode's JavaScript MIDI effect (`anode.scripter`), a compatible subset of Logic Pro's Scripter. It sits in
a track's MIDI FX slots like the other MIDI effects, and runs a script on the MIDI on its way to the instrument.

How it runs is [ADR-0009](../adr/0009-scripter.md):

- Each instance has its own QuickJS-ng runtime (`engine/native/an_script`, pinned in its CMakeLists) in a fixed 4 MiB
  arena. Running out of the arena is an error, not growth.
- The script compiles on the control thread. The audio thread only calls it.
- A block's calls into the script may take 25 % of the block's period. A call that runs longer is interrupted.
- A script that is interrupted, throws or runs out of memory is bypassed: its input passes through, the notes it
  started end, and the slot reports the problem with its line. Editing the script (Run) re-enables it.
- There are no modules and no I/O. `Trace` writes to the Script Editor's console.

The code is `Anode.Dsp/MidiEffects/Scripter`:

- `ScripterEffect` is the effect.
- `ScriptPrelude` is the API, in JavaScript.
- `ScripterPresets` are the factory scripts.

## The API

### Callbacks

A script defines any of these as global functions:

| Function | Called |
|---|---|
| `HandleMIDI(event)` | For each incoming event, in order. Without it, events pass through. With it, an event goes on only if the script sends it. |
| `ProcessMIDI()` | Once per block, after the block's events. |
| `ParameterChanged(index, value)` | Once for each parameter after the script runs, then whenever a value changes, from the host or from `SetParameter`. Not for `text` parameters. |
| `Reset()` | When the transport stops or jumps. |
| `Idle()` | About four times a second, on the audio thread, under the block's budget. |

| Variable | Effect |
|---|---|
| `var PluginParameters = [...]` | The script's parameters (below). |
| `var NeedsTimingInfo = true` | Accepted. Timing is always available (see the differences below). |
| `var ResetParameterDefaults = true` | Run starts every parameter at its default. Otherwise values carry over by name. |

### Timing

`GetTimingInfo()` returns the block's timing, from the engine's `TransportContext`:

| Field | Meaning |
|---|---|
| `playing` | Whether the transport plays. |
| `blockStartBeat`, `blockEndBeat`, `blockLength` | The block's start and end in beats, and its length. Beats are quarter notes, and **beat 1 is the project start**, as in Logic. |
| `tempo` | The playback tempo in bpm. |
| `meterNumerator`, `meterDenominator` | The time signature. |
| `cycling`, `leftCycleBeat`, `rightCycleBeat` | Always `false`, 1 and 1 (see the differences below). |

Every event has a `beatPos`: the beat of its sample.

### Parameters

`PluginParameters` entries have a `name` and a `type`:

| `type` | Fields | Value |
|---|---|---|
| `lin` (the default) | `minValue`, `maxValue`, `numberOfSteps` (0: continuous), `defaultValue`, `unit` | `minValue` … `maxValue`, snapped to the steps |
| `log` | as `lin` | as `lin`. The editor shows it on a log scale. |
| `menu` | `valueStrings`, `defaultValue` | the index of a string |
| `checkbox` | `defaultValue` | 0 or 1 |
| `momentary` | none | 1 while pressed, else 0 |
| `text` | none | none: a label |

The fields are as follows:

- `disableAutomation: true` keeps a parameter out of automation.
- A name is the parameter's id: values are stored and carried over by name. A repeated name gains " 2", " 3" and so on.
- A script has at most 128 parameters.

The functions are as follows:

- `GetParameter(nameOrIndex)` returns a value.
- `SetParameter(nameOrIndex, value)` sets a value. The host sees the change, and `ParameterChanged` runs if the value
  changed.
- `UpdatePluginParameters()` re-reads `PluginParameters` after the script changes it, keeping values by name. The host
  updates the slot's parameters.

The parameters become the slot's parameters: automatable and mappable to Smart Controls.

### Events

The event classes are `Event`, `Note` (the base of `NoteOn` and `NoteOff`), `NoteOn`, `NoteOff`, `PolyPressure`,
`ControlChange`, `ProgramChange`, `ChannelPressure`, `PitchBend` and `TargetEvent`.

| Class | Fields (defaults) | Notes |
|---|---|---|
| all | `channel` (1, 1 … 16), `beatPos`, `port` (1), `articulationID` (0), `isRealtime` (false) | |
| `NoteOn` | `pitch` (60), `velocity` (100) | A note-on at velocity 0 arrives as a `NoteOff` with velocity 0. |
| `NoteOff` | `pitch` (60), `velocity` (64) | |
| `PolyPressure` | `pitch` (60), `value` (0) | |
| `ControlChange` | `number` (1), `value` (0) | |
| `ProgramChange` | `number` (0) | |
| `ChannelPressure` | `value` (0) | |
| `PitchBend` | `value` (0, −8192 … 8191) | |
| `TargetEvent` | `target` (a name), `value` (0 … 1) | Listed for the host with its target's name (below). |
| `Event` | `status`, `data1`, `data2` | Any other message. The channel comes from `channel`. |

A constructor takes an event to copy: `new NoteOff(noteOn)` keeps the channel, pitch, velocity, beat and timing of
the note-on. Values are rounded and clamped to MIDI's ranges when sent.

Each event has these methods:

| Method | Sends the event |
|---|---|
| `send()` | Now: at the sample of the event being handled. An event created in `ProcessMIDI` goes at the block's start. |
| `sendAfterMilliseconds(ms)` | `ms` after that sample. |
| `sendAtBeat(beat)` | At `beat` (1 is the project start). |
| `sendAfterBeats(beats)` | `beats` after the event's `beatPos`. |
| `trace()` | Writes its description, e.g. `[NoteOn channel:1 pitch:60 [C3] velocity:100]`, to the console. |

Scheduled events wait in the instance across blocks, and play on the first sample at or after their time:

- Milliseconds count samples, at any tempo.
- Beats follow the tempo map through ramps.
- A time already past plays at once.
- When the transport stops, starts or jumps, an event waiting for a beat keeps its remaining distance as time.
  It neither hangs nor fires early.
- While the transport is stopped, beats run at the tempo from where the playhead stands.
- At most 2048 events wait. Further ones are dropped, and the instance counts them.

### Helpers

`MIDI` has these helpers:

| Helper | Returns |
|---|---|
| `noteName(n)` | The note's name, e.g. 60 → `C3` (middle C is C3, as in Logic). |
| `noteNumber(name)` | The note's number, e.g. `C#3` or `Db3` → 61. |
| `ccName(n)` | The controller's name. |
| `allNotesOff()` | Sends note-offs for every note the script started. |
| `normalizeStatus(v)`, `normalizeChannel(v)`, `normalizeData(v)` | `v` clamped to its range. |
| `_noteNames`, `_ccNames` | The name tables. |

`Trace(value)` writes a line, at most 255 bytes, to the console. The engine sends at most 50 lines a second per slot
and counts the rest as dropped.

`Math.random` is seeded per instance when it loads, so a run with a given seed repeats exactly.

## Differences from Logic

- **Timing info** is always available: `NeedsTimingInfo` is accepted and not needed.
- **Cycle:** `cycling`, `leftCycleBeat` and `rightCycleBeat` are not filled in. The engine splits blocks at a cycle
  jump, and a jump keeps waiting events at their distance in time.
- **`TargetEvent`** does not reach plugin parameters by itself. The instance lists the targets the script names, and
  the events it sends for each block, for the engine to route. There is no `target` parameter type.
- **`Idle`** runs on the audio thread, under the block's budget, about every 250 ms.
- **`ProcessMIDI`** runs after the block's `HandleMIDI` calls, so an arpeggiator can answer a note in the block it
  arrives in.
- **`GetTimingInfo()`** returns the same object every call. Do not keep it across blocks.
- **New events** start at the sample of the event being handled.
- **Not supported:** the `Fader` and `Environment` objects, Scripter on audio, `SysEx`, MIDI ports beyond the
  `port` field, and `target`-type parameters.
- **The engine** has the base JavaScript objects, `Date`, `RegExp`, `JSON`, `Map`/`Set` and typed arrays. It has no
  `Promise`, `Proxy`, `WeakRef`, timers, modules or I/O.
- **Limits:**
  - Recursion is limited, and a stack overflow is an error.
  - A block's calls share the budget, by default 25 % of the block period.
  - A script that runs out of its arena stops. Reference counting frees most garbage at once, and the engine runs the
    collector between blocks.
- **Errors** are reported with the line in the script. When an error is raised inside the API, the line is where the
  script called it. For an interrupted loop, QuickJS reports the last line it recorded, which can be the statement
  before a tight loop.

## Factory scripts

`ScripterPresets.All` has the factory scripts, written for Anode:

| Id | Name | What it does |
|---|---|---|
| `factory.transposer` | Transposer | Moves notes by semitones, ending each where it started. |
| `factory.delay-notes` | Delay Notes | Delays everything by milliseconds. |
| `factory.chord` | Chord | Plays a chord shape on each note. |
| `factory.random-velocity` | Random Velocity | Varies velocities at random. |
| `factory.note-repeater` | Note Repeater | Echoes notes on a beat grid, fading. |
| `factory.guitar-strummer` | Guitar Strummer | Spreads a chord's notes like a strum. |
| `factory.drum-humaniser` | Drum Humaniser | Small random delays and velocity changes. |
| `factory.cc-smoother` | CC Smoother | Ramps a controller between values. |
| `factory.arpeggio` | Arpeggio | Plays held notes on the song's grid. |
| `factory.trace-demo` | Trace Demo | Traces events and timing. |

## For the engine

The engine drives `ScripterEffect` through `IScriptHost` (`Anode.Dsp/MidiEffects/IScriptHost.cs`):

- **New source:** create a new instance with `BuiltinMidiEffects.Create("anode.scripter")`, then `Prepare` it, then
  call `Compile(source)` on the control thread. A null result means it compiled. The engine then:
  - swaps the new instance in;
  - sets the values by id through `SetParameter`;
  - ends the old instance's notes.

  `ScripterEffect.CreateCompiled(source, rate, frames, previous)` does the same in one call, carrying values by name.
- **Parameters:**
  - `RuntimeParameters` is the slot's list after `Compile`. Each parameter's id is its name.
  - `TakeParametersChanged()` returns true once after the script's `UpdatePluginParameters`, or after a new
    `TargetEvent` target. The audio thread hands the change over without allocating.
  - `ParameterValuesVersion` changes when the script sets a value itself.
- **Faults:** the engine reads `Faulted` on the audio thread after each `Process`. `TakeError()` returns the run-time
  error once, with its line. A compile error is `Compile`'s result instead.
- **Trace:** `DrainTrace(lines)` appends the waiting lines and returns how many the ring dropped.
- **`Idle()`:**
  - The engine calls it about every 50 ms. It runs the garbage collector when the arena grew by 256 KiB, or about
    once a second.
  - It never makes the audio thread wait. When a block is processing, it tries again on a later call. A block that
    starts during a collection takes its events one block late.
  - The script's own `Idle` runs on the audio thread, about every 250 ms.
- **State:** `SaveState` and `LoadState` hold the source and the values by name. Loading compiles the script.

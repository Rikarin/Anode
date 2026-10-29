/**
 * Scripter's factory scripts: workstream D's `ScripterPresets.All` (engine/src/Anode.Dsp/MidiEffects/Scripter/
 * ScripterPresets.cs), copied with their ids, names and sources, in menu order. There is no endpoint for them, so the
 * Script Editor's lazy chunk carries this copy; keep it in step with the engine's (a spec checks it when the file is
 * there).
 */
export interface FactoryScript {
  /** The engine's stable id (`factory.…`); `script-preset-{id}` is its menu item's test id. */
  id: string;
  name: string;
  description: string;
  source: string;
}

export const FactoryScripts: readonly FactoryScript[] = [
  {
    id: 'factory.transposer',
    name: 'Transposer',
    description: 'Moves notes by semitones; a note ends where it started, whatever the slider does meanwhile.',
    source: `// Transposer: moves every note by the Semitones slider.
var PluginParameters = [
  { name: "Semitones", type: "lin", minValue: -24, maxValue: 24, numberOfSteps: 48, defaultValue: 0 }
];

var playing = {}; // incoming pitch (per channel) -> the pitch it was sent as

function HandleMIDI(event) {
  var key = event.channel * 128 + event.pitch;
  if (event instanceof NoteOn) {
    event.pitch = MIDI.normalizeData(event.pitch + GetParameter("Semitones"));
    playing[key] = event.pitch;
  } else if (event instanceof NoteOff && playing[key] !== undefined) {
    event.pitch = playing[key];
    delete playing[key];
  } else if (event instanceof PolyPressure) {
    event.pitch = MIDI.normalizeData(event.pitch + GetParameter("Semitones"));
  }
  event.send();
}
`
  },
  {
    id: 'factory.delay-notes',
    name: 'Delay Notes',
    description: 'Delays every event by a time in milliseconds.',
    source: `// Delay Notes: everything arrives Delay milliseconds late.
var PluginParameters = [
  { name: "Delay", type: "lin", unit: "ms", minValue: 0, maxValue: 1000, numberOfSteps: 1000, defaultValue: 100 }
];

function HandleMIDI(event) {
  event.sendAfterMilliseconds(GetParameter("Delay"));
}
`
  },
  {
    id: 'factory.chord',
    name: 'Chord',
    description: 'Turns each note into a chord of the chosen shape.',
    source: `// Chord: every note plays the chord built on it.
var shapes = {
  "Major": [0, 4, 7], "Minor": [0, 3, 7], "Sus 4": [0, 5, 7], "Major 7": [0, 4, 7, 11],
  "Minor 7": [0, 3, 7, 10], "Dominant 7": [0, 4, 7, 10], "Power": [0, 7, 12], "Octaves": [0, 12]
};
var names = Object.keys(shapes);
var PluginParameters = [
  { name: "Shape", type: "menu", valueStrings: names, defaultValue: 0 },
  { name: "Spread Velocity", type: "checkbox", defaultValue: 0 }
];

var sounding = {}; // channel and root -> the pitches sent for it

function HandleMIDI(event) {
  if (!(event instanceof Note)) { event.send(); return; }
  var key = event.channel * 128 + event.pitch;
  if (event instanceof NoteOn) {
    var shape = shapes[names[GetParameter("Shape")]];
    var sent = [];
    for (var i = 0; i < shape.length; i++) {
      var note = new NoteOn(event);
      note.pitch = event.pitch + shape[i];
      if (note.pitch > 127) continue;
      if (GetParameter("Spread Velocity")) note.velocity = Math.max(1, event.velocity - 8 * i);
      note.send();
      sent.push(note.pitch);
    }
    sounding[key] = sent;
  } else {
    var pitches = sounding[key] || [event.pitch];
    delete sounding[key];
    for (var j = 0; j < pitches.length; j++) {
      var off = new NoteOff(event);
      off.pitch = pitches[j];
      off.send();
    }
  }
}
`
  },
  {
    id: 'factory.random-velocity',
    name: 'Random Velocity',
    description: "Varies each note's velocity at random around the played one.",
    source: `// Random Velocity: each note-on's velocity moves up to Range either way.
var PluginParameters = [
  { name: "Range", type: "lin", minValue: 0, maxValue: 64, numberOfSteps: 64, defaultValue: 16 }
];

function HandleMIDI(event) {
  if (event instanceof NoteOn) {
    var range = GetParameter("Range");
    var change = Math.round((Math.random() * 2 - 1) * range);
    event.velocity = Math.min(127, Math.max(1, event.velocity + change));
  }
  event.send();
}
`
  },
  {
    id: 'factory.note-repeater',
    name: 'Note Repeater',
    description: 'Repeats each note in time with the song, each repeat softer than the last.',
    source: `// Note Repeater: echoes of each note on a beat grid, fading by Decay.
var NeedsTimingInfo = true;
var divisions = ["1/4", "1/8", "1/8 T", "1/16"];
var beats = [1, 0.5, 1 / 3, 0.25];
var PluginParameters = [
  { name: "Repeats", type: "lin", minValue: 0, maxValue: 8, numberOfSteps: 8, defaultValue: 3 },
  { name: "Time", type: "menu", valueStrings: divisions, defaultValue: 1 },
  { name: "Decay", type: "lin", unit: "%", minValue: 0, maxValue: 100, numberOfSteps: 100, defaultValue: 30 }
];

function HandleMIDI(event) {
  event.send();
  if (!(event instanceof NoteOn)) return;
  var step = beats[GetParameter("Time")];
  var velocity = event.velocity;
  for (var i = 1; i <= GetParameter("Repeats"); i++) {
    velocity = Math.round(velocity * (1 - GetParameter("Decay") / 100));
    if (velocity < 1) break;
    var on = new NoteOn(event);
    on.velocity = velocity;
    on.sendAfterBeats(i * step);
    var off = new NoteOff(event);
    off.sendAfterBeats(i * step + step * 0.9);
  }
}
`
  },
  {
    id: 'factory.guitar-strummer',
    name: 'Guitar Strummer',
    description: 'Spreads the notes of a chord played together, like a strum, down or up.',
    source: `// Guitar Strummer: the notes of a chord that start in the same block are spread Strum ms apart.
var PluginParameters = [
  { name: "Strum", type: "lin", unit: "ms", minValue: 0, maxValue: 100, numberOfSteps: 100, defaultValue: 25 },
  { name: "Direction", type: "menu", valueStrings: ["Down (low first)", "Up (high first)", "Alternate"],
    defaultValue: 0 }
];

var chord = [];
var delays = {};      // channel and pitch -> the delay its note-on got
var upNext = false;

function HandleMIDI(event) {
  if (event instanceof NoteOn) { chord.push(event); return; }
  if (event instanceof NoteOff) {
    var key = event.channel * 128 + event.pitch;
    event.sendAfterMilliseconds(delays[key] || 0);
    delete delays[key];
    return;
  }
  event.send();
}

function ProcessMIDI() {
  if (chord.length === 0) return;
  var direction = GetParameter("Direction");
  var up = direction === 1 || (direction === 2 && upNext);
  upNext = !upNext;
  chord.sort(function (a, b) { return up ? b.pitch - a.pitch : a.pitch - b.pitch; });
  for (var i = 0; i < chord.length; i++) {
    var delay = i * GetParameter("Strum");
    delays[chord[i].channel * 128 + chord[i].pitch] = delay;
    chord[i].sendAfterMilliseconds(delay);
  }
  chord = [];
}

function Reset() { chord = []; delays = {}; }
`
  },
  {
    id: 'factory.drum-humaniser',
    name: 'Drum Humaniser',
    description: 'Loosens programmed drums: small random delays and velocity changes per hit.',
    source: `// Drum Humaniser: each hit comes up to Timing ms late and up to Velocity softer or louder.
var PluginParameters = [
  { name: "Timing", type: "lin", unit: "ms", minValue: 0, maxValue: 30, numberOfSteps: 300, defaultValue: 8 },
  { name: "Velocity", type: "lin", minValue: 0, maxValue: 40, numberOfSteps: 40, defaultValue: 10 }
];

var late = {}; // channel and pitch -> the delay its note-on got, so the note-off keeps the length

function HandleMIDI(event) {
  var key = event.channel * 128 + event.pitch;
  if (event instanceof NoteOn) {
    var delay = Math.random() * GetParameter("Timing");
    var change = Math.round((Math.random() * 2 - 1) * GetParameter("Velocity"));
    event.velocity = Math.min(127, Math.max(1, event.velocity + change));
    late[key] = delay;
    event.sendAfterMilliseconds(delay);
  } else if (event instanceof NoteOff) {
    event.sendAfterMilliseconds(late[key] || 0);
    delete late[key];
  } else {
    event.send();
  }
}
`
  },
  {
    id: 'factory.cc-smoother',
    name: 'CC Smoother',
    description: 'Glides one controller from value to value instead of jumping.',
    source: `// CC Smoother: a jump of the chosen controller becomes a ramp lasting Glide ms.
var PluginParameters = [
  { name: "Controller", type: "lin", minValue: 0, maxValue: 127, numberOfSteps: 127, defaultValue: 1 },
  { name: "Glide", type: "lin", unit: "ms", minValue: 1, maxValue: 500, numberOfSteps: 499, defaultValue: 60 }
];

var last = {}; // channel -> the controller's last value
var STEP_MS = 5;

function HandleMIDI(event) {
  if (!(event instanceof ControlChange) || event.number !== GetParameter("Controller")) {
    event.send();
    return;
  }
  var from = last[event.channel];
  last[event.channel] = event.value;
  if (from === undefined || from === event.value) { event.send(); return; }
  var glide = GetParameter("Glide");
  var steps = Math.max(1, Math.round(glide / STEP_MS));
  for (var i = 1; i <= steps; i++) {
    var cc = new ControlChange(event);
    cc.value = Math.round(from + (event.value - from) * i / steps);
    cc.sendAfterMilliseconds((i - 1) * glide / steps);
  }
}

function Reset() { last = {}; }
`
  },
  {
    id: 'factory.arpeggio',
    name: 'Arpeggio',
    description: "Plays the held notes one at a time on the song's grid, up, down or up and down.",
    source: `// Arpeggio: the held notes in turn, on the beat grid while the song plays.
var NeedsTimingInfo = true;
var rates = [1, 0.5, 0.25, 0.125];
var PluginParameters = [
  { name: "Rate", type: "menu", valueStrings: ["1/4", "1/8", "1/16", "1/32"], defaultValue: 2 },
  { name: "Direction", type: "menu", valueStrings: ["Up", "Down", "Up and Down"], defaultValue: 0 },
  { name: "Gate", type: "lin", unit: "%", minValue: 10, maxValue: 100, numberOfSteps: 90, defaultValue: 80 }
];

var held = [];
var step = 0;

function HandleMIDI(event) {
  if (event instanceof NoteOn) {
    held.push(event);
    held.sort(function (a, b) { return a.pitch - b.pitch; });
  } else if (event instanceof NoteOff) {
    held = held.filter(function (n) { return n.pitch !== event.pitch || n.channel !== event.channel; });
    if (held.length === 0) step = 0;
  } else {
    event.send();
  }
}

function order() {
  var direction = GetParameter("Direction");
  if (direction === 1) return held.slice().reverse();
  if (direction === 2 && held.length > 2) return held.concat(held.slice(1, -1).reverse());
  return held;
}

function ProcessMIDI() {
  var info = GetTimingInfo();
  if (!info.playing || held.length === 0) return;
  var rate = rates[GetParameter("Rate")];
  // Grid points (beat 1 is the song's start) inside this block.
  var beat = 1 + Math.ceil((info.blockStartBeat - 1) / rate - 1e-9) * rate;
  for (; beat < info.blockEndBeat; beat += rate) {
    var notes = order();
    var source = notes[step % notes.length];
    step++;
    var on = new NoteOn(source);
    on.sendAtBeat(beat);
    var off = new NoteOff(source);
    off.sendAtBeat(beat + rate * GetParameter("Gate") / 100);
  }
}

function Reset() { held = []; step = 0; }
`
  },
  {
    id: 'factory.trace-demo',
    name: 'Trace Demo',
    description: 'Shows what arrives in the console: each event, and the timing once a second.',
    source: `// Trace Demo: prints each event and, while the song plays, the timing info about once a second.
var NeedsTimingInfo = true;
var PluginParameters = [
  { name: "Show Events", type: "checkbox", defaultValue: 1 },
  { name: "Show Timing", type: "checkbox", defaultValue: 1 }
];

Trace("Trace Demo ready");
var lastSecond = -1;

function HandleMIDI(event) {
  if (GetParameter("Show Events")) event.trace();
  event.send();
}

function ProcessMIDI() {
  var info = GetTimingInfo();
  if (!info.playing || !GetParameter("Show Timing")) return;
  var second = Math.floor((info.blockStartBeat - 1) * 60 / info.tempo);
  if (second === lastSecond) return;
  lastSecond = second;
  Trace("beat " + info.blockStartBeat.toFixed(3) + " at " + info.tempo.toFixed(1) + " bpm, " +
    info.meterNumerator + "/" + info.meterDenominator);
}

function ParameterChanged(index, value) {
  Trace(PluginParameters[index].name + " = " + value);
}
`
  }
];

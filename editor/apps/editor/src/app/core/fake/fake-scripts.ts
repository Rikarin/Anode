import type { ScriptPreset } from '@anode/api-client';

/**
 * The fake Server's Scripter factory scripts (`GET /library/scripts`): four of the engine's `ScripterPresets.All`
 * (engine/src/Anode.Dsp/MidiEffects/Scripter/ScripterPresets.cs), with their ids, names and sources, for tests and the
 * browser-only editor. The real Server serves them all; the editor has no list of its own.
 */
export const FakeScriptPresets: readonly ScriptPreset[] = [
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

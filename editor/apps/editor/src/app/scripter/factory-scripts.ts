/**
 * Scripter's factory presets, in the spirit of Logic's tutorial scripts, written for Anode against the API subset in
 * docs/08-phase-7.md ("Scripter"). They live in the Script Editor's lazy chunk: there is no endpoint for them (the
 * engine's own copies, workstream D's, should match these).
 */
export interface FactoryScript {
  /** A stable id (`script-preset-{id}` is its menu item's test id). */
  id: string;
  name: string;
  source: string;
}

export const FactoryScripts: readonly FactoryScript[] = [
  {
    id: 'transposer',
    name: 'Transposer',
    source: `// Transposer: moves every note by a number of semitones.
var PluginParameters = [
  { name: 'Semitones', type: 'lin', minValue: -24, maxValue: 24, numberOfSteps: 48, defaultValue: 0 }
];

// The pitch each held key was sent as, so its note-off follows it after the slider moves.
var sounding = {};

function HandleMIDI(event) {
  if (event instanceof NoteOn) {
    var pitch = MIDI.normalizeData(event.pitch + GetParameter('Semitones'));
    sounding[event.channel * 128 + event.pitch] = pitch;
    event.pitch = pitch;
  } else if (event instanceof NoteOff) {
    var key = event.channel * 128 + event.pitch;
    if (key in sounding) {
      event.pitch = sounding[key];
      delete sounding[key];
    }
  }
  event.send();
}
`
  },
  {
    id: 'delay-notes',
    name: 'Delay Notes',
    source: `// Delay Notes: plays every note (and its release) later by a time in milliseconds.
var PluginParameters = [
  { name: 'Delay', type: 'lin', unit: 'ms', minValue: 0, maxValue: 1000, numberOfSteps: 100, defaultValue: 250 }
];

function HandleMIDI(event) {
  if (event instanceof NoteOn || event instanceof NoteOff) {
    event.sendAfterMilliseconds(GetParameter('Delay'));
  } else {
    event.send();
  }
}
`
  },
  {
    id: 'chord',
    name: 'Simple Chord',
    source: `// Simple Chord: each note becomes a triad, major or minor, built up from it.
var PluginParameters = [
  { name: 'Chord', type: 'menu', valueStrings: ['Major', 'Minor', 'Sus4', 'Power'], defaultValue: 0 }
];

var Intervals = [[0, 4, 7], [0, 3, 7], [0, 5, 7], [0, 7, 12]];
// The intervals each held key started with, so a menu change cannot leave notes hanging.
var started = {};

function HandleMIDI(event) {
  if (event instanceof NoteOn) {
    var intervals = Intervals[GetParameter('Chord')];
    started[event.pitch] = intervals;
    play(event, intervals, NoteOn);
  } else if (event instanceof NoteOff) {
    play(event, started[event.pitch] || [0], NoteOff);
    delete started[event.pitch];
  } else {
    event.send();
  }
}

function play(event, intervals, Kind) {
  for (var i = 0; i < intervals.length; i++) {
    var note = new Kind(event);
    note.pitch = MIDI.normalizeData(event.pitch + intervals[i]);
    note.send();
  }
}
`
  },
  {
    id: 'random-velocity',
    name: 'Random Velocity',
    source: `// Random Velocity: varies each note's velocity by up to a range around it.
var PluginParameters = [
  { name: 'Range', type: 'lin', minValue: 0, maxValue: 64, numberOfSteps: 64, defaultValue: 20 }
];

function HandleMIDI(event) {
  if (event instanceof NoteOn) {
    var range = GetParameter('Range');
    var offset = Math.round((Math.random() * 2 - 1) * range);
    event.velocity = Math.max(1, Math.min(127, event.velocity + offset));
  }
  event.send();
}
`
  },
  {
    id: 'note-repeater',
    name: 'Note Repeater',
    source: `// Note Repeater: echoes each note a number of times, quieter each time.
var PluginParameters = [
  { name: 'Repeats', type: 'lin', minValue: 1, maxValue: 8, numberOfSteps: 7, defaultValue: 3 },
  { name: 'Time', type: 'lin', unit: 'ms', minValue: 20, maxValue: 1000, numberOfSteps: 98, defaultValue: 200 },
  { name: 'Decay', type: 'lin', unit: '%', minValue: 0, maxValue: 100, numberOfSteps: 100, defaultValue: 30 }
];

function HandleMIDI(event) {
  event.send();
  if (!(event instanceof NoteOn)) {
    return;
  }
  var time = GetParameter('Time');
  var velocity = event.velocity;
  for (var i = 1; i <= GetParameter('Repeats'); i++) {
    velocity = Math.round(velocity * (1 - GetParameter('Decay') / 100));
    if (velocity < 1) {
      break;
    }
    var on = new NoteOn(event);
    on.velocity = velocity;
    on.sendAfterMilliseconds(time * i);
    var off = new NoteOff(event);
    off.sendAfterMilliseconds(time * i + time / 2);
  }
}
`
  },
  {
    id: 'guitar-strummer',
    name: 'Guitar Strummer',
    source: `// Guitar Strummer: spreads the notes of a chord out in time, in the order they are played.
var PluginParameters = [
  { name: 'Strum', type: 'lin', unit: 'ms', minValue: 0, maxValue: 100, numberOfSteps: 100, defaultValue: 25 }
];

// Keys held now, and the delay each was strummed with (its note-off waits as long).
var held = 0;
var delays = {};

function HandleMIDI(event) {
  if (event instanceof NoteOn) {
    var delay = held * GetParameter('Strum');
    delays[event.pitch] = delay;
    held++;
    event.sendAfterMilliseconds(delay);
  } else if (event instanceof NoteOff) {
    held = Math.max(0, held - 1);
    event.sendAfterMilliseconds(delays[event.pitch] || 0);
    delete delays[event.pitch];
  } else {
    event.send();
  }
}

function Reset() {
  held = 0;
  delays = {};
}
`
  },
  {
    id: 'drum-humanizer',
    name: 'Drum Humanizer',
    source: `// Drum Humanizer: nudges each hit late by a little and varies its velocity, as a drummer would.
var PluginParameters = [
  { name: 'Timing', type: 'lin', unit: 'ms', minValue: 0, maxValue: 30, numberOfSteps: 30, defaultValue: 8 },
  { name: 'Velocity', type: 'lin', minValue: 0, maxValue: 40, numberOfSteps: 40, defaultValue: 12 }
];

var late = {};

function HandleMIDI(event) {
  if (event instanceof NoteOn) {
    var delay = Math.random() * GetParameter('Timing');
    var spread = GetParameter('Velocity');
    event.velocity = Math.max(1, Math.min(127, Math.round(event.velocity + (Math.random() * 2 - 1) * spread)));
    late[event.pitch] = delay;
    event.sendAfterMilliseconds(delay);
  } else if (event instanceof NoteOff) {
    event.sendAfterMilliseconds(late[event.pitch] || 0);
    delete late[event.pitch];
  } else {
    event.send();
  }
}
`
  },
  {
    id: 'cc-smoother',
    name: 'CC Smoother',
    source: `// CC Smoother: eases a controller towards each new value instead of jumping to it.
var PluginParameters = [
  { name: 'Controller', type: 'lin', minValue: 0, maxValue: 127, numberOfSteps: 127, defaultValue: 1 },
  { name: 'Smoothing', type: 'lin', unit: '%', minValue: 0, maxValue: 95, numberOfSteps: 95, defaultValue: 60 }
];

var last = -1;

function HandleMIDI(event) {
  if (event instanceof ControlChange && event.number === GetParameter('Controller')) {
    if (last < 0) {
      last = event.value;
    }
    var amount = GetParameter('Smoothing') / 100;
    last = last + (event.value - last) * (1 - amount);
    event.value = Math.round(last);
  }
  event.send();
}

function Reset() {
  last = -1;
}
`
  },
  {
    id: 'arpeggiator',
    name: 'Simple Arpeggio',
    source: `// Simple Arpeggio: plays the held keys one after another, in time with the project.
var NeedsTimingInfo = true;
var PluginParameters = [
  { name: 'Rate', type: 'menu', valueStrings: ['1/4', '1/8', '1/16'], defaultValue: 1 },
  { name: 'Gate', type: 'lin', unit: '%', minValue: 10, maxValue: 100, numberOfSteps: 90, defaultValue: 80 },
  { name: 'Latch', type: 'checkbox', defaultValue: 0 }
];

var Divisions = [1, 0.5, 0.25];
var held = [];
var step = 0;
var nextBeat = -1;

function HandleMIDI(event) {
  if (event instanceof NoteOn) {
    if (GetParameter('Latch') === 1 && held.length > 0 && step > 0) {
      held = [];
    }
    held.push(event.pitch);
    held.sort(function (a, b) {
      return a - b;
    });
  } else if (event instanceof NoteOff) {
    if (GetParameter('Latch') === 0) {
      var i = held.indexOf(event.pitch);
      if (i >= 0) {
        held.splice(i, 1);
      }
    }
  } else {
    event.send();
  }
}

function ProcessMIDI() {
  var info = GetTimingInfo();
  if (!info.playing || held.length === 0) {
    nextBeat = -1;
    return;
  }
  var division = Divisions[GetParameter('Rate')];
  if (nextBeat < 0) {
    nextBeat = Math.ceil(info.blockStartBeat / division) * division;
  }
  while (nextBeat < info.blockEndBeat) {
    var on = new NoteOn();
    on.pitch = held[step % held.length];
    on.velocity = 100;
    on.sendAtBeat(nextBeat);
    var off = new NoteOff(on);
    off.sendAtBeat(nextBeat + (division * GetParameter('Gate')) / 100);
    step++;
    nextBeat += division;
  }
}

function Reset() {
  held = [];
  step = 0;
  nextBeat = -1;
}
`
  },
  {
    id: 'trace-demo',
    name: 'Trace Demo',
    source: `// Trace Demo: writes every event, and every parameter change, to the console.
var PluginParameters = [{ name: 'Show Names', type: 'checkbox', defaultValue: 1 }];

Trace('Trace Demo is running: play some notes.');

function HandleMIDI(event) {
  if (GetParameter('Show Names') === 1 && (event instanceof NoteOn || event instanceof NoteOff)) {
    Trace(MIDI.noteName(event.pitch) + ' velocity ' + event.velocity);
  } else {
    event.trace();
  }
  event.send();
}

function ParameterChanged(index, value) {
  Trace('Parameter ' + index + ' is now ' + value);
}
`
  }
];

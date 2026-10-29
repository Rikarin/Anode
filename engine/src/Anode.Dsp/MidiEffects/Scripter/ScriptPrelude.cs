namespace Anode.Dsp.MidiEffects.Scripter;

/// <summary>
///     The JavaScript the Scripter compiles before a script (as <c>&lt;prelude&gt;</c>, so errors raised in it report
///     the script's line): Logic's Scripter API (docs/scripter/README.md) on top of an_script's <c>__emit</c> and
///     <c>__trace</c> natives.
/// </summary>
/// <remarks>
///     <para>The host drives it with global functions:</para>
///     <list type="bullet">
///         <item><c>__block(playing, startBeat, endBeat, tempo, numerator, denominator, frames, sampleRate)</c> per block;</item>
///         <item><c>__param(index, value)</c> for a value the host set: returns the value fitted to the parameter;</item>
///         <item><c>__midi(status, data1, data2, frame, beat)</c> per incoming event (HandleMIDI, or send it on);</item>
///         <item><c>__process(idle)</c> after the block's events (ProcessMIDI; Idle when idle is 1);</item>
///         <item>
///             <c>__reset()</c> (Reset), <c>__init()</c> after a compile (the parameter count),
///             <c>__restore(index, value)</c> (a stored value, without ParameterChanged), <c>__changedAll()</c>
///             (ParameterChanged for every parameter), <c>__sync()</c> (every value again, as <c>-2</c> records) and
///             <c>__describe()</c> (the parameters and targets, for <c>eval_json</c>).
///         </item>
///     </list>
///     <para>Each <c>__emit</c> record is eight numbers:</para>
///     <list type="bullet">
///         <item>
///             a MIDI event: <c>status</c> (with the channel), <c>data1</c>, <c>data2</c>, <c>mode</c> (0 now, 1 at
///             beat <c>when</c>, 2 <c>when</c> ms after the event), <c>when</c>, <c>frame</c> (the event's frame in the
///             block: where "now" and the milliseconds count from);
///         </item>
///         <item><c>-1</c> a TargetEvent: target index, value (0 … 1), then mode, when and frame as above;</item>
///         <item><c>-2</c> the script set a parameter: index, value;</item>
///         <item><c>-3</c> the parameter list or the target names changed: the host reads <c>__describe()</c>;</item>
///         <item><c>-4</c> <c>MIDI.allNotesOff()</c>: note-offs, at the block's first frame, for the notes sounding.</item>
///     </list>
/// </remarks>
internal static class ScriptPrelude {
    public const string Name = "<prelude>";

    public const string Source = """
                                 var __t = {
                                   playing: false, blockStartBeat: 1, blockEndBeat: 1, blockLength: 0, tempo: 120,
                                   meterNumerator: 4, meterDenominator: 4, cycling: false, leftCycleBeat: 1, rightCycleBeat: 1
                                 };
                                 var __qps = 120 / (60 * 48000);
                                 var __now = 0; // the frame of the event being handled: where new events start
                                 var __params = [];
                                 var __values = [];
                                 var __byName = Object.create(null);
                                 var __changing = [];
                                 var __targetNames = [];
                                 var __targetIndex = Object.create(null);

                                 function __clamp(v, lo, hi) { v = +v; if (!(v >= lo)) return lo; return v > hi ? hi : v; }
                                 function __data(v) { return __clamp(Math.round(v), 0, 127); }
                                 function __channel(e) { return __clamp(Math.round(e.channel), 1, 16) - 1; }

                                 function __copy(target, source) {
                                   if (!source || typeof source !== "object") return;
                                   for (var k in target) {
                                     if (k.charCodeAt(0) !== 95 && source[k] !== undefined) target[k] = source[k];
                                   }
                                   if (source.__offset !== undefined) target.__offset = source.__offset;
                                 }

                                 function __base(e) { return e.beatPos > 0 ? e.beatPos : __t.blockStartBeat + e.__offset * __qps; }

                                 function __send(e, mode, when) {
                                   if (!(when >= 0) && mode !== 1) when = 0;
                                   var kind = e.__kind();
                                   if (kind === -1) {
                                     return __emit(-1, __target(String(e.target)), __clamp(e.value, 0, 1), mode, when, e.__offset);
                                   }
                                   var status = kind < 0xF0 ? kind | __channel(e) : kind;
                                   return __emit(status, e.__d1(), e.__d2(), mode, when, e.__offset);
                                 }

                                 function __target(name) {
                                   var i = __targetIndex[name];
                                   if (i === undefined) {
                                     i = __targetNames.length;
                                     __targetNames.push(name);
                                     __targetIndex[name] = i;
                                     __emit(-3);
                                   }
                                   return i;
                                 }

                                 globalThis.Event = class Event {
                                   constructor(e) {
                                     this.channel = 1;
                                     this.port = 1;
                                     this.beatPos = 0;
                                     this.articulationID = 0;
                                     this.isRealtime = false;
                                     this.__offset = __now;
                                     if (new.target === Event) {
                                       this.status = 0x80;
                                       this.data1 = 0;
                                       this.data2 = 0;
                                     }
                                     __copy(this, e);
                                   }
                                   __kind() { var s = Math.round(this.status) & 0xFF; return s < 0xF0 ? s & 0xF0 : s; }
                                   __d1() { return __data(this.data1); }
                                   __d2() { return __data(this.data2); }
                                   send() { __send(this, 0, 0); }
                                   sendAfterMilliseconds(ms) { __send(this, 2, +ms); }
                                   sendAtBeat(beat) { __send(this, 1, +beat); }
                                   sendAfterBeats(beats) { __send(this, 1, __base(this) + Math.max(0, +beats || 0)); }
                                   trace() { Trace(this.toString()); }
                                   toString() {
                                     return "[Event channel:" + this.channel + " status:" + this.status + " data1:" + this.data1 +
                                       " data2:" + this.data2 + "]";
                                   }
                                 };

                                 globalThis.Note = class Note extends Event {
                                   constructor(e) { super(); this.pitch = 60; this.velocity = 100; __copy(this, e); }
                                   __d1() { return __data(this.pitch); }
                                   __d2() { return __data(this.velocity); }
                                   toString() {
                                     return "[" + this.constructor.name + " channel:" + this.channel + " pitch:" + this.pitch + " [" +
                                       MIDI.noteName(this.pitch) + "] velocity:" + this.velocity + "]";
                                   }
                                 };

                                 globalThis.NoteOn = class NoteOn extends Note {
                                   constructor(e) { super(); __copy(this, e); }
                                   __kind() { return 0x90; }
                                 };

                                 globalThis.NoteOff = class NoteOff extends Note {
                                   constructor(e) { super(); this.velocity = 64; __copy(this, e); }
                                   __kind() { return 0x80; }
                                 };

                                 globalThis.PolyPressure = class PolyPressure extends Event {
                                   constructor(e) { super(); this.pitch = 60; this.value = 0; __copy(this, e); }
                                   __kind() { return 0xA0; }
                                   __d1() { return __data(this.pitch); }
                                   __d2() { return __data(this.value); }
                                   toString() {
                                     return "[PolyPressure channel:" + this.channel + " pitch:" + this.pitch + " [" +
                                       MIDI.noteName(this.pitch) + "] value:" + this.value + "]";
                                   }
                                 };

                                 globalThis.ControlChange = class ControlChange extends Event {
                                   constructor(e) { super(); this.number = 1; this.value = 0; __copy(this, e); }
                                   __kind() { return 0xB0; }
                                   __d1() { return __data(this.number); }
                                   __d2() { return __data(this.value); }
                                   toString() {
                                     return "[ControlChange channel:" + this.channel + " number:" + this.number + " [" +
                                       MIDI.ccName(this.number) + "] value:" + this.value + "]";
                                   }
                                 };

                                 globalThis.ProgramChange = class ProgramChange extends Event {
                                   constructor(e) { super(); this.number = 0; __copy(this, e); }
                                   __kind() { return 0xC0; }
                                   __d1() { return __data(this.number); }
                                   __d2() { return 0; }
                                   toString() { return "[ProgramChange channel:" + this.channel + " number:" + this.number + "]"; }
                                 };

                                 globalThis.ChannelPressure = class ChannelPressure extends Event {
                                   constructor(e) { super(); this.value = 0; __copy(this, e); }
                                   __kind() { return 0xD0; }
                                   __d1() { return __data(this.value); }
                                   __d2() { return 0; }
                                   toString() { return "[ChannelPressure channel:" + this.channel + " value:" + this.value + "]"; }
                                 };

                                 globalThis.PitchBend = class PitchBend extends Event {
                                   constructor(e) { super(); this.value = 0; __copy(this, e); }
                                   __kind() { return 0xE0; }
                                   __bend() { return __clamp(Math.round(this.value), -8192, 8191) + 8192; }
                                   __d1() { return this.__bend() & 0x7F; }
                                   __d2() { return this.__bend() >> 7; }
                                   toString() { return "[PitchBend channel:" + this.channel + " value:" + this.value + "]"; }
                                 };

                                 globalThis.TargetEvent = class TargetEvent extends Event {
                                   constructor(e) { super(); this.target = ""; this.value = 0; __copy(this, e); }
                                   __kind() { return -1; }
                                   toString() { return "[TargetEvent target:" + this.target + " value:" + this.value + "]"; }
                                 };

                                 var __noteLetters = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
                                 var __ccNames = [];
                                 (function () {
                                   var named = {
                                     0: "Bank MSB", 1: "Modulation", 2: "Breath", 4: "Foot Control", 5: "Portamento Time", 6: "Data MSB",
                                     7: "Volume", 8: "Balance", 10: "Pan", 11: "Expression", 12: "Effect 1", 13: "Effect 2", 32: "Bank LSB",
                                     38: "Data LSB", 64: "Sustain", 65: "Portamento", 66: "Sostenuto", 67: "Soft Pedal", 68: "Legato",
                                     69: "Hold 2", 71: "Resonance", 72: "Release Time", 73: "Attack Time", 74: "Brightness",
                                     84: "Portamento Control", 91: "Reverb", 92: "Tremolo", 93: "Chorus", 94: "Detune", 95: "Phaser",
                                     96: "Data Increment", 97: "Data Decrement", 98: "NRPN LSB", 99: "NRPN MSB", 100: "RPN LSB",
                                     101: "RPN MSB", 120: "All Sound Off", 121: "Reset Controllers", 122: "Local Control",
                                     123: "All Notes Off", 124: "Omni Off", 125: "Omni On", 126: "Mono On", 127: "Poly On"
                                   };
                                   for (var i = 0; i < 128; i++) __ccNames.push(named[i] || "Controller " + i);
                                 })();

                                 globalThis.MIDI = {
                                   _noteNames: [],
                                   _ccNames: __ccNames,
                                   noteName: function (n) {
                                     n = Math.round(n);
                                     if (!(n >= 0 && n <= 127)) return undefined;
                                     return __noteLetters[n % 12] + (Math.floor(n / 12) - 2);
                                   },
                                   noteNumber: function (name) {
                                     var m = /^\s*([A-Ga-g])([#b♯♭]?)(-?\d+)\s*$/.exec(String(name));
                                     if (!m) return undefined;
                                     var n = { c: 0, d: 2, e: 4, f: 5, g: 7, a: 9, b: 11 }[m[1].toLowerCase()];
                                     if (m[2] === "#" || m[2] === "♯") n++;
                                     if (m[2] === "b" || m[2] === "♭") n--;
                                     n += (parseInt(m[3], 10) + 2) * 12;
                                     return n >= 0 && n <= 127 ? n : undefined;
                                   },
                                   ccName: function (n) { n = Math.round(n); return n >= 0 && n <= 127 ? __ccNames[n] : undefined; },
                                   allNotesOff: function () { __emit(-4, 0, 0); },
                                   normalizeStatus: function (v) { return __clamp(Math.round(v), 0x80, 0xEF); },
                                   normalizeChannel: function (v) { return __clamp(Math.round(v), 1, 16); },
                                   normalizeData: function (v) { return __data(v); }
                                 };
                                 for (var __n = 0; __n < 128; __n++) MIDI._noteNames.push(MIDI.noteName(__n));

                                 function Trace(value) { __trace(value === undefined ? "undefined" : String(value)); }

                                 function GetTimingInfo() { return __t; }

                                 function __normalize(p, i) {
                                   if (!p || typeof p !== "object") p = {};
                                   var type = String(p.type || "lin");
                                   var d = {
                                     name: p.name === undefined ? "Parameter " + (i + 1) : String(p.name),
                                     type: type, min: 0, max: 1, default: 0, steps: 0, valueStrings: null,
                                     unit: p.unit === undefined ? "" : String(p.unit), automatable: !p.disableAutomation
                                   };
                                   if (type === "menu") {
                                     var strings = Array.isArray(p.valueStrings) ? p.valueStrings.map(String) : [];
                                     if (strings.length === 0) strings = [""];
                                     d.valueStrings = strings;
                                     d.max = strings.length - 1;
                                     d.steps = strings.length - 1;
                                   } else if (type === "checkbox" || type === "momentary") {
                                     d.steps = 1;
                                     d.valueStrings = ["Off", "On"];
                                   } else if (type === "text") {
                                     d.max = 0;
                                     d.automatable = false;
                                   } else {
                                     if (type !== "log") d.type = "lin";
                                     var lo = +p.minValue, hi = +p.maxValue;
                                     d.min = isFinite(lo) ? lo : 0;
                                     d.max = isFinite(hi) ? hi : d.min + 1;
                                     if (d.max < d.min) { var x = d.min; d.min = d.max; d.max = x; }
                                     var steps = Math.round(+p.numberOfSteps);
                                     d.steps = steps > 0 ? steps : 0;
                                   }
                                   d.default = d.type === "momentary" ? 0 : __fit(d, p.defaultValue === undefined ? d.min : +p.defaultValue);
                                   return d;
                                 }

                                 function __fit(d, v) {
                                   v = +v;
                                   if (!isFinite(v)) v = d.default;
                                   if (v < d.min) v = d.min;
                                   if (v > d.max) v = d.max;
                                   if (d.steps > 0 && d.max > d.min) {
                                     var step = (d.max - d.min) / d.steps;
                                     v = d.min + Math.round((v - d.min) / step) * step;
                                   }
                                   return v;
                                 }

                                 function __rebuild(keep) {
                                   var old = Object.create(null);
                                   if (keep) for (var j = 0; j < __params.length; j++) old[__params[j].name] = __values[j];
                                   var list = Array.isArray(globalThis.PluginParameters) ? globalThis.PluginParameters : [];
                                   __params = [];
                                   __values = [];
                                   __byName = Object.create(null);
                                   for (var i = 0; i < list.length && i < 128; i++) {
                                     var d = __normalize(list[i], i);
                                     if (d.name in __byName) {
                                       var k = 2;
                                       while ((d.name + " " + k) in __byName) k++;
                                       d.name = d.name + " " + k;
                                     }
                                     __byName[d.name] = i;
                                     __params.push(d);
                                     __values.push(keep && d.name in old ? __fit(d, old[d.name]) : d.default);
                                   }
                                 }

                                 function __index(p) {
                                   if (typeof p === "number") { p = Math.round(p); return p >= 0 && p < __params.length ? p : -1; }
                                   var i = __byName[String(p)];
                                   return i === undefined ? -1 : i;
                                 }

                                 function __notify(i, v) {
                                   if (typeof ParameterChanged !== "function" || __changing[i] || __params[i].type === "text") return;
                                   __changing[i] = true;
                                   try { ParameterChanged(i, v); } finally { __changing[i] = false; }
                                 }

                                 function GetParameter(p) { var i = __index(p); return i < 0 ? undefined : __values[i]; }

                                 function SetParameter(p, v) {
                                   var i = __index(p);
                                   if (i < 0) return;
                                   v = __fit(__params[i], v);
                                   if (v === __values[i]) return;
                                   __values[i] = v;
                                   __emit(-2, i, v);
                                   __notify(i, v);
                                 }

                                 function UpdatePluginParameters() { __rebuild(true); __emit(-3); }

                                 function __init() { __rebuild(false); return __params.length; }

                                 function __describe() {
                                   var parameters = [];
                                   for (var i = 0; i < __params.length; i++) {
                                     var d = __params[i];
                                     parameters.push({
                                       name: d.name, type: d.type, min: d.min, max: d.max, default: d.default, steps: d.steps,
                                       valueStrings: d.valueStrings, unit: d.unit, automatable: d.automatable, value: __values[i]
                                     });
                                   }
                                   return { parameters: parameters, targets: __targetNames };
                                 }

                                 function __sync() {
                                   for (var i = 0; i < __params.length; i++) __emit(-2, i, __values[i]);
                                   return __params.length;
                                 }

                                 function __changedAll() { for (var i = 0; i < __params.length; i++) __notify(i, __values[i]); }

                                 function __restore(i, v) {
                                   if (!(i >= 0 && i < __params.length)) return NaN;
                                   return __values[i] = __fit(__params[i], v);
                                 }

                                 function __param(i, v) {
                                   if (!(i >= 0 && i < __params.length)) return NaN;
                                   v = __fit(__params[i], v);
                                   __values[i] = v;
                                   __notify(i, v);
                                   return v;
                                 }

                                 function __block(playing, startBeat, endBeat, tempo, numerator, denominator, frames, sampleRate) {
                                   __t.playing = playing !== 0;
                                   __t.blockStartBeat = startBeat;
                                   __t.blockEndBeat = endBeat;
                                   __t.blockLength = endBeat - startBeat;
                                   __t.tempo = tempo;
                                   __t.meterNumerator = numerator;
                                   __t.meterDenominator = denominator;
                                   __qps = tempo / (60 * sampleRate);
                                   __now = 0;
                                 }

                                 function __midi(status, data1, data2, frame, beat) {
                                   var kind = status & 0xF0, e;
                                   __now = frame;
                                   switch (kind) {
                                     case 0x90:
                                       if (data2 > 0) { e = new NoteOn(); e.pitch = data1; e.velocity = data2; }
                                       else { e = new NoteOff(); e.pitch = data1; e.velocity = 0; }
                                       break;
                                     case 0x80: e = new NoteOff(); e.pitch = data1; e.velocity = data2; break;
                                     case 0xA0: e = new PolyPressure(); e.pitch = data1; e.value = data2; break;
                                     case 0xB0: e = new ControlChange(); e.number = data1; e.value = data2; break;
                                     case 0xC0: e = new ProgramChange(); e.number = data1; break;
                                     case 0xD0: e = new ChannelPressure(); e.value = data1; break;
                                     case 0xE0: e = new PitchBend(); e.value = ((data2 << 7) | data1) - 8192; break;
                                     default: e = new Event(); e.status = status; e.data1 = data1; e.data2 = data2; break;
                                   }
                                   if (kind < 0xF0) e.channel = (status & 0x0F) + 1;
                                   e.beatPos = beat;
                                   e.__offset = frame;
                                   if (typeof HandleMIDI === "function") HandleMIDI(e); else e.send();
                                   __now = 0;
                                 }

                                 function __process(idle) {
                                   if (typeof ProcessMIDI === "function") ProcessMIDI();
                                   if (idle && typeof Idle === "function") Idle();
                                 }

                                 function __reset() { if (typeof Reset === "function") Reset(); }
                                 """;
}

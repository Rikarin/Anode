# anode-midiprobe

A test tool (Phase 8, [docs/09-phase-8.md](../../../docs/09-phase-8.md)) that stands in for MIDI hardware and for
another app in the Link session: it talks to Anode's virtual MIDI ports (or any named pair), sends scripted messages
(SysEx included), records everything that arrives with host timestamps, plays a Mackie Control or HUI surface well
enough for the golden path, acts as a MIDI Clock or MTC master, and joins the Link session. macOS (CoreMIDI) only for
the ports; Link anywhere an_link is built.

Built with the solution (`dotnet build engine/Anode.slnx`) into
`engine/tools/Anode.MidiProbe/bin/<Configuration>/net10.0/`: run `anode-midiprobe` there, or
`dotnet exec anode-midiprobe.dll`. It needs `engine/native/build.sh` to have run (cth_audio, an_link).

## Modes

- `anode-midiprobe [serve]`: long-running. One JSON command a line on stdin; one JSON object a line on stdout. The
  first line out is `{"event":"ready","version":"…"}`. Each command's answer carries the command's `id` (any JSON
  value; echoed), `ok` (`true`, or `false` with `error`), and its results. Commands run in order, except `wait`,
  `open`, `linkJoin` and `script` (unless `"wait": false`), which run beside the others, so a `wait` can be pending
  while you `send`. `{"cmd":"quit"}` answers and exits; so does closing stdin.
- `anode-midiprobe run <commands.json> [--output <results.json>]`: runs a JSON array of commands in order and writes
  the array of answers (to stdout without `--output`). Exit code 0 when every answer is ok.

From Playwright (Node):

```js
const probe = spawn(exe, ['serve']);
const lines = readline.createInterface({ input: probe.stdout });
// write `${JSON.stringify({ id: 1, cmd: 'open' })}\n` to probe.stdin; match answers by id.
```

## Conventions

- **Times** are host nanoseconds on the `cth_midi_now_ns` clock (macOS `mach_absolute_time`, the clock Anode's host
  stamps MIDI and its audio callbacks with). `{"cmd":"now"}` returns it. A received message's `t` is the CoreMIDI
  packet's timestamp (when its sender scheduled it), else its arrival.
- **Bytes** are `data` (an array of numbers) or `hex` (`"903C64"`, spaces allowed). Answers give `hex`, upper case,
  no spaces. A command may carry several whole messages (`"B00740B00A40"`), SysEx included.
- **Recording**: from start-up, every message on the probe's input (after `open`/`virtual`, only that input) is kept
  in arrival order until `clear` (or `received` with `"clear": true`). Analyses and `wait` read it; `since` (ns) and
  `prefix` (hex the message starts with) narrow it.

## Commands

### Ports

| Command | Fields | Answer |
|---|---|---|
| `ports` | | `ports: [{uid, name, input}]` as this process sees them (Anode's virtual ports appear as `anode:out`, an input, and `anode:in`, an output) |
| `open` | `in` (default `anode:out`), `out` (default `anode:in`), by uid or name; `timeoutMs` (5000) | `in`, `out`: the ports. Waits for them to appear. The probe records from `in` and sends to `out` |
| `virtual` | `prefix` (`probe`), `name` (`Anode Probe`) | Publishes the probe's own pair: "<name> In" (`<prefix>:in`, an output for Anode) and "<name> Out" (`<prefix>:out`, an input for Anode), and talks through them. `anodeInput`/`anodeOutput`: the uids Anode uses (e.g. a surface's `inputPort` `probe:out`, `outputPort` `probe:in`) |

### Sending and recording

| Command | Fields | Answer |
|---|---|---|
| `now` | | `now` |
| `send` | `data`/`hex`; `at` (ns) or `inMs` (from now); default now | `sent` (bytes), `at` |
| `script` | `events: [{t: ms from the start, data/hex}]`, `startInMs` (20), `wait` (true) | `start`, `end` (ns), `sent`. Each event goes out timestamped at its time (sent about 2 ms ahead) |
| `received` | `since`, `prefix`, `clear` | `count`, `messages: [{t, hex}]` |
| `wait` | `count` (1), `since`, `prefix`, `timeoutMs` (5000) | as `received`, once `count` messages match; `ok: false` on time-out |
| `clear` | | |
| `watch` | `on` (true) | While on, each message received is also printed unasked: `{"event":"midi","t":…,"hex":"…"}` |

### MIDI Clock and MTC master

| Command | Fields | Answer |
|---|---|---|
| `clock` | `bpm` (120); `start` (true: Start) or `sixteenths` (a Song Position Pointer and Continue instead); `preRollMs` (clocks that long before the start) | `start` (ns of the first clock after Start), `bpm`. 24 clocks a beat, each timestamped exactly, sent 20 ms ahead |
| `clockTempo` | `bpm` | from the next clock |
| `clockStop` | `keepTicking` (false) | `stop` (ns). Stop after the last clock sent |
| `mtc` | `rate` (`fps24`, `fps25` default, `fps2997Drop`, `fps30`; also `24`, `25`, `29.97`, `30`), `from` (`"hh:mm:ss:ff"`) or `seconds` | `start` (ns at which the timecode is `from`), `seconds`, `rate`. A full frame just before, then quarter frames |
| `mtcStop` | | Quarter frames stop |

### Analyses (of what was received)

| Command | Fields | Answer |
|---|---|---|
| `analyzeClock` | `since`, `prefix` | `clocks`; `meanMs`, `medianMs`, `bpm` (of the median interval), `maxDeviationMs`, `p99DeviationMs`, `outliers` (intervals more than 0.5 ms off the median), `stdDevMs`, `minMs`, `maxMs`; `transport: [{t, kind: start/continue/stop/songPosition, sixteenths?}]` in order. Intervals across a Start or Stop are left out |
| `analyzeMtc` | `rate` (the expected one; the timecode's own wins), `since` | `rate`, `count`, `positions: [{t, seconds, label, fullFrame}]` (a full frame, or a completed set of eight quarter frames, compensated to the time of its last piece), `last`, `maxDriftFrames` (the quarter frames' positions against their times, from the first) |
| `mcuState` | `extender` (false: device id 0x14; true: 0x15), `since` | What Anode sent a Mackie Control: `faders` (8, 0…1, from 14-bit pitch bend), `master`, `lines` (the scribble strip's two 56-character lines), `strips: [{top, bottom}]` (7 characters each), `leds: {note: velocity}` (on), `timecode` (10 digits, the display's left to right), `assignment` (2), `meters` (8, the last level 0–12 per strip) |

### Control surfaces

| Command | Fields | Sends |
|---|---|---|
| `mcuFader` | `strip` (0–7, 8 the master), `value` (0…1), `touch` (true), `release` (true) | `90 68+s 7F`, `Es lsb msb`, `90 68+s 00` |
| `mcuButton` | `button` (name below; strip buttons add `strip`) or `note`; `action` (`click` default, `press`, `release`) | `90 nn 7F` and/or `90 nn 00` |
| `mcuVpot` | `strip`, `ticks` (−63…63; negative counter-clockwise) | `B0 10+s vv` (bit 6 the direction) |
| `huiFader` | `strip` (0–7), `value` (0…1), `release` (true) | `B0 0F s`, `B0 2F 40` (touch), `B0 s msb`, `B0 20+s lsb`, then `B0 0F s`, `B0 2F 00` |
| `huiPing` | `on` (true) | Answers each host ping `90 00 00` with `90 00 7F` |

Mackie Control button names: `recArm`, `solo`, `mute`, `select`, `vpotPush` (+ strip), `assignTrack`, `assignSend`,
`assignPan`, `assignPlugin`, `assignEq`, `assignInstrument`, `bankLeft`, `bankRight`, `channelLeft`, `channelRight`,
`flip`, `global`, `nameValue`, `smpteBeats`, `f1`…`f8`, `read`, `write`, `trim`, `touch`, `latch`, `group`, `save`,
`undo`, `cancel`, `enter`, `marker`, `nudge`, `cycle`, `drop`, `replace`, `click`, `soloGlobal`, `rewind`, `forward`,
`stop`, `play`, `record`, `up`, `down`, `left`, `right`, `zoom`, `scrub`, `faderTouch` (+ strip). These follow the
protocol as Logic's surfaces speak it; the byte vectors Anode's implementation is tested against are
`engine/tests/Anode.Control.Tests/McuVectors.json` and `HuiVectors.json` (workstream D), and `send` takes any of them.

### Ableton Link

| Command | Fields | Answer (all: `tempo`, `peers`, `playing`, `now`) |
|---|---|---|
| `linkJoin` | `bpm` (optional), `startStop` (false), `peers` (1), `timeoutMs` (5000) | Joins, waits for `peers` other peers, then sets the session's tempo to `bpm` (a joining peer takes the session's tempo, so the probe sets its own once in) |
| `linkTempo` | `bpm` | Sets the session's tempo |
| `linkPlay` | `playing` (true), `quantum` (4) | Starts (at beat 0 of the quantum) or stops the session's transport (shared with start/stop sync) |
| `linkState` | `at` (array of host ns; default now), `quantum` (4) | `beats: [{at, beat, phase}]` |
| `linkClockError` | `quantum` (4), `skip` (24), `since` | How far the MIDI Clock received (Anode's, following Link) is from the session's beat: each clock's beat as the clock counts it (from Start, or the pointer and Continue) against Link's phase at its timestamp. `clocks`, `maxAbsMs`, `meanMs` |
| `linkLeave` | | Leaves the session |

## The golden path's steps 6–8 with it

```jsonc
{"id":1,"cmd":"open"}                                   // anode:out → probe, probe → anode:in
// 6. Anode sends clock and MTC to anode:out at 120 bpm; play 3 s, locate, stop:
{"id":2,"cmd":"analyzeClock"}      // bpm ≈ 120, medianMs ≈ 20.833, p99DeviationMs < 0.5; transport: start … stop, songPosition
{"id":3,"cmd":"analyzeMtc","rate":"fps25"}  // positions[].seconds against the playhead; maxDriftFrames < 1
// 7.
{"id":4,"cmd":"clock","bpm":100}  // Anode (source midiClock, input anode:in) plays; syncState.externalTempo ≈ 100
{"id":5,"cmd":"clockStop"}        // Anode stops
{"id":6,"cmd":"mtc","from":"00:00:10:00"}   // Anode (source mtc) chases to 10 s (less the offset) and plays
{"id":7,"cmd":"mtcStop"}          // Anode stops
// 8.
{"id":8,"cmd":"linkJoin","bpm":128}   // Anode's syncState.externalTempo becomes 128
{"id":9,"cmd":"linkState"}            // after a tempo change in Anode: tempo is Anode's
{"id":10,"cmd":"linkClockError"}      // with Anode playing and sending clock: maxAbsMs < 1
```

On the null device in a busy JIT host, the device thread is now and then held up (a garbage collection) and realigns:
its audio stalls and so does the clock, which shows as one long interval among the clocks. `p99DeviationMs` and
`outliers` separate that from jitter.

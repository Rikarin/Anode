# OSC

Anode listens for OSC 1.0 over UDP, on port 8000 by default (the OSC settings). On macOS it advertises itself
over Bonjour as `_osc._udp`. Every address is under `/anode`.

The code is in `engine/src/Anode.Control/Osc`:

- `OscCodec` is the binary format.
- `OscAddressSpace.Resolve` maps a message to a request.
- `OscAddressSpace.Feedback` lists the values clients receive.

[README.md](README.md) covers the other control protocols.

## Format

- **Messages** carry an address, a type tag string and arguments. **Bundles** carry `#bundle`, a 64-bit NTP time
  tag and sized elements, nested to 16 levels.
- Everything is big-endian and padded to 4 bytes.
- Strings are UTF-8, NUL-terminated and padded.
- The argument types Anode reads and writes:

  | Tag | Type |
  |---|---|
  | `i` | int32 |
  | `f` | float32 |
  | `s` | string |
  | `b` | blob (int32 size, the bytes, padding) |
  | `T` / `F` | true / false (no data) |

- When reading, Anode also accepts:
  - `h` (int64), read as `i` when it fits;
  - `d` (float64), read as `f`;
  - `S`, read as `s`;
  - `N` and `I`, which are skipped;
  - a message without a type tag string, which has no arguments.
- A malformed packet is dropped. `OscCodec.Decode` throws only `FormatException`, and `TryDecode` returns false.
- Bundle time tags are not scheduled: a bundle's messages apply on arrival, in order.

## Values

Values are normalised 0…1 unless the address says otherwise:

- A value argument can be `i`, `f`, `T` (1) or `F` (0). It is clamped to 0…1.
- Volume is the fader position on Anode's fader law, the same as a fader in the mixer. `…/volume/db` takes decibels.
- Pan is 0 for left, 0.5 for centre and 1 for right.
- Switches are 0 off and 1 on.
- A **press** address (transport functions, markers, key commands, select next and previous) acts on 1, or on no
  argument. It ignores 0, except that rewind and forward stop on 0, so a button that sends 1 and then 0 works.
- An address that does not exist, or that has the wrong arguments, is ignored. That includes a track index past the
  last track, and `/anode/selected/…` with no track selected.

A control move from OSC is the same change an editor's would be. It is undoable, coalesced per gesture, and written as
automation when the track's automation mode writes.

## Addresses

`{n}` is a 1-based track index in mixer order. `{id}` is a track id. `{k}` is a 1-based send index.

### Transport

| Address | Arguments | Does |
|---|---|---|
| `/anode/transport/play` | press | Play |
| `/anode/transport/stop` | press | Stop |
| `/anode/transport/record` | press | Record |
| `/anode/transport/cycle` | press | Turns the cycle on or off (a toggle). Feedback carries its state |
| `/anode/transport/rewind` | 1 / 0 | Rewinds while held |
| `/anode/transport/forward` | 1 / 0 | Fast-forwards while held |
| `/anode/transport/goToStart` | press | Moves the playhead to the project start |
| `/anode/transport/position` | `f` quarter notes | Moves the playhead to that many quarter notes from the start (0 or more) |

### Tracks

Each of these is available as `/anode/track/{n}/…`, `/anode/track/id/{id}/…` and `/anode/selected/…` (the selected
track).

| Address suffix | Arguments | Does |
|---|---|---|
| `…/volume` | value | Sets the volume (fader position) |
| `…/volume/db` | `i`/`f` dB | Sets the volume in dB |
| `…/pan` | value | Sets the pan |
| `…/mute` | value | Mute on (1) or off (0) |
| `…/solo` | value | Solo on or off |
| `…/arm` | value | Record arm on or off |
| `…/send/{k}` | value | Sets send k's level |
| `…/send/{k}/db` | `i`/`f` dB | Sets send k's level in dB |
| `…/name` | `s` | Renames the track |

Two more addresses work on the selection:

| Address | Arguments | Does |
|---|---|---|
| `/anode/selected/next` | press | Selects the track below |
| `/anode/selected/previous` | press | Selects the track above |

### Markers, Smart Controls and key commands

| Address | Arguments | Does |
|---|---|---|
| `/anode/marker/{m}` | press | Moves the playhead to marker m (1-based, in project order) |
| `/anode/marker/goto` | `i`/`f` m | The same, with the number as the argument |
| `/anode/smart/{id}` | value | Sets the selected track's Smart Control (`knob1`…) |
| `/anode/smart/{i}` | value | The same, by 1-based position among the selected track's Smart Controls |
| `/anode/key/{id}` | press | Runs the key command with that id (the rest of the address, e.g. `/anode/key/edit.undo`) |

### Feedback

| Address | Arguments | Does |
|---|---|---|
| `/anode/subscribe` | `i` port | Sends feedback to the sender's host on that UDP port for this session |
| `/anode/unsubscribe` | `i` port, or none | Stops feedback to that port of the sender's host, or to every port of it |

Clients listed in the OSC settings always receive feedback. Others subscribe.

## Feedback

Anode sends each address below whenever its value changes, at most 30 times a second per address. A new subscriber
first receives every address with its current value.

| Address | Type | Value |
|---|---|---|
| `/anode/transport/play` | `i` | 1 while playing or recording |
| `/anode/transport/stop` | `i` | 1 while stopped |
| `/anode/transport/record` | `i` | 1 while recording |
| `/anode/transport/cycle` | `i` | 1 while the cycle is on |
| `/anode/transport/tempo` | `f` | the tempo at the playhead, in bpm |
| `/anode/transport/beats` | `s` | the playhead as `bar.beat.division.ticks`, e.g. `7.2.3.120` |
| `/anode/transport/timecode` | `s` | the playhead as `HH:MM:SS:FF` |
| `/anode/tracks` | `i` | the number of tracks |
| `/anode/track/{n}/name` | `s` | the name |
| `/anode/track/{n}/volume` | `f` | the fader position |
| `/anode/track/{n}/volume/db` | `f` | the volume in dB |
| `/anode/track/{n}/pan` | `f` | the pan, 0…1 |
| `/anode/track/{n}/mute`, `/solo`, `/arm` | `i` | 0 or 1 |
| `/anode/track/{n}/send/{k}` | `f` | send k's level, normalised |
| `/anode/track/{n}/send/{k}/name` | `s` | send k's destination |
| `/anode/selected/index` | `i` | the selected track's index, 0 when none |
| `/anode/selected/name`, `/volume`, `/volume/db`, `/pan`, `/mute`, `/solo`, `/arm` | as above | the selected track's |
| `/anode/markers` | `i` | the number of markers |
| `/anode/marker/{m}/name` | `s` | marker m's name |
| `/anode/smart/{id}` | `f` | the selected track's Smart Control value |
| `/anode/smart/{id}/name` | `s` | its name |
| `/anode/smart/{id}/display` | `s` | its value as text |

Tracks are reported by index only, not by id.

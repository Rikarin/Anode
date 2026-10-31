# Control

How Anode is played and mixed from hardware: generic controller assignments, the Mackie Control, Mackie Control XT
and HUI surfaces, and OSC ([osc.md](osc.md)). The plan is [Phase 8](../09-phase-8.md).

The protocols are `engine/src/Anode.Control`, pure code with no I/O and no clock:

- `Assignments.AssignmentEngine` runs the controller assignments.
- `Surfaces.MackieControl` and `Surfaces.Hui` speak the surface protocols. `Surfaces.SurfaceGroup` joins units into one
  console.
- `Osc.OscCodec` and `Osc.OscAddressSpace` are OSC.

The server owns the ports, the clock and the rates, and turns what these classes report into commands.

## Routing

- A port given to a control surface is a control port. Its messages go to the surface, never to tracks.
- On any other port, a message that matches an assignment is consumed. The rest play the track as before.
- The host matches on its MIDI thread, from the server's table (`AssignmentEngine.Matchers`).
  - A matcher is a port (or any), a status nibble, a channel (or any) and a first data byte (or any).
  - A note matcher also takes its key's note offs.
  - A 14-bit matcher takes controllers n and n + 32.
  - An NRPN matcher takes CC 99, 98, 6, 38, 96 and 97 on its channel, whatever the parameter. The server's engine
    matches the parameter.
- While learning, the host plays every message and also forwards it to the server.

## Controller assignments

An assignment maps an input to a target, with a mode, a range and pickup. Assignments live in modes, the modes in
zones. Only each zone's active mode is live.

### Inputs

| Kind | Number | Value |
|---|---|---|
| `controlChange` | the controller 0…127 | 0…127 |
| `controlChange14` | the MSB's controller 0…31 (the LSB is n + 32) | 0…16383, read when the LSB arrives after the MSB |
| `nrpn` | the parameter 0…16383 (CC 99 MSB, 98 LSB) | 0…16383, read on CC 38 after CC 6. CC 96 steps up by one, 97 down |
| `note` | the key | note on: its velocity (a press). Note off or velocity 0: 0 (a release) |
| `pitchBend` | 0 | 0…16383 |
| `programChange` | the program | a press, with no value |

- The port is a `MidiPort.id`. None means any port that is not a surface's.
- The channel is 1…16. None means any channel.
- CC 101 or 100 (an RPN) ends the selected NRPN.
- The 14-bit halves and the NRPN selection are kept per port and channel.

### Matching

- The first assignment whose input a message belongs to takes it. The order is zone, mode, then list.
- The assignment takes the message even when it makes no change: an MSB alone, CC 99, or while pickup holds.
- Each message makes at most one change.

### Modes

Values are normalised 0…1 over the target's range. A volume's value is its fader position. `min` and `max` are the
assignment's range; `min` above `max` inverts it.

| Mode | Effect |
|---|---|
| `absolute` | value = min + (input / full scale) × (max − min). A program change sets max. NRPN increment/decrement step by one. |
| `relativeTwosComplement` | 1…63 up, 127…65 down by 1…63 (7-bit: v < 64 ? v : v − 128) |
| `relativeSignedBit` | 1…63 up, 65…127 down by 1…63 (bit 6 is the sign) |
| `relativeBinaryOffset` | v − 64: 65 is one up, 63 one down |
| `toggle` | each press flips the target between min and max. A press is a note on, a program change, or a value at or above half (64; 8192). |
| `momentary` | max while pressed, min on release |

- **Relative steps.** One step moves the target by (max − min) / 128 from its current value, clamped to the range. A
  14-bit or NRPN input in a relative mode reads the same encodings over 14 bits.
- **Toggle.** The target's current value decides which way it flips. With no current value, the assignment keeps its
  own state.
- **Functions** are transport, key commands, zone modes, and select next and previous. They get 1 on a press and 0 on
  a release, whatever the mode and range. They act on 1. Rewind and forward also stop on 0. A relative mode does
  nothing on a function.

### Pickup

Pickup applies to absolute mode. The control has no effect until its value comes within a step of the target's current
value, or crosses it from the last value the control sent.

Once picked up, the control follows. If the target moves elsewhere, for example in the editor, pickup holds again.
`Reset()` forgets pickup, toggles and partial messages. The server calls it when the selection or the project changes
under the assignments.

### Targets

- **`parameter`**: an automation address (`volume`, `pan`, `mute`, `send:…`, `insert:…:…`, `instrument:…`,
  `midiEffect:…:…`) on a fixed track. No track id means the selected track.
- **`smartControl`**: `knob1`… on the selected track.
- **`trackSwitch`**: `mute`, `solo` and `recordArm` (1 on, 0 off), and `selectNext` and `selectPrevious`.
- **`transport`**: `play`, `stop`, `record`, `cycle` (toggles), `rewind`, `forward` and `goToStart`.
- **`keyCommand`**: by id.
- **`zoneMode`**: makes a mode its zone's active mode, on press. The engine applies it itself, and `Assignments` shows
  the active modes as switched.

### Learn

`AssignmentEngine.Learn(port, message)` gives the input a message reads as:

- A note on is a note. A note off reads as nothing.
- A control change is 7-bit, unless its pair's other half (controller ± 32) came just before it on the same port and
  channel. Then it is the 14-bit pair. So an MSB alone first reads as 7-bit, and the LSB right after it reads as the
  pair.
- CC 6, 38, 96 or 97 after 99 and 98 is that NRPN. CC 99, 98, 101 and 100 alone read as nothing.

`AssignmentEngine.Assign(assignments, input, target, zoneId, modeId, newId)` makes the assignment:

- It goes in the given mode. With no zone, it goes in the first zone, made when there is none. With no mode, it goes
  in the zone's active mode.
- It replaces any assignment of the same input there.
- A 14-bit or NRPN input also replaces the 7-bit assignments of the same target that its halves made a moment before.
- The new assignment is a toggle for switches (track switches, the `mute` parameter) and absolute for everything
  else. Its range is 0…1, with no pickup.

## Control surfaces

Every surface implements `IControlSurface`:

- `Decode` turns input into `SurfaceAction`s.
- `Encode` turns a `ProjectView` into feedback, and sends only what changed since the last encode.
- `EncodeMeters` sends the meters.
- `Poll` sends the protocol's own messages and replies.
- `Invalidate` forgets what was sent, so the next encode sends everything.

A unit invalidates itself when it first connects. A fader that is touched is not sent. When it is released, its
position is sent again, and a fader the surface moved itself is not echoed back.

The server drives a set of surfaces as follows:

- **Grouping.** Put the surfaces in `SurfaceGroup`s, left to right in `order`. One group of every Mackie Control and
  XT is the usual setup. A HUI is usually its own group.
- **Input.** Pass each control port's input to `group.Decode(unit, bytes, timeNs)`, then call `Poll`, so that
  handshake and ping replies go out at once.
- **Feedback.** Call `group.Encode(view)` at most 30 times a second (faders and LEDs) and `group.EncodeMeters(view)`
  10 times a second. Call `group.Poll(now)` every 100 ms or so.
- **The view.** Build the `ProjectView` from the project, the transport and the meters:
  - The tracks are the ones a mixer shows, in order.
  - Only the selected track carries its inserts.
  - A track's fader is its position on the fader law, 0…1.

### Banking

- The group owns the bank. `BankBase` is the index of the track on the leftmost strip, and each unit's `BankOffset` is
  the base plus the strips to its left.
- A bank button (±8) or channel button (±1) on any unit moves the whole group, between 0 and the last track. The
  group reports `Banked` with the new base.
- `Encode` brings the bank back within the tracks when tracks have gone.
- The server reports each unit's `BankOffset` in `SurfaceStatus`.

Extenders take the main Mackie Control's VPot mode, send, plug-in page, name/value and display state. The main unit is
the group's first Mackie Control. In plug-in mode, the parameters run on across the units, left to right.

### Mackie Control and XT

The SysEx header is `F0 00 00 66 14` for the main unit and `F0 00 00 66 15` for an XT. The XT has the strip controls
only.

| Control | Message (unit → Anode) | In Anode |
|---|---|---|
| Fader 1–8 | pitch bend, channel 0–7, 14-bit | `setParameter` `volume` of the strip's track |
| Master fader | pitch bend, channel 8 | ignored (the view has no master strip) |
| Fader touch | note 0x68–0x6F (on 7F, off 00) | `touch` 1/0: a gesture on the volume, and no fader feedback while touched |
| VPot | CC 0x10–0x17: bit 6 counter-clockwise, low 6 bits the steps | `stepParameter` by the VPot mode, 1/128 of the range a step |
| VPot press | note 0x20–0x27 | pan: centres it. Plug-in, choosing: chooses the insert |
| Rec / Solo / Mute | notes 0x00–0x07 / 0x08–0x0F / 0x10–0x17 | `recordArm` / `solo` / `mute` with the new state (1 on) |
| Select | notes 0x18–0x1F | `select` the strip's track |
| Send / Pan / Plug-in | notes 0x29 / 0x2A / 0x2B | VPot mode. Send again steps to the next send. Plug-in again goes back to choosing |
| Track, EQ, Instrument | 0x28, 0x2C, 0x2D | ignored |
| Bank ◀ ▶ / Channel ◀ ▶ | notes 0x2E, 0x2F / 0x30, 0x31 | bank the group by 8 / 1. In plug-in mode, page the inserts or parameters instead |
| Name/Value | note 0x34 | the top line shows the VPot parameter's name instead of the track's |
| SMPTE/Beats | note 0x35 | the timecode display shows bars and beats, or timecode |
| Read/Off, Write, Touch, Latch | notes 0x4A, 0x4B, 0x4D, 0x4E | `setAutomationMode` of the selected track. Read/Off turns a track in read off |
| Trim | 0x4C | ignored |
| Cycle, Stop, Play, Record | notes 0x56, 0x5D, 0x5E, 0x5F | `cycle`, `stop`, `play`, `record` on press |
| Rewind, Fast forward | notes 0x5B, 0x5C | `rewind` / `forward`, 1 on press and 0 on release |
| Cursor ▲ ▼ ◀ ▶ | notes 0x60–0x63 | `cursorUp`, `cursorDown`, `cursorLeft`, `cursorRight` |
| Zoom | note 0x64 | `zoom` 1/0 (a toggle). While on, the server zooms with the cursor keys |
| Jog wheel | CC 0x3C, as a VPot | `jog` by the steps |
| Other buttons | | ignored |

**Handshake.**

- Until it is connected, Anode sends the Device Query `F0 00 00 66 14 00 F7` every 2 s.
- The unit answers with a Host Connection Query (0x01: serial ×7, challenge ×4). Anode replies with a Host Connection
  Reply (0x02: the serial, and the response from the protocol's published formula).
- A Connection Confirmation (0x03), or any other input, connects. A unit that skips the handshake works too.
- A Connection Error (0x04) or Go Offline (0x0F) disconnects.

**Feedback.** In the order `Encode` sends it:

- **Meter modes.** After an invalidate, channel meter mode `F0 … 20 0s 01 F7` for each strip: signal LED on, LCD meter
  off.
- **Scribble strip.** `F0 … 12 offset chars F7`. Two lines of 56 characters, 7 a strip (6 and a space). Offset 0 is
  the top line, 0x38 the bottom. Each changed line is sent from its first change to its last.
  - Pan mode: the track name on top (or "Pan" with Name/Value), and the pan below ("C", "L50", "R100").
  - Send mode: the track name (or the send's), and the send's level.
  - Plug-in mode: the inserts' names while choosing, then each parameter's name and value.
  - Names are abbreviated: spaces go first, then lowercase vowels from the end, keeping a trailing number ("Track 10"
    becomes "Trck10"). Text is printable ASCII.
- **Each strip.**
  - The fader: pitch bend, round(position × 16383).
  - The VPot ring: CC 0x30–0x37, (mode << 4) | position. Pan is a dot at 1…11, with 6 the centre. A send or a
    parameter is a fill at round(value × 11). An empty strip is off.
  - The LEDs, as notes with 7F on and 00 off: rec, solo, mute and select.
- **Main unit LEDs.**
  - The mode: Track off, Send, Pan, Plug-in.
  - The selected track's automation: Read, Write, Trim off, Touch, Latch.
  - The transport: Cycle; Stop while stopped; Play while playing or recording; Record while recording.
  - Zoom, SMPTE and Beats.
- **Assignment display.** CC 0x4B (left), 0x4A (right): "PN" for pan, "S1"… for sends, "PI" for plug-in.
- **Timecode display.** CC 0x49 is the leftmost digit and 0x40 the rightmost, in the unit's 7-segment set (letters
  minus 0x40, digits and space as ASCII; +0x40 lights the dot).
  - Timecode: `HHH MM SS FFF`.
  - Beats: `BBB bb dd ttt` (bar, beat, division, ticks).
  - Dots follow each group but the last.

**Meters.** Channel pressure `D0 (strip << 4) | level`:

- The level is 0…12: round(meter × 12).
- It falls at most one step per call, so 1.2 s from full to off at 10 calls a second.
- A strip is sent while its level is above 0, and once when it reaches 0.

### HUI

The ping:

- Anode sends `90 00 00` every second. The unit answers `90 00 7F`, which connects it, as does any other input.
- With no input for 3 s, the unit is offline.
- A unit that pings itself (`90 00 00`) is answered `90 00 7F`.

Switches arrive as a zone (CC 0x0F) and then a port (CC 0x2F: 0x40 | port pressed, port released). LEDs go out the
same way: CC 0x0C zone, CC 0x2C (0x40 | port on, port off), as one 6-byte buffer.

| Control | Message (unit → Anode) | In Anode |
|---|---|---|
| Fader 1–8 | CC 0x00–0x07 MSB, then CC 0x20–0x27 LSB | `setParameter` `volume`, read on the LSB |
| Fader touch | zone 0–7, port 0 | `touch` 1/0 |
| Select / Mute / Solo / Rec | zone 0–7, ports 1 / 2 / 3 / 7 | `select` / `mute` / `solo` / `recordArm` (the new state) |
| V-Sel | zone 0–7, port 5 | pan mode: centres the pan |
| VPot | CC 0x40–0x47: 0x40 + n is n steps clockwise, n alone n counter-clockwise | `stepParameter` (pan, or the send) |
| Channel ◀ / Bank ◀ / Channel ▶ / Bank ▶ | zone 0x0A, ports 0 / 1 / 2 / 3 | bank the group by 1 / 8 |
| Pan, Send A–E | zone 0x0B, port 2; ports 7, 6, 5, 4, 3 | the VPot mode (pan, or send 1–5) |
| Cursor ▼ ◀ ▶ ▲, Mode | zone 0x0D, ports 0, 1, 3, 4; 2 | cursor actions; Mode toggles `zoom` |
| Rewind, Fast forward | zone 0x0E, ports 1, 2 | `rewind` / `forward` 1 on press, 0 on release |
| Stop, Play, Record | zone 0x0E, ports 3, 4, 5 | `stop`, `play`, `record` |
| Loop | zone 0x0F, port 3 | `cycle` |
| Latch, Read, Off, Write, Touch | zone 0x18, ports 1–5 | `setAutomationMode` of the selected track |
| Jog | CC 0x0D, as a VPot | `jog` |

**Feedback.** In the order `Encode` sends it:

- **Strip names.** `F0 00 00 66 05 00 10 strip c1 c2 c3 c4 F7`, abbreviated as on Mackie Control ("Track 1" becomes
  "Trc1").
- **Each strip.**
  - The fader: `B0 0s msb B0 2s lsb`, one buffer.
  - The VPot ring: CC 0x10–0x17, as Mackie Control's.
  - The LEDs: select, mute, solo, rec.
- **LEDs.** Pan and Send A–E, Stop, Play, Record, Loop, and the selected track's automation mode.
- **Timecode.** `F0 00 00 66 05 00 11 d0…d7 F7`: eight digits 0–9, the rightmost first, `HHMMSSFF`.

**Meters.** Poly pressure, `A0 strip level A0 strip 0x10|level` (left, then right; both the track's meter), on the
same scale and with the same decay as Mackie Control.

## Vectors

`engine/tests/Anode.Control.Tests/McuVectors.json` and `HuiVectors.json` pin every byte of a set of sessions: decode,
encode, meter and poll steps on a `SurfaceGroup`, with their outputs. The file's `about` describes the format. The
server's tests replay them. Rewrite them with `ANODE_UPDATE_VECTORS=1 dotnet test` after a deliberate protocol change.

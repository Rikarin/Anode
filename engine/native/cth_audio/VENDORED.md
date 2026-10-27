# Vendored: `cth_audio`

Copied unchanged from Cathode (`engine/native/cth_audio`, GPL-3.0-or-later) on 2026-10-27. It stays byte-identical to
Cathode's copy until both repositories consume it from a shared package (docs/adr/0002-native-audio-backend.md); a fix
made here goes to Cathode too.

## Anode changes not yet in Cathode

Phase 2 (MIDI tracks) needed two additions to the MIDI API; they go to Cathode with the next sync:

- `cth_midi_event.source` (was `reserved[4]`; same size and offset): the index of the input that sent the event, as
  `cth_midi_port_info` numbers ports, so a track can take input from one device.
- `cth_midi_send_at`: a timestamped send. CoreMIDI schedules it; WinMM and the ALSA sequencer send at once, and the
  caller holds the event until it is due.

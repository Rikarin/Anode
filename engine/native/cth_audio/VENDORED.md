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

Phase 8 (control and sync, docs/09-phase-8.md) added, also for the next sync:

- SysEx in: a second ring of variable-length records (`cth_midi_read_sysex`; helpers in `src/cth_midi_sysex.h`),
  assembled per input from CoreMIDI's UMP type 3 pieces and WinMM's `MIM_LONGDATA` buffers. System common (MTC
  quarter frames, song position) now enters the short ring too. ALSA queues no SysEx yet.
- `cth_midi_send` (and `_at`) take any number of whole messages of any length, SysEx included (CoreMIDI: UMP types 1,
  2 and 3; WinMM: `midiOutShortMsg` and `midiOutLongMsg`). System messages now go out as UMP type 1 on CoreMIDI,
  where they used to be sent as type 2.
- Virtual ports (`cth_midi_virtual_open`, `_close`): CoreMIDI only (`MIDIDestinationCreate`, `MIDISourceCreate`), each
  carrying its uid as the endpoint property "cth uid" so every cth_audio process lists it under that uid; WinMM and
  ALSA return `CTH_MIDI_VIRTUAL_UNSUPPORTED`.
- CoreMIDI: the client is created on a thread that runs a run loop, so the endpoint lists follow ports that appear
  after it (another app's virtual port); without one they never change.

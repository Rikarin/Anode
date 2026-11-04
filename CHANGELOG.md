# Changelog

All notable changes to Anode are recorded here, by hand.

## Unreleased

- Bootstrap: repository layout, .NET engine solution (`anode-host`, `anode-server`), Nx editor workspace (Angular
  editor, Electron desktop), vendored `cth_audio` shim, CI workflows, implementation plan and ADRs 0001–0008.
- Missing audio files: a project opens, plays and saves without them, reports them, keeps their regions as offline, and
  the Missing Files window finds or relinks them, checking length, rate and channels.
- Channel strips: input trim, polarity and a track delay (late or early) on every full strip, dual pan for stereo
  signals, and the pan law as a project setting (schema 12; existing projects keep −3 dB compensated).
- Mixer: drag an insert or MIDI FX slot to another strip to move it with its state and automation, ⌥-drag to copy it;
  the slot's menu does the same by Copy to and Move to.
- Solo: solo safe on any full strip, AFL and PFL as a solo mode (schema 13), and a monitor section in the Mixer's toolbar
  with dim, mono and a reference level, which the speakers hear and a bounce does not.
- Meters: each strip, the master too, chooses Peak, RMS, K-20, K-14, K-12 or LUFS and reads at the input, before or
  after the fader (schema 14; existing strips keep post-fader peaks).
- Control bar: a load meter with a bar per core, the disk, and an overload light; a click shows the audio thread's load,
  its peak and the overload and underrun counts.

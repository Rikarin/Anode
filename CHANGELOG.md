# Changelog

All notable changes to Anode are recorded here, by hand.

## Unreleased

- Bootstrap: repository layout, .NET engine solution (`anode-host`, `anode-server`), Nx editor workspace (Angular
  editor, Electron desktop), vendored `cth_audio` shim, CI workflows, implementation plan and ADRs 0001–0008.
- Missing audio files: a project opens, plays and saves without them, reports them, keeps their regions as offline, and
  the Missing Files window finds or relinks them, checking length, rate and channels.
- Channel strips: input trim, polarity and a track delay (late or early) on every full strip, dual pan for stereo
  signals, and the pan law as a project setting (schema 12; existing projects keep −3 dB compensated).

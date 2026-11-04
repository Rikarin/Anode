# Changelog

All notable changes to Anode are recorded here, by hand.

## Unreleased

- Bootstrap: repository layout, .NET engine solution (`anode-host`, `anode-server`), Nx editor workspace (Angular
  editor, Electron desktop), vendored `cth_audio` shim, CI workflows, implementation plan and ADRs 0001–0008.
- Missing audio files: a project opens, plays and saves without them, reports them, keeps their regions as offline, and
  the Missing Files window finds or relinks them, checking length, rate and channels.
- Multipressor: each band's gain reduction reaches the editor (insert readings can now carry several values) and shows
  under its knobs. A meters frame without readings no longer blanks the reading a plugin window shows.
- Channel EQ and Linear Phase EQ: a draggable curve over a live analyser of the output, with a handle per band. An
  insert's live display is read only while a window shows it (`POST /plugins/displays`, a lease the window renews).
- New utility effects and meters, each with its panel: Tuner, Test Oscillator, Expander, Frequency Spread, Oscilloscope
  and Phase Meter with a goniometer (docs/effects/parity.md). Plugin windows show the MultiMeter's correlation as a
  correlation, not LUFS.
- Channel EQ: any band can work on the mid or the side alone, and a bell or shelf can be dynamic, its gain following its
  own band's level. Older projects and states load with both off and sound the same.

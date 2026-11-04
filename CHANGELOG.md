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

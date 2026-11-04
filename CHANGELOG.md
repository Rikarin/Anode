# Changelog

All notable changes to Anode are recorded here, by hand.

## Unreleased

- Bootstrap: repository layout, .NET engine solution (`anode-host`, `anode-server`), Nx editor workspace (Angular
  editor, Electron desktop), vendored `cth_audio` shim, CI workflows, implementation plan and ADRs 0001–0008.
- Missing audio files: a project opens, plays and saves without them, reports them, keeps their regions as offline, and
  the Missing Files window finds or relinks them, checking length, rate and channels.
- Nudge regions and jump by key: ⌥← and ⌥→ nudge the selected regions by the Tracks area's Nudge value (tick, frame,
  division, beat, bar), and key commands move the playhead to the next or previous transient, region edge and marker.
- Loop regions: audio, MIDI and pattern regions repeat by the inspector's Loop and Repeats or by dragging their upper
  right corner, compiled, bounced and exported to MIDI files; repeats stop at the next region, and Convert Loops to
  Regions (⌃L) makes them regions of their own. Project schema 12.

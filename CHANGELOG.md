# Changelog

All notable changes to Anode are recorded here, by hand.

## Unreleased

- Bootstrap: repository layout, .NET engine solution (`anode-host`, `anode-server`), Nx editor workspace (Angular
  editor, Electron desktop), vendored `cth_audio` shim, CI workflows, implementation plan and ADRs 0001–0008.
- Missing audio files: a project opens, plays and saves without them, reports them, keeps their regions as offline, and
  the Missing Files window finds or relinks them, checking length, rate and channels.
- Save As and Save a Copy: the whole package at a new place, built beside it and renamed in only when complete; Save As
  goes on there with its undo history, and a failure leaves the original and the session as they were.
- Import from Project: a browser reads another project without opening it, and chosen tracks join this one as one undo
  step with their regions, inserts, sends and automation, on buses of their own; media is copied, and converted from
  another sample rate.
- Project and track templates: the chooser offers factory and user templates, a project saves as one with its media, and
  tracks save with their routing, plugins and states and come back on buses of their own.
- Migration fixtures: a package written by the real build of each schema, 1 to 11, loads through the chain with what it
  held, with properties that the chain leaves what is there alone and that a damaged file is read or refused.
- Nudge regions and jump by key: ⌥← and ⌥→ nudge the selected regions by the Tracks area's Nudge value (tick, frame,
  division, beat, bar), and key commands move the playhead to the next or previous transient, region edge and marker.
- Loop regions: audio, MIDI and pattern regions repeat by the inspector's Loop and Repeats or by dragging their upper
  right corner, compiled, bounced and exported to MIDI files; repeats stop at the next region, and Convert Loops to
  Regions (⌃L) makes them regions of their own.
- Time-stretch regions: ⌥-drag an audio region's lower edge, or set the inspector's Stretch, and it lasts up to four
  times longer or shorter through the flex engine, its file untouched.
- Insert and cut time, ripple edit: Insert Time (⌃⌘Z) and Cut Time (⌃⌘X) work on the marquee or the locators across every
  track, splitting what crosses them and moving tempo, signature, key and chord tracks, markers, sections, automation,
  score symbols, cycle and punch; the Ripple switch makes Delete and Cut close the gap on every track.
- Pre-roll and count-in length: the control bar's menu sets the count-in in bars or beats and a pre-roll that starts
  recording from a stop that far before the playhead, the take starting at the playhead.

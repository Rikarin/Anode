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

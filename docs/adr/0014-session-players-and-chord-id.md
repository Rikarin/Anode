# ADR-0014: Session Players as a rule-based generator over own content; Chord ID by own analysis

- **Status:** accepted
- **Date:** 2026-11-03

## Context

Phase 11 adds Logic's Drummer and Session Players (Bass Player, Keyboard Player), the chord track and Chord ID. The
plan (§11) decided a rule-based pattern generator first, over an own-authored style library of MIDI phrase banks, a
variation grammar and humanisation, conditioned on the chord track and the section markers, with a small ONNX sequence
model as a later option, and own sample content for kits. Chord ID was left between chroma features with an HMM or
CRF, and a small ONNX model. Apple's Drummer and Session Player libraries, their drummer names and presets are not
ours to use (§1, ADR-0008). The Stem Splitter's model weights are research-only (#24), which is a reason not to add a
second set of weights with an unclear licence.

## Decision

- **Generation is a pure function on the server.** A Session Player region is its parameters (player, style,
  complexity, intensity, fills, swing, feel, the player's own switches, a seed) and its length; the generator
  (`Anode.Generative`) turns them, the chord track, the section markers and an optional rhythm source into MIDI
  events. Same inputs, same notes: the seed is stored, regenerate picks a new one. The server's compiler sends the
  generated events to the host as an ordinary MIDI region, cached by a hash of its inputs, so the engine and the
  real-time path are unchanged. Convert to MIDI (or to a pattern for the Drummer) freezes the current output into an
  ordinary region.
- **The generator is rules over a phrase bank.** A style is data: phrase banks per player (bars of one- to four-bar
  phrases tagged by complexity, intensity, role: groove, fill, intro, ending, pickup), a variation grammar (which
  phrases follow which, per section kind; substitutions such as ghost notes, open hats, approach notes, passing chords,
  inversions, octave doubling), and a feel (swing, push or lay back, velocity curves). Complexity and intensity pick
  and morph phrases (the XY pad is a point in that space); fills come at section ends and on request; bass and keys
  map the phrase's chord-relative degrees onto the chord track (root, slash bass, chord tones, scale tones for passing
  notes), voice-led within the instrument's range. Humanisation is seeded timing and velocity variation per player.
- **The content is ours and generated or authored.** Styles are authored in the repository as data (JSON phrase banks
  written for Anode, not transcribed from any product). Drum kits are made by Anode's content builder from Anode's own
  instruments (Drum Synth, the physical-modelling instrument and one-shots rendered from them), as Anode Essentials
  is. Players and styles have generic, descriptive names (a genre and a feel), never a person's name or a Logic
  preset's name. Sampled content from outside (CC0 only) needs a row in ADR-0008 and the user's go-ahead to fetch it.
- **Chord ID is Anode's own analysis; no model weights.** From MIDI: notes weighted by duration and velocity per
  beat, matched against chord templates (major, minor, 7, maj7, m7, dim, m7b5, aug, sus2, sus4, 6, m6, 9 and slash
  bass from the lowest note), smoothed by a Viterbi pass over beats with a change penalty and the key. From audio: a
  harmonic chromagram (constant-Q style bins from Anode's FFT, tuning estimated, percussive energy suppressed by
  median filtering), beat-synchronous from Phase 5's beat tracker, the same templates and HMM. Deterministic and pure
  C#, in `Anode.Dsp.Analysis` beside the Phase 5 analyses. An ONNX chord model stays a later option.
- **The chord track is a global track** of chords (root, quality, bass) with a key and scale per region of the song;
  Session Players and Pitch Source follow it; Chord ID writes it.

## Consequences

- Session Player output can be tested exactly: chord tones, ranges, densities and fills are properties of a seed.
- The players sound as good as their phrase banks; growing the style library is content work after Phase 11.
- Nothing in Phase 11 adds third-party content or weights; ADR-0008 gets rows only for Anode's own content.
- An ONNX sequence model or chord model would need its own licence check and ADR.

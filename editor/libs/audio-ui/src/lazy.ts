/**
 * Parts only lazy-loaded editors and windows use (Phase 7's Event List, Step Editor, Step Input Keyboard and MIDI
 * Transform; Phase 10's panners and multichannel meter, which the Mixer and the settings windows load, and the remote's touch
 * keyboard and drum pads; Phase 11's Environment nodes, on `@xui/node-graph`). They are not in the main barrel: esbuild puts every file the eagerly loaded app reaches into the initial
 * bundle, even when only a lazy chunk uses it.
 */
export * from './lib/chord-lane';
export * from './lib/drum-pads';
export * from './lib/environment-node';
export * from './lib/eq-graph';
export * from './lib/eq-response';
export * from './lib/event-row';
export * from './lib/multichannel-meter';
export * from './lib/object-panner';
export * from './lib/panner-math';
export * from './lib/part-box';
export * from './lib/scales';
export * from './lib/score-toolbar';
export * from './lib/session-pad';
export * from './lib/session-pad-math';
export * from './lib/spectrum-graph';
export * from './lib/step-keyboard';
export * from './lib/step-lane';
export * from './lib/surround-panner';
export * from './lib/touch-keyboard';
export * from './lib/transform-grid';

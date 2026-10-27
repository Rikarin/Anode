import type { components } from '../generated/schema';

type Schemas = components['schemas'];

export type SystemInfo = Schemas['SystemInfo'];
export type EngineStatus = Schemas['EngineStatus'];
export type ProblemDetails = Schemas['ProblemDetails'];

export type Project = Schemas['Project'];
export type ProjectSummary = Schemas['ProjectSummary'];
export type ProjectSettings = Schemas['ProjectSettings'];
export type TimeSignature = Schemas['TimeSignature'];
export type CycleRange = Schemas['CycleRange'];
export type MetronomeSettings = Schemas['MetronomeSettings'];
export type Track = Schemas['Track'];
export type TrackInput = Schemas['TrackInput'];
export type ChannelStrip = Schemas['ChannelStrip'];
export type AudioRegion = Schemas['AudioRegion'];
export type AudioFile = Schemas['AudioFile'];

export type EditCommand = Schemas['EditCommand'];
export type CommandBatch = Schemas['CommandBatch'];
export type CommandResult = Schemas['CommandResult'];
export type UndoHistory = Schemas['UndoHistory'];
export type ProjectDelta = Schemas['ProjectDelta'];
export type DeltaOp = Schemas['DeltaOp'];

export type MidiRegion = Schemas['MidiRegion'];
export type RegionPlayback = Schemas['RegionPlayback'];
export type Note = Schemas['Note'];
export type ControlEvent = Schemas['ControlEvent'];
export type ControlKind = Schemas['ControlKind'];
export type NewNote = Schemas['NewNote'];
export type NoteChange = Schemas['NoteChange'];
export type MidiInputSettings = Schemas['MidiInputSettings'];
export type MidiOutputSettings = Schemas['MidiOutputSettings'];
export type MidiPort = Schemas['MidiPort'];
export type MidiDevices = Schemas['MidiDevices'];
export type LiveMidiEvent = Schemas['LiveMidiEvent'];
export type InstrumentSlot = Schemas['InstrumentSlot'];
export type PluginInfo = Schemas['PluginInfo'];
export type ParameterInfo = Schemas['ParameterInfo'];
export type TrackKind = Schemas['TrackKind'];

export type TransportState = Schemas['TransportState'];
export type TransportStatus = Schemas['TransportStatus'];
export type TransportRequest = Schemas['TransportRequest'];

/** Peak levels of one track (or the master) since the previous meters frame, linear. */
export interface TrackMeter {
  trackId: string;
  peakLeft: number;
  peakRight: number;
}

/** Live MIDI the engine received; `trackId` is the track that played it, if any. */
export interface MidiActivity {
  trackId: string | null;
  device: string | null;
  status: number;
  data1: number;
  data2: number;
}

/**
 * What the Server sends on `/ws` (engine/src/Anode.Contracts/Realtime.cs; not in the OpenAPI document). The first
 * message after connecting is `connected`; `engine` reports a change of the link to the engine host; `resync` replaces
 * events a client fell too far behind to receive, so it refetches what it shows. `projectChanged` carries the delta of
 * every edit, undo, redo or finished recording; `transport` and `meters` stream while the engine runs; `midiActivity`
 * streams live MIDI; `pluginsChanged` and `midiDevicesChanged` ask for a refetch.
 */
export type ServerMessage =
  | { type: 'connected'; version: string }
  | { type: 'engine'; engine: EngineStatus }
  | { type: 'resync' }
  | { type: 'projectOpened'; project: ProjectSummary; revision: number }
  | { type: 'projectClosed' }
  | { type: 'projectSaved'; project: ProjectSummary; revision: number }
  | { type: 'projectChanged'; delta: ProjectDelta }
  | { type: 'transport'; transport: TransportStatus }
  | { type: 'meters'; tracks: TrackMeter[]; master: TrackMeter }
  | { type: 'midiActivity'; events: MidiActivity[] }
  | { type: 'pluginsChanged' }
  | { type: 'midiDevicesChanged' };

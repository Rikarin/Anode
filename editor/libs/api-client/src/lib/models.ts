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

export type TempoEvent = Schemas['TempoEvent'];
export type TempoCurve = Schemas['TempoCurve'];
export type SignatureEvent = Schemas['SignatureEvent'];
export type KeyEvent = Schemas['KeyEvent'];
export type KeyMode = Schemas['KeyMode'];
export type Marker = Schemas['Marker'];
export type ArrangementMarker = Schemas['ArrangementMarker'];
export type RecordingSettings = Schemas['RecordingSettings'];
export type CycleRecordMode = Schemas['CycleRecordMode'];
export type TakeFolder = Schemas['TakeFolder'];
export type Take = Schemas['Take'];
export type CompSegment = Schemas['CompSegment'];
export type Screenset = Schemas['Screenset'];
export type ProjectAlternative = Schemas['ProjectAlternative'];
export type ProjectBackup = Schemas['ProjectBackup'];
export type KeyCommandSet = Schemas['KeyCommandSet'];
export type Bookmarks = Schemas['Bookmarks'];
export type FileEntry = Schemas['FileEntry'];
export type FileKind = Schemas['FileKind'];
export type DirectoryListing = Schemas['DirectoryListing'];

export type Insert = Schemas['Insert'];
export type Send = Schemas['Send'];
export type MixerGroup = Schemas['MixerGroup'];
export type GroupProperties = Schemas['GroupProperties'];
export type AutomationMode = Schemas['AutomationMode'];
export type AutomationPoint = Schemas['AutomationPoint'];
export type AutomationLane = Schemas['AutomationLane'];
export type MixerSettings = Schemas['MixerSettings'];
export type PdcMode = Schemas['PdcMode'];
export type IoLabels = Schemas['IoLabels'];
export type IoLabel = Schemas['IoLabel'];
export type PatchInfo = Schemas['PatchInfo'];
export type SavePatchRequest = Schemas['SavePatchRequest'];
export type PluginEditorRequest = Schemas['PluginEditorRequest'];
export type Job = Schemas['Job'];
export type JobKind = Schemas['JobKind'];
export type JobState = Schemas['JobState'];
export type BounceRequest = Schemas['BounceRequest'];
export type BounceSource = Schemas['BounceSource'];
export type AudioFormat = Schemas['AudioFormat'];
export type DitherMode = Schemas['DitherMode'];
export type NormalizeMode = Schemas['NormalizeMode'];
export type PluginFormat = Schemas['PluginFormat'];

export type FlexMode = Schemas['FlexMode'];
export type FlexFollow = Schemas['FlexFollow'];
export type FlexMarker = Schemas['FlexMarker'];
export type PitchNote = Schemas['PitchNote'];
export type FileTempo = Schemas['FileTempo'];
export type ProjectTempoMode = Schemas['ProjectTempoMode'];
export type SmartTempoSettings = Schemas['SmartTempoSettings'];
export type VarispeedMode = Schemas['VarispeedMode'];
export type VarispeedSettings = Schemas['VarispeedSettings'];
export type TrackAlternative = Schemas['TrackAlternative'];
export type BeatMapping = Schemas['BeatMapping'];
export type PitchSegment = Schemas['PitchSegment'];
export type FileAnalysis = Schemas['FileAnalysis'];
export type BounceInPlaceRequest = Schemas['BounceInPlaceRequest'];
export type BounceInPlaceDestination = Schemas['BounceInPlaceDestination'];
export type BounceSourceAction = Schemas['BounceSourceAction'];
export type ProcessRequest = Schemas['ProcessRequest'];
export type ProcessEffect = Schemas['ProcessEffect'];

export type PatternRegion = Schemas['PatternRegion'];
export type Pattern = Schemas['Pattern'];
export type PatternRow = Schemas['PatternRow'];
export type PatternRowKind = Schemas['PatternRowKind'];
export type PatternPlayMode = Schemas['PatternPlayMode'];
export type PatternStep = Schemas['PatternStep'];
export type DrumMachine = Schemas['DrumMachine'];
export type DrumPad = Schemas['DrumPad'];
export type InsertChain = Schemas['InsertChain'];
export type SmartControls = Schemas['SmartControls'];
export type SmartControlsLayout = Schemas['SmartControlsLayout'];
export type SmartControl = Schemas['SmartControl'];
export type SmartControlKind = Schemas['SmartControlKind'];
export type SmartMapping = Schemas['SmartMapping'];
export type GraphPoint = Schemas['GraphPoint'];
export type LiveLoopsGrid = Schemas['LiveLoopsGrid'];
export type Scene = Schemas['Scene'];
export type Cell = Schemas['Cell'];
export type CellContent = Schemas['CellContent'];
export type CellKind = Schemas['CellKind'];
export type CellPlayMode = Schemas['CellPlayMode'];
export type LaunchQuantize = Schemas['LaunchQuantize'];
export type LoopInfo = Schemas['LoopInfo'];
export type LoopKind = Schemas['LoopKind'];
export type LoopScale = Schemas['LoopScale'];
export type LoopPage = Schemas['LoopPage'];
export type LoopFavouriteRequest = Schemas['LoopFavouriteRequest'];
export type Pack = Schemas['Pack'];
export type InstallPackRequest = Schemas['InstallPackRequest'];
export type AddLoopRequest = Schemas['AddLoopRequest'];
export type AddToLoopLibraryRequest = Schemas['AddToLoopLibraryRequest'];
export type LaunchRequest = Schemas['LaunchRequest'];
export type StopRequest = Schemas['StopRequest'];
export type LiveRecordRequest = Schemas['LiveRecordRequest'];

/** A Live Loops cell's state in the launcher (engine/src/Anode.Contracts/LiveLoops.cs; only on `/ws`). */
export type CellState = 'queued' | 'playing' | 'stopping' | 'stopped';

export type TransportState = Schemas['TransportState'];
export type TransportStatus = Schemas['TransportStatus'];
export type TransportRequest = Schemas['TransportRequest'];

/** Peak levels of one track (or the master) since the previous meters frame, linear. */
export interface TrackMeter {
  trackId: string;
  peakLeft: number;
  peakRight: number;
}

/**
 * A reading of an insert effect (`IAudioEffect.Reading`: a compressor's gain reduction in dB, a meter's LUFS), for the
 * generic plugin editor. Not in the committed contracts yet: the editor reads `meters.readings` when a Server sends it
 * (the fake Server does), and shows no reading otherwise.
 */
export interface InsertReading {
  /** The track, or null for the master strip. */
  trackId: string | null;
  insertId: string;
  value: number;
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
 * streams live MIDI; `pluginsChanged` and `midiDevicesChanged` ask for a refetch; `job` reports job progress (bounce, freeze, analyze, bounce in place, process); `pluginProblem` a
 * plugin the engine had to bypass; `analysis` says a file's analysis changed, so a client refetches it; `cell` reports a
 * Live Loops cell's launcher state at a project sample (for queued and stopping, the boundary it waits for).
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
  | { type: 'meters'; tracks: TrackMeter[]; master: TrackMeter; readings?: InsertReading[] }
  | { type: 'midiActivity'; events: MidiActivity[] }
  | { type: 'pluginsChanged' }
  | { type: 'midiDevicesChanged' }
  | { type: 'job'; job: Job }
  | { type: 'pluginProblem'; trackId: string | null; insertId: string | null; pluginId: string; message: string }
  | { type: 'analysis'; fileId: string }
  | { type: 'cell'; cellId: string; state: CellState; sample: number };

import type { components } from '../generated/schema';

type Schemas = components['schemas'];

export type SystemInfo = Schemas['SystemInfo'];
export type EngineStatus = Schemas['EngineStatus'];
export type EngineAudio = Schemas['EngineAudio'];
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
export type ParameterKind = NonNullable<Schemas['ParameterKind']>;
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

export type ControlEventChange = Schemas['ControlEventChange'];
export type NewControlEvent = Schemas['NewControlEvent'];
export type TransformField = Schemas['TransformField'];
export type TransformConditionOp = Schemas['TransformConditionOp'];
export type TransformCondition = Schemas['TransformCondition'];
export type TransformOperationKind = Schemas['TransformOperationKind'];
export type TransformOperation = Schemas['TransformOperation'];
export type TransformMode = Schemas['TransformMode'];
export type TransformSet = Schemas['TransformSet'];
export type GroovePoint = Schemas['GroovePoint'];
export type GrooveTemplate = Schemas['GrooveTemplate'];
export type Articulation = Schemas['Articulation'];
export type ArticulationSwitchKind = Schemas['ArticulationSwitchKind'];
export type ArticulationSwitch = Schemas['ArticulationSwitch'];
export type ArticulationOutputKind = Schemas['ArticulationOutputKind'];
export type ArticulationOutput = Schemas['ArticulationOutput'];
export type ArticulationSet = Schemas['ArticulationSet'];
export type ScriptPreset = Schemas['ScriptPreset'];
export type LaneKind = Schemas['LaneKind'];
export type Lane = Schemas['Lane'];
export type LaneSet = Schemas['LaneSet'];
export type StepInputState = Schemas['StepInputState'];
export type StepInputRequest = Schemas['StepInputRequest'];
export type AudioEditKind = Schemas['AudioEditKind'];
export type AudioEditOperation = Schemas['AudioEditOperation'];
export type LoopSearchRequest = Schemas['LoopSearchRequest'];
export type LoopSearchResult = Schemas['LoopSearchResult'];
export type SampleRange = Schemas['SampleRange'];
export type PreviewRangeRequest = Schemas['PreviewRangeRequest'];

export type ControlInputKind = Schemas['ControlInputKind'];
export type ControlInput = Schemas['ControlInput'];
export type ControlTargetKind = Schemas['ControlTargetKind'];
export type TrackSwitchAction = Schemas['TrackSwitchAction'];
export type ControlTransportAction = Schemas['ControlTransportAction'];
export type ControlTarget = Schemas['ControlTarget'];
export type ControlValueMode = Schemas['ControlValueMode'];
export type ControlAssignment = Schemas['ControlAssignment'];
export type ControlMode = Schemas['ControlMode'];
export type ControlZone = Schemas['ControlZone'];
export type ControllerAssignments = Schemas['ControllerAssignments'];
export type ControlMessage = Schemas['ControlMessage'];
export type LearnState = Schemas['LearnState'];
export type LearnRequest = Schemas['LearnRequest'];
export type ControlSurfaceKind = Schemas['ControlSurfaceKind'];
export type ControlSurface = Schemas['ControlSurface'];
export type AddSurfaceRequest = Schemas['AddSurfaceRequest'];
export type ReorderSurfacesRequest = Schemas['ReorderSurfacesRequest'];
export type SurfaceStatus = Schemas['SurfaceStatus'];
export type OscClient = Schemas['OscClient'];
export type OscSettings = Schemas['OscSettings'];
export type OscStatus = Schemas['OscStatus'];
export type SelectionRequest = Schemas['SelectionRequest'];
export type SyncSource = Schemas['SyncSource'];
export type MtcFrameRate = Schemas['MtcFrameRate'];
export type MidiSyncDestination = Schemas['MidiSyncDestination'];
export type SyncSettings = Schemas['SyncSettings'];
export type SyncState = Schemas['SyncState'];
export type StemKind = Schemas['StemKind'];
export type StemSplitRequest = Schemas['StemSplitRequest'];

export type Clef = Schemas['Clef'];
export type Notehead = Schemas['Notehead'];
export type StemDirection = Schemas['StemDirection'];
export type Staff = Schemas['Staff'];
export type DrumMapEntry = Schemas['DrumMapEntry'];
export type StaffStyle = Schemas['StaffStyle'];
export type RegionNotation = Schemas['RegionNotation'];
export type ScoreSymbolKind = Schemas['ScoreSymbolKind'];
export type ScorePlacement = Schemas['ScorePlacement'];
export type ScoreSymbol = Schemas['ScoreSymbol'];
export type Syllabic = Schemas['Syllabic'];
export type LyricSyllable = Schemas['LyricSyllable'];
export type PitchStep = Schemas['PitchStep'];
export type PitchName = Schemas['PitchName'];
export type ChordSymbol = Schemas['ChordSymbol'];
export type ScoreSet = Schemas['ScoreSet'];
export type InstrumentName = Schemas['InstrumentName'];
export type ScoreMargins = Schemas['ScoreMargins'];
export type ScoreLayout = Schemas['ScoreLayout'];
export type ProjectScore = Schemas['ProjectScore'];
export type ScoreViewMode = Schemas['ScoreViewMode'];
export type ScoreEventKind = Schemas['ScoreEventKind'];
export type NoteValue = Schemas['NoteValue'];
export type Accidental = Schemas['Accidental'];
export type Barline = Schemas['Barline'];
export type ScoreKey = Schemas['ScoreKey'];
export type ScorePitch = Schemas['ScorePitch'];
export type ScoreTuplet = Schemas['ScoreTuplet'];
export type ScoreDuration = Schemas['ScoreDuration'];
export type ScoreNote = Schemas['ScoreNote'];
export type ScoreEvent = Schemas['ScoreEvent'];
export type ScoreVoice = Schemas['ScoreVoice'];
export type ScoreStaffMeasure = Schemas['ScoreStaffMeasure'];
export type ScorePartMeasure = Schemas['ScorePartMeasure'];
export type ScorePart = Schemas['ScorePart'];
export type ScoreTempoMark = Schemas['ScoreTempoMark'];
export type ScoreRehearsal = Schemas['ScoreRehearsal'];
export type ScoreMeasure = Schemas['ScoreMeasure'];
export type ScoreView = Schemas['ScoreView'];
export type ScoreDocument = Schemas['ScoreDocument'];
export type NewScoreSymbol = Schemas['NewScoreSymbol'];
export type LyricChange = Schemas['LyricChange'];
export type NewChordSymbol = Schemas['NewChordSymbol'];
export type MusicXmlExportRequest = Schemas['MusicXmlExportRequest'];
export type MusicXmlImportRequest = Schemas['MusicXmlImportRequest'];
export type AafExportRequest = Schemas['AafExportRequest'];
export type AafImportRequest = Schemas['AafImportRequest'];
export type MidiExportRequest = Schemas['MidiExportRequest'];
export type MidiImportRequest = Schemas['MidiImportRequest'];
export type ImportedTrack = Schemas['ImportedTrack'];
export type ImportedScore = Schemas['ImportedScore'];
export type ImportedContent = Schemas['ImportedContent'];

/**
 * The factory staff styles' ids (engine/src/Anode.Contracts/Score.cs, `StaffStyleIds`); a track's `staffStyleId` null
 * means `StaffStyleIds.default`.
 */
export const StaffStyleIds = {
  factoryPrefix: 'factory.',
  treble: 'factory.treble',
  bass: 'factory.bass',
  piano: 'factory.piano',
  drums: 'factory.drums',
  guitar: 'factory.guitar',
  bassGuitar: 'factory.bassGuitar',
  clarinetBb: 'factory.clarinetBb',
  trumpetBb: 'factory.trumpetBb',
  hornF: 'factory.hornF',
  altoSax: 'factory.altoSax',
  tenorSax: 'factory.tenorSax',
  viola: 'factory.viola',
  cello: 'factory.cello',
  default: 'factory.piano'
} as const;

/** A new region's score settings (`RegionNotation.Default`): the staff style's grids, Logic's defaults otherwise. */
export const DefaultRegionNotation: RegionNotation = {
  displayQuantizeTicks: 0,
  tripletQuantizeTicks: 0,
  interpretation: true,
  noOverlap: true,
  syncopation: false,
  maxDots: 1
};

/** A new project's score layout (`ScoreLayout.Default`): A4 portrait, 15 mm margins, 7 mm staves, no texts, concert pitch. */
export const DefaultScoreLayout: ScoreLayout = {
  pageWidthMm: 210,
  pageHeightMm: 297,
  margins: { top: 15, bottom: 15, left: 15, right: 15 },
  staffHeightMm: 7,
  title: '',
  subtitle: '',
  composer: '',
  header: '',
  footer: '',
  transposingScore: false,
  instrumentNames: []
};

/** A new project's score content (`ProjectScore.Empty`): nothing stored, the default layout. */
export const EmptyProjectScore: ProjectScore = {
  staffStyles: [],
  symbols: [],
  lyrics: [],
  chords: [],
  sets: [],
  layout: DefaultScoreLayout
};

// ---- Phase 10 (docs/11-phase-10.md): surround, video, remote

export type ChannelFormat = Schemas['ChannelFormat'];
export type PannerMode = Schemas['PannerMode'];
export type SurroundPanner = Schemas['SurroundPanner'];
export type ObjectPanner = Schemas['ObjectPanner'];
export type Panner = Schemas['Panner'];
export type MonitoringSettings = Schemas['MonitoringSettings'];
export type AdmExportRequest = Schemas['AdmExportRequest'];
export type AdmImportRequest = Schemas['AdmImportRequest'];
export type VideoFrameRate = Schemas['VideoFrameRate'];
export type MovieAudio = Schemas['MovieAudio'];
export type Movie = Schemas['Movie'];
export type MarkerKind = Schemas['MarkerKind'];
export type MovieImportRequest = Schemas['MovieImportRequest'];
export type ThumbnailsRequest = Schemas['ThumbnailsRequest'];
export type MovieThumbnails = Schemas['MovieThumbnails'];
export type SceneDetectRequest = Schemas['SceneDetectRequest'];
export type MovieAudioImportRequest = Schemas['MovieAudioImportRequest'];
export type MovieAudioCodec = Schemas['MovieAudioCodec'];
export type MovieAudioExportRequest = Schemas['MovieAudioExportRequest'];
export type DiscoveryInfo = Schemas['DiscoveryInfo'];
export type KeyCommandPad = Schemas['KeyCommandPad'];
export type PadSet = Schemas['PadSet'];
export type RemotePads = Schemas['RemotePads'];
export type ListenInState = Schemas['ListenInState'];

/**
 * A speaker of a layout (engine/src/Anode.Contracts/Spatial.cs, `Speaker`): its ADM label and nominal position in
 * degrees (azimuth positive to the left, elevation positive up); `lfe` marks the LFE channel.
 */
export interface Speaker {
  label: string;
  azimuth: number;
  elevation: number;
  lfe: boolean;
}

/** A channel format's speakers in buffer order (`SpeakerLayout`): display name, BS.2051 system name (or null). */
export interface SpeakerLayout {
  format: ChannelFormat;
  name: string;
  ituName: string | null;
  speakers: readonly Speaker[];
}

function speaker(label: string, azimuth: number, elevation = 0, lfe = false): Speaker {
  return { label, azimuth, elevation, lfe };
}

const L = speaker('M+030', 30);
const R = speaker('M-030', -30);
const C = speaker('M+000', 0);
const Lfe = speaker('LFE1', 45, -30, true);
const Ls = speaker('M+110', 110);
const Rs = speaker('M-110', -110);
const Lss = speaker('M+090', 90);
const Rss = speaker('M-090', -90);
const Lrs = speaker('M+135', 135);
const Rrs = speaker('M-135', -135);
const Lw = speaker('M+060', 60);
const Rw = speaker('M-060', -60);
const Ltf = speaker('U+045', 45, 30);
const Rtf = speaker('U-045', -45, 30);
const Ltm = speaker('U+090', 90, 30);
const Rtm = speaker('U-090', -90, 30);
const Ltr = speaker('U+135', 135, 30);
const Rtr = speaker('U-135', -135, 30);

/**
 * Every channel format's layout (engine/src/Anode.Contracts/Spatial.cs, `SpeakerLayouts`, ITU-R BS.2051 positions),
 * keyed by format; channels in the ADM/SMPTE order (L, R, C, LFE, sides before rears, wides, the upper layer).
 */
export const SpeakerLayouts: Readonly<Record<ChannelFormat, SpeakerLayout>> = {
  mono: { format: 'mono', name: '1.0', ituName: null, speakers: [C] },
  stereo: { format: 'stereo', name: '2.0', ituName: '0+2+0', speakers: [L, R] },
  lcr: { format: 'lcr', name: '3.0', ituName: null, speakers: [L, R, C] },
  quad: {
    format: 'quad',
    name: '4.0',
    ituName: null,
    speakers: [speaker('M+045', 45), speaker('M-045', -45), Lrs, Rrs]
  },
  surround50: { format: 'surround50', name: '5.0', ituName: null, speakers: [L, R, C, Ls, Rs] },
  surround51: { format: 'surround51', name: '5.1', ituName: '0+5+0', speakers: [L, R, C, Lfe, Ls, Rs] },
  surround70: { format: 'surround70', name: '7.0', ituName: null, speakers: [L, R, C, Lss, Rss, Lrs, Rrs] },
  surround71: { format: 'surround71', name: '7.1', ituName: '0+7+0', speakers: [L, R, C, Lfe, Lss, Rss, Lrs, Rrs] },
  surround712: {
    format: 'surround712',
    name: '7.1.2',
    ituName: null,
    speakers: [L, R, C, Lfe, Lss, Rss, Lrs, Rrs, Ltm, Rtm]
  },
  surround714: {
    format: 'surround714',
    name: '7.1.4',
    ituName: '4+7+0',
    speakers: [L, R, C, Lfe, Lss, Rss, Lrs, Rrs, Ltf, Rtf, Ltr, Rtr]
  },
  surround916: {
    format: 'surround916',
    name: '9.1.6',
    ituName: null,
    speakers: [L, R, C, Lfe, Lss, Rss, Lrs, Rrs, Lw, Rw, Ltf, Rtf, Ltm, Rtm, Ltr, Rtr]
  }
};

/** A new strip's Surround Panner (`SurroundPanner.Default`): front centre as a point, no LFE, a stereo input at ±30°. */
export const DefaultSurroundPanner: SurroundPanner = {
  angle: 0,
  diversity: 1,
  elevation: 0,
  centreLevelDb: 0,
  lfeLevelDb: -144,
  spread: 60
};

/** A new strip's 3D Object Panner (`ObjectPanner.Default`): straight ahead, at ear height, on the sphere, a point. */
export const DefaultObjectPanner: ObjectPanner = { azimuth: 0, elevation: 0, distance: 1, size: 0 };

/** A new strip's panner (`Panner.Default`): stereo pan, the other panners at their defaults. */
export const DefaultPanner: Panner = {
  mode: 'stereo',
  surround: DefaultSurroundPanner,
  object: DefaultObjectPanner
};

/** The panners' automation and Smart Control addresses (`PannerTargets`). */
export const PannerTargets = {
  surroundAngle: 'surround:angle',
  surroundDiversity: 'surround:diversity',
  surroundElevation: 'surround:elevation',
  surroundCentre: 'surround:centre',
  surroundLfe: 'surround:lfe',
  surroundSpread: 'surround:spread',
  objectAzimuth: 'object:azimuth',
  objectElevation: 'object:elevation',
  objectDistance: 'object:distance',
  objectSize: 'object:size'
} as const;

/** A new user's monitoring (`MonitoringSettings.Default`): speakers, no down-mix, channel i to output i + 1. */
export const DefaultMonitoringSettings: MonitoringSettings = { binaural: false, downmixTo: null, speakerMap: [] };

/** The DNS-SD advertisement (`Discovery`): service type and TXT keys. */
export const Discovery = {
  serviceType: '_anode._tcp',
  txtVersion: 'version',
  txtProject: 'project',
  txtPairing: 'pairing',
  txtPath: 'path'
} as const;

/**
 * The listen-in WebSocket's frames (`ListenIn`): after a `ListenInFormat` text message, one binary message per Opus
 * frame: a 16-byte little-endian header (uint32 sequence at 0, int64 transport sample at 4 (−1 stopped), uint16 packet
 * length at 12, uint8 flags at 14), then the packet. At most four listeners.
 */
export const ListenIn = {
  maxListeners: 4,
  sampleRate: 48_000,
  channels: 2,
  frameSamples: 960,
  bitrate: 128_000,
  headerBytes: 16,
  flagPlaying: 1,
  flagDiscontinuity: 2
} as const;

/** The first (text) message on the listen-in socket (`ListenInFormat`; not in the OpenAPI document). */
export interface ListenInFormat {
  sampleRate: number;
  channels: number;
  frameSamples: number;
  bitrate: number;
  projectSampleRate: number;
}

/** A listen-in frame's header and packet, as {@link readListenInFrame} reads it. */
export interface ListenInFrame {
  sequence: number;
  transportSample: number;
  flags: number;
  packet: Uint8Array;
}

/** Reads a listen-in binary message (`ListenIn.TryReadHeader`); null when it is too short for its header and packet. */
export function readListenInFrame(message: ArrayBuffer): ListenInFrame | null {
  if (message.byteLength < ListenIn.headerBytes) {
    return null;
  }
  const view = new DataView(message);
  const length = view.getUint16(12, true);
  if (message.byteLength < ListenIn.headerBytes + length) {
    return null;
  }
  return {
    sequence: view.getUint32(0, true),
    transportSample: Number(view.getBigInt64(4, true)),
    flags: view.getUint8(14),
    packet: new Uint8Array(message, ListenIn.headerBytes, length)
  };
}

// ---- Phase 11 (docs/12-phase-11.md): the chord track, Session Players, the Mastering Assistant, the Environment

export type NewChord = Schemas['NewChord'];
export type Chord = Schemas['Chord'];
export type ScaleKind = Schemas['ScaleKind'];
export type ScaleChange = Schemas['ScaleChange'];
export type ChordTrack = Schemas['ChordTrack'];
export type PitchSourceMode = Schemas['PitchSourceMode'];
export type RegionKey = Schemas['RegionKey'];
export type ChordIdWrite = Schemas['ChordIdWrite'];
export type ChordIdRequest = Schemas['ChordIdRequest'];
export type DetectedChord = Schemas['DetectedChord'];
export type DetectedKey = Schemas['DetectedKey'];
export type ChordIdResult = Schemas['ChordIdResult'];
export type SessionPlayerKind = Schemas['SessionPlayerKind'];
export type SectionKind = Schemas['SectionKind'];
export type SessionPlayerPad = Schemas['SessionPlayerPad'];
export type DrumPiece = Schemas['DrumPiece'];
export type DrumPieceSetting = Schemas['DrumPieceSetting'];
export type DrummerSwitches = Schemas['DrummerSwitches'];
export type BassSwitches = Schemas['BassSwitches'];
export type KeysVoicing = Schemas['KeysVoicing'];
export type KeysLeftHand = Schemas['KeysLeftHand'];
export type KeysSwitches = Schemas['KeysSwitches'];
export type SessionPlayerParameters = Schemas['SessionPlayerParameters'];
export type SessionPlayerRegion = Schemas['SessionPlayerRegion'];
export type SessionPlayerPreset = Schemas['SessionPlayerPreset'];
export type SessionPlayerRhythm = Schemas['SessionPlayerRhythm'];
export type SessionPlayerStyle = Schemas['SessionPlayerStyle'];
export type SectionDefaults = Schemas['SectionDefaults'];
export type SessionPlayerCatalog = Schemas['SessionPlayerCatalog'];
export type SessionPlayerNotes = Schemas['SessionPlayerNotes'];
export type RegenerateRequest = Schemas['RegenerateRequest'];
export type SessionPlayerConvertTarget = Schemas['SessionPlayerConvertTarget'];
export type SessionPlayerConvertRequest = Schemas['SessionPlayerConvertRequest'];
export type ConvertedSessionPlayerRegion = Schemas['ConvertedSessionPlayerRegion'];
export type MasteringCharacter = Schemas['MasteringCharacter'];
export type MasteringAnalysis = Schemas['MasteringAnalysis'];
export type MasteringSettings = Schemas['MasteringSettings'];
export type MasteringState = Schemas['MasteringState'];
export type MasteringAnalysisRequest = Schemas['MasteringAnalysisRequest'];
export type EnvironmentObjectKind = Schemas['EnvironmentObjectKind'];
export type EnvironmentLayer = Schemas['EnvironmentLayer'];
export type DelayTap = Schemas['DelayTap'];
export type EnvironmentObject = Schemas['EnvironmentObject'];
export type EnvironmentCable = Schemas['EnvironmentCable'];
export type MidiEnvironment = Schemas['MidiEnvironment'];
export type EnvironmentInputRequest = Schemas['EnvironmentInputRequest'];

/**
 * The chord qualities (engine/src/Anode.Contracts/Chords.cs, `ChordQualities`): as written after a chord's root, in
 * the order of Chord ID's templates and the quality menu, with their intervals above the root.
 */
export const ChordQualities: readonly { quality: string; intervals: readonly number[] }[] = [
  { quality: '', intervals: [0, 4, 7] },
  { quality: 'm', intervals: [0, 3, 7] },
  { quality: '7', intervals: [0, 4, 7, 10] },
  { quality: 'maj7', intervals: [0, 4, 7, 11] },
  { quality: 'm7', intervals: [0, 3, 7, 10] },
  { quality: 'dim', intervals: [0, 3, 6] },
  { quality: 'm7b5', intervals: [0, 3, 6, 10] },
  { quality: 'aug', intervals: [0, 4, 8] },
  { quality: 'sus2', intervals: [0, 2, 7] },
  { quality: 'sus4', intervals: [0, 5, 7] },
  { quality: '6', intervals: [0, 4, 7, 9] },
  { quality: 'm6', intervals: [0, 3, 7, 9] },
  { quality: '9', intervals: [0, 4, 7, 10, 14] }
];

/** A project's chord track before anything is on it (`ChordTrack.Empty`). */
export const EmptyChordTrack: ChordTrack = { chords: [], scales: [] };

const StepClasses: Readonly<Record<PitchName['step'], number>> = { c: 0, d: 2, e: 4, f: 5, g: 7, a: 9, b: 11 };

/** A spelled pitch's class, 0 (C) … 11 (B) (`PitchSources.PitchClass`). */
export function pitchClass(pitch: PitchName): number {
  return (((StepClasses[pitch.step] + pitch.alter) % 12) + 12) % 12;
}

/** The shortest transposition between pitch classes, −6…+5 (`PitchSources.RootInterval`): Pitch Source's Root mode. */
export function rootInterval(from: number, to: number): number {
  const up = (((to - from) % 12) + 12) % 12;
  return up >= 6 ? up - 12 : up;
}

const SectionPrefixes: readonly (readonly [string, SectionKind])[] = [
  ['intro', 'intro'],
  ['verse', 'verse'],
  ['pre-chorus', 'chorus'],
  ['chorus', 'chorus'],
  ['hook', 'chorus'],
  ['bridge', 'bridge'],
  ['middle', 'bridge'],
  ['break', 'bridge'],
  ['outro', 'outro'],
  ['ending', 'outro'],
  ['end', 'outro']
];

/** An arrangement section's kind by its name (`SectionKinds.Of`): what a Session Player region over it defaults to. */
export function sectionKind(name: string | null | undefined): SectionKind {
  const lower = (name ?? '').trim().toLowerCase();
  return SectionPrefixes.find(([prefix]) => lower.startsWith(prefix))?.[1] ?? 'verse';
}

/** The Drummer's notes per kit piece (`DrumKitNotes`, General MIDI percussion), the main one first. */
export const DrumKitNotes: Readonly<Record<DrumPiece, readonly number[]>> = {
  kick: [36],
  snare: [38, 37],
  hiHat: [42, 44, 46],
  toms: [50, 47, 45, 41],
  cymbals: [49, 51, 53],
  percussion: [39, 54, 56, 82]
};

/** The Mastering Assistant insert's plugin id and parameter ids (`MasteringParameters`). */
export const MasteringParameters = {
  pluginId: 'anode.mastering-assistant',
  character: 'character',
  targetLufs: 'targetLufs',
  ceilingDbtp: 'ceilingDbtp',
  eq: 'eq',
  dynamics: 'dynamics',
  width: 'width',
  matchedBypass: 'matchedBypass'
} as const;

/** The analysis' spectrum bands (`MasteringBands`): ISO 266 third-octave centres, 25 Hz … 20 kHz. */
export const MasteringBands: readonly number[] = [
  25, 31.5, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000,
  5000, 6300, 8000, 10000, 12500, 16000, 20000
];

/** A new Mastering Assistant's settings (`MasteringSettings.Default`): Clean, −14 LUFS, −1 dBTP, full strength. */
export const DefaultMasteringSettings: MasteringSettings = {
  character: 'clean',
  targetLufs: -14,
  ceilingDbtp: -1,
  eq: 1,
  dynamics: 1,
  width: 1,
  matchedBypass: false
};

/** A numeric parameter of an Environment object kind (`EnvironmentParameter`). */
export interface EnvironmentParameter {
  id: string;
  name: string;
  min: number;
  max: number;
  default: number;
}

/** An Environment object kind's name, inlets, outlets and parameters (`EnvironmentObjectKindInfo`). */
export interface EnvironmentObjectKindInfo {
  kind: EnvironmentObjectKind;
  name: string;
  inlets: readonly string[];
  outlets: readonly string[];
  parameters: readonly EnvironmentParameter[];
}

const EnvironmentChannel: EnvironmentParameter = { id: 'channel', name: 'Channel', min: 1, max: 16, default: 1 };

function environmentKind(
  kind: EnvironmentObjectKind,
  name: string,
  inlets: readonly string[],
  outlets: readonly string[],
  parameters: readonly EnvironmentParameter[] = []
): EnvironmentObjectKindInfo {
  return { kind, name, inlets, outlets, parameters };
}

/**
 * The Environment's object kinds (engine/src/Anode.Contracts/Environment.cs, `EnvironmentObjectKinds`), keyed by kind:
 * inlets and outlets in index order (cables name them by index), and the numeric parameters (the arpeggiator's and
 * chord memoriser's are their MIDI FX's, from the plugin catalogue).
 */
export const EnvironmentObjectKinds: Readonly<Record<EnvironmentObjectKind, EnvironmentObjectKindInfo>> = {
  physicalInput: environmentKind('physicalInput', 'Physical Input', [], ['Out']),
  sequencerInput: environmentKind('sequencerInput', 'Sequencer Input', ['In'], []),
  keyboard: environmentKind(
    'keyboard',
    'Keyboard',
    [],
    ['Out'],
    [EnvironmentChannel, { id: 'velocity', name: 'Velocity', min: 1, max: 127, default: 100 }]
  ),
  monitor: environmentKind('monitor', 'Monitor', ['In'], ['Out']),
  fader: environmentKind(
    'fader',
    'Fader',
    ['In'],
    ['Out'],
    [
      { id: 'message', name: 'Message', min: 0, max: 3, default: 0 },
      EnvironmentChannel,
      { id: 'number', name: 'Number', min: 0, max: 127, default: 7 },
      { id: 'min', name: 'Minimum', min: 0, max: 127, default: 0 },
      { id: 'max', name: 'Maximum', min: 0, max: 127, default: 127 },
      { id: 'value', name: 'Value', min: 0, max: 127, default: 0 }
    ]
  ),
  transformer: environmentKind('transformer', 'Transformer', ['In'], ['Matched', 'Unmatched']),
  delayLine: environmentKind(
    'delayLine',
    'Delay Line',
    ['In'],
    ['Out'],
    [{ id: 'dry', name: 'Original', min: 0, max: 1, default: 1 }]
  ),
  arpeggiator: environmentKind('arpeggiator', 'Arpeggiator', ['In'], ['Out']),
  chordMemorizer: environmentKind('chordMemorizer', 'Chord Memorizer', ['In'], ['Out']),
  channelSplitter: environmentKind(
    'channelSplitter',
    'Channel Splitter',
    ['In'],
    [...Array.from({ length: 16 }, (_, c) => `Channel ${c + 1}`), 'Other']
  ),
  midiPort: environmentKind('midiPort', 'MIDI Port', ['In'], []),
  trackInput: environmentKind('trackInput', 'Track Input', ['In'], []),
  controlTarget: environmentKind('controlTarget', 'Control Target', ['In'], [])
};

/** A project's Environment before anything is in it (`MidiEnvironment.Empty`): physical input to sequencer input. */
export const EmptyEnvironment: MidiEnvironment = { layers: [], objects: [], cables: [] };

/** An event an Environment monitor saw (`MonitoredEvent`; only on `/ws`): its bytes and project sample (−1 stopped). */
export interface MonitoredEvent {
  status: number;
  data1: number;
  data2: number;
  sample: number;
}

/** The master's Mastering Assistant's live readings (`MasteringEvent`; only on `/ws`). */
export interface MasteringReading {
  insertId: string;
  momentaryLufs: number;
  shortTermLufs: number;
  integratedLufs: number;
  truePeakDbtp: number;
  compressorReductionDb: number;
  limiterReductionDb: number;
}

/** Anode's virtual MIDI ports' fixed ids (engine/src/Anode.Contracts/Control.cs, `VirtualMidiPorts`; macOS only). */
export const VirtualMidiPorts = { inputId: 'anode:in', outputId: 'anode:out' } as const;

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
  /**
   * Phase 10 (docs/11-phase-10.md, multichannel metering): the linear peak of every channel of the strip's output
   * format, in its layout's order (`SpeakerLayouts`), for a strip (or the master) wider than stereo (`TrackMeter.Peaks`
   * in `Realtime.cs`, relayed from the host's channel meters); absent for mono and stereo meters, which use
   * `peakLeft`/`peakRight`.
   */
  peaks?: number[];
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
 * Live Loops cell's launcher state at a project sample (for queued and stopping, the boundary it waits for). Phase 7:
 * `scriptLog` carries a Scripter slot's `Trace` lines (at most 50 a second; `dropped` counts the rest); `parameters`
 * says a MIDI FX slot's run-time parameter list changed (a script compiled); `stepInput` carries the step input state
 * whenever it changes. Phase 8: `controlLearn` carries a MIDI message received while learning and the assignment it
 * made, if any; `sync` the sync state when it changes (at most 10 a second); `surfaces` every control surface's status
 * when one connects, disconnects or banks; `selection` the selected track when hardware or OSC changed it (the editor
 * follows; not sent for its own `PUT /project/selection`); `keyCommand` a key command a controller or OSC asked for
 * that the Server does not run itself (the editor runs it). Phase 10: `listenIn` the listen-in state when it starts or
 * stops, a listener comes or goes, or the Server's delay changes. Phase 11: `environmentMonitor` an Environment
 * monitor's last events and `environmentFader` a fader's value (at the meter rate, from the host's telemetry);
 * `mastering` the master's Mastering Assistant's live loudness and gain reduction while playing.
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
  | { type: 'cell'; cellId: string; state: CellState; sample: number }
  | { type: 'scriptLog'; trackId: string; slotId: string; lines: string[]; dropped: number }
  | { type: 'parameters'; trackId: string; slotId: string; parameters: ParameterInfo[] }
  | { type: 'stepInput'; state: StepInputState }
  | { type: 'controlLearn'; message: ControlMessage; assignmentId?: string | null }
  | { type: 'sync'; state: SyncState }
  | { type: 'surfaces'; surfaces: SurfaceStatus[] }
  | { type: 'selection'; trackId: string | null }
  | { type: 'keyCommand'; id: string }
  | { type: 'listenIn'; state: ListenInState }
  | { type: 'environmentMonitor'; objectId: string; events: MonitoredEvent[] }
  | { type: 'environmentFader'; objectId: string; value: number }
  | ({ type: 'mastering' } & MasteringReading);

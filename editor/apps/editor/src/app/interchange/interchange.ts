import type { AafExportRequest, MidiExportRequest, MidiImportRequest, MusicXmlExportRequest } from '@anode/api-client';

/** The interchange formats (docs/10-phase-9.md). */
export type InterchangeFormat = 'musicxml' | 'aaf' | 'midi';

/** What a score export takes: every MIDI track, a score set, or one track's part. */
export type ScoreSource = { kind: 'all' } | { kind: 'set'; setId: string } | { kind: 'part'; trackId: string };

/** The export dialog's form. */
export interface ExportForm {
  fileName: string;
  /** '' for the project's Exports folder. */
  directory: string;
  /** MusicXML: what to export, and `.mxl`. */
  source: ScoreSource;
  compressed: boolean;
  /** AAF: embedded media, else linked (WAVs beside the file). */
  embedMedia: boolean;
  /** AAF and MIDI: the tracks (all of their kind when every one is chosen). */
  trackIds: readonly string[];
  /** MIDI: type 0 or 1, and one region only. */
  format: 0 | 1;
  regionId: string | null;
}

/** The import dialog's form: the file (a path on the Server's disk, or an upload), and the MIDI options. */
export interface ImportForm {
  path: string;
  file: File | null;
  importTempo: boolean;
  importMarkers: boolean;
  merge: boolean;
}

export const FormatNames: Record<InterchangeFormat, string> = {
  musicxml: 'MusicXML',
  aaf: 'AAF',
  midi: 'MIDI File'
};

/** The extensions an import accepts (the browser's file picker). */
export const ImportAccept: Record<InterchangeFormat, string> = {
  musicxml: '.musicxml,.mxl,.xml',
  aaf: '.aaf',
  midi: '.mid,.midi,.smf'
};

/** The extension an export writes. */
export function exportExtension(format: InterchangeFormat, form: Pick<ExportForm, 'compressed'>): string {
  return format === 'musicxml' ? (form.compressed ? 'mxl' : 'musicxml') : format === 'aaf' ? 'aaf' : 'mid';
}

/** The name the export writes (the extension added when missing, as the Server does). */
export function exportFileName(format: InterchangeFormat, form: Pick<ExportForm, 'fileName' | 'compressed'>): string {
  const extension = exportExtension(format, form);
  const name = form.fileName.trim();
  return name.toLowerCase().endsWith(`.${extension}`) ? name : `${name}.${extension}`;
}

/** What keeps an export from starting. */
export function exportProblems(format: InterchangeFormat, form: ExportForm): string[] {
  const problems: string[] = [];
  if (!form.fileName.trim()) {
    problems.push('Give the file a name.');
  } else if (/[\\/]/.test(form.fileName)) {
    problems.push('The name cannot contain folders; choose the folder under Where.');
  }
  if ((format === 'aaf' || format === 'midi') && form.trackIds.length === 0 && !form.regionId) {
    problems.push(format === 'aaf' ? 'Choose at least one audio track.' : 'Choose at least one track.');
  }
  return problems;
}

/** The tracks to send: null when every track of the kind is chosen (the Server's default). */
function chosenTracks(chosen: readonly string[], all: readonly string[]): string[] | null {
  return all.length > 0 && all.every(id => chosen.includes(id)) ? null : all.filter(id => chosen.includes(id));
}

export function musicXmlRequest(form: ExportForm): MusicXmlExportRequest {
  return {
    fileName: form.fileName.trim(),
    directory: form.directory.trim() || null,
    setId: form.source.kind === 'set' ? form.source.setId : null,
    partTrackId: form.source.kind === 'part' ? form.source.trackId : null,
    compressed: form.compressed
  };
}

export function aafRequest(form: ExportForm, audioTrackIds: readonly string[]): AafExportRequest {
  return {
    fileName: form.fileName.trim(),
    directory: form.directory.trim() || null,
    trackIds: chosenTracks(form.trackIds, audioTrackIds),
    embedMedia: form.embedMedia
  };
}

export function midiRequest(form: ExportForm, midiTrackIds: readonly string[]): MidiExportRequest {
  return {
    fileName: form.fileName.trim(),
    directory: form.directory.trim() || null,
    format: form.format,
    trackIds: form.regionId ? null : chosenTracks(form.trackIds, midiTrackIds),
    regionId: form.regionId
  };
}

export function midiImportRequest(form: ImportForm): MidiImportRequest {
  return { path: form.path, importTempo: form.importTempo, importMarkers: form.importMarkers, merge: form.merge };
}

import { Injectable, inject, signal } from '@angular/core';
import type { AudioFile, CommandResult, EditCommand } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { ConnectionService } from '../core/connection';
import { Notify } from '../core/notify';
import { ProjectStore } from '../state/project.store';
import { plural } from './region-edit';

/** Whether a dropped file is one the Server can import (Phase 1: WAV only). */
export function isWav(file: { name: string; type?: string }): boolean {
  return /\.wave?$/i.test(file.name) || file.type === 'audio/wav' || file.type === 'audio/x-wav';
}

/** Whether a dropped file is a Standard MIDI File. */
export function isMidiFile(file: { name: string; type?: string }): boolean {
  return /\.(mid|midi|smf)$/i.test(file.name) || file.type === 'audio/midi' || file.type === 'audio/x-midi';
}

/** The id of the track a batch added, from its delta. */
export function addedTrackId(result: CommandResult | null, before: ReadonlySet<string>): string | null {
  for (const op of result?.delta.ops ?? []) {
    if (op.op === 'trackUpserted' && !before.has(op.track.id)) {
      return op.track.id;
    }
  }
  return null;
}

/**
 * Importing audio by dropping files on the Tracks area. In the desktop app the Server reads the files where they are
 * (their paths come from the preload: `ImportAudioFile`); in a browser they are uploaded (`UploadAudioFile`). Then one
 * batch places them on the track, one after the other from the drop point; a drop below the last track makes a new
 * track first.
 */
@Injectable({ providedIn: 'root' })
export class AudioImport {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly notify = inject(Notify);
  private readonly desktop = inject(ConnectionService).desktop;
  /** Files being imported. */
  readonly importing = signal(0);

  /** Imports dropped files onto `trackId` (a new track when null) from `startTick`. */
  async importFiles(files: readonly File[], trackId: string | null, startTick: number): Promise<void> {
    const midi = files.filter(isMidiFile);
    for (const file of midi) {
      const path = this.desktop?.pathForFile?.(file);
      if (path) {
        await this.importServerFile(path, 'midi', null, 0);
      } else {
        this.notify.error(`${file.name}: a browser cannot send MIDI files yet; import it from the All Files browser.`);
      }
    }
    const wavs = files.filter(isWav);
    if (wavs.length + midi.length < files.length) {
      this.notify.error('Anode imports WAV and MIDI files only for now.');
    }

    await this.place(
      wavs.map(file => () => {
        const path = this.desktop?.pathForFile?.(file);
        return path ? this.api.importAudioFile(path) : this.api.uploadAudioFile(file);
      }),
      trackId,
      startTick
    );
  }

  /**
   * Imports a file from the Server's disk (the All Files browser): audio onto `trackId` at `startTick`, a Standard
   * MIDI File as new instrument tracks with its tempo and signatures.
   */
  async importServerFile(
    path: string,
    kind: 'audio' | 'midi',
    trackId: string | null,
    startTick: number
  ): Promise<void> {
    if (kind === 'midi') {
      this.importing.update(n => n + 1);
      try {
        await this.project.send(() => this.api.importMidiFile(path), 'Could not import the MIDI file');
      } finally {
        this.importing.update(n => n - 1);
      }
      return;
    }
    await this.importPaths([path], trackId, startTick);
  }

  /** Places a file of the project (the Project Audio browser) on a track at `startTick`, or on a new track. */
  async placeProjectFile(fileId: string, trackId: string | null, startTick: number): Promise<void> {
    const file = this.project.files().get(fileId);
    if (file) {
      await this.place([() => Promise.resolve(file)], trackId, startTick);
    }
  }

  /** Imports files by path (the desktop app's Import dialog). */
  async importPaths(paths: readonly string[], trackId: string | null, startTick: number): Promise<void> {
    await this.place(
      paths.map(path => () => this.api.importAudioFile(path)),
      trackId,
      startTick
    );
  }

  private async place(
    imports: readonly (() => Promise<AudioFile>)[],
    trackId: string | null,
    startTick: number
  ): Promise<void> {
    if (imports.length === 0) {
      return;
    }

    this.importing.update(n => n + imports.length);
    const files: AudioFile[] = [];
    try {
      for (const run of imports) {
        const file = await this.notify.run(run, 'Could not import');
        if (file) {
          this.project.upsertFile(file);
          files.push(file);
        }
      }
    } finally {
      this.importing.update(n => n - imports.length);
    }

    const [first] = files;
    if (!first) {
      return;
    }

    let target = trackId;
    if (target === null) {
      const before = new Set(this.project.trackIds());
      const added = await this.project.execute(
        [{ type: 'addTrack', name: first.name.replace(/\.[^.]+$/, ''), stereo: first.channels > 1 }],
        'New Track'
      );
      target = addedTrackId(added, before);
      if (target === null) {
        return;
      }
    }

    const perTick = this.project.framesPerTick();
    const commands: EditCommand[] = [];
    let at = Math.max(0, Math.round(startTick));
    for (const file of files) {
      commands.push({ type: 'addRegion', trackId: target, fileId: file.id, startTick: at });
      at += Math.round(file.frames / perTick);
    }

    await this.project.execute(commands, plural('Add', commands.length));
  }
}

import { Injectable, computed, inject, signal } from '@angular/core';
import type { CommandResult, PatchInfo } from '@anode/api-client';
import { ANODE_API } from '../core/api';
import { noteName } from '../midi/midi-math';
import { PatchesStore } from '../state/patches.store';
import { PluginsStore } from '../state/plugins.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { UiStore } from '../state/ui.store';
import { DrumSynthId, auditionEvents, drumMachineOf, kitPatches, padOfTrack } from './drum-machine';

/** How long a click's audition holds its note. */
export const AuditionMs = 250;

/**
 * The Drum Machine Designer's edits (docs/07-phase-6.md): creating one (empty, from a kit, or converting an instrument
 * track in place), the selected pad per DMD, auditioning pads (live MIDI to the DMD, which the host routes to the pad
 * by note), and the pad commands (add, rename, instrument, move to another note, remove), each one undo step.
 */
@Injectable({ providedIn: 'root' })
export class DrumMachineStore {
  private readonly api = inject(ANODE_API);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly ui = inject(UiStore);
  private readonly plugins = inject(PluginsStore);
  private readonly patches = inject(PatchesStore);

  /** The installed kits (patches of kind `stack`), by name. */
  readonly kits = computed(() => kitPatches(this.patches.patches()));
  /** The DMD of the selected track (the DMD itself or one of its pads), or null. */
  readonly current = computed(() => {
    const id = this.selection.track();
    return drumMachineOf(id ? this.project.track(id) : null, this.project.tracks());
  });
  /** The selected pad's note per DMD. */
  private readonly selectedNotes = signal<ReadonlyMap<string, number>>(new Map());

  /** The selected pad's note on a DMD: the one picked, else the pad of a selected pad track, else its first pad. */
  selectedNote(dmdId: string): number | null {
    const dmd = this.project.track(dmdId);
    const pads = dmd?.drumMachine?.pads ?? [];
    const picked = this.selectedNotes().get(dmdId);
    if (picked !== undefined) {
      return picked;
    }
    const selected = this.selection.track();
    const own = dmd && selected ? padOfTrack(dmd, selected) : null;
    return own?.note ?? pads[0]?.note ?? null;
  }

  selectPad(dmdId: string, note: number): void {
    this.selectedNotes.update(map => new Map(map).set(dmdId, note));
  }

  /**
   * A new Drum Machine Designer from a kit (or empty), after the selected track; or, with `trackId`, that instrument
   * track turned into one in place. Selects it and shows its editor.
   */
  async create(kit: PatchInfo | null, trackId?: string): Promise<CommandResult | null> {
    const before = new Set(this.project.trackIds());
    const result = await this.project.execute(
      [{ type: 'createDrumMachine', trackId: trackId ?? null, kitPatchId: kit?.id ?? null, name: kit?.name ?? null }],
      kit ? `Drum Machine Designer: ${kit.name}` : 'New Drum Machine Designer'
    );
    if (result) {
      const added = result.delta.ops.find(
        op => op.op === 'trackUpserted' && !before.has(op.track.id) && op.track.drumMachine
      );
      const id = trackId ?? (added?.op === 'trackUpserted' ? added.track.id : null);
      if (id) {
        this.selection.track.set(id);
        this.ui.editorView.set('drumMachine');
      }
    }
    return result;
  }

  /** Plays a pad as a click would: note on now, off a moment later. */
  async audition(dmdId: string, note: number): Promise<void> {
    const { on, off } = auditionEvents(note);
    try {
      await this.api.sendLiveMidi(dmdId, [on]);
      setTimeout(() => void this.api.sendLiveMidi(dmdId, [off]).catch(() => undefined), AuditionMs);
    } catch {
      // Auditioning is best effort: a Server without an engine plays nothing.
    }
  }

  async addPad(dmdId: string, note: number, pluginId = DrumSynthId): Promise<CommandResult | null> {
    const result = await this.project.execute(
      [{ type: 'addDrumPad', trackId: dmdId, note, name: null, pluginId }],
      `Add Pad ${noteName(note)}`
    );
    if (result) {
      this.selectPad(dmdId, note);
    }
    return result;
  }

  rename(dmdId: string, note: number, name: string): Promise<CommandResult | null> {
    const trimmed = name.trim();
    const pad = this.pad(dmdId, note);
    if (!trimmed || !pad || pad.name === trimmed) {
      return Promise.resolve(null);
    }
    return this.project.execute([{ type: 'setDrumPad', trackId: dmdId, note, name: trimmed }], 'Rename Pad');
  }

  setInstrument(dmdId: string, note: number, pluginId: string): Promise<CommandResult | null> {
    const pad = this.pad(dmdId, note);
    const track = pad ? this.project.track(pad.trackId) : undefined;
    if (!pad || track?.instrument?.pluginId === pluginId) {
      return Promise.resolve(null);
    }
    const name = this.plugins.plugin(pluginId)?.name ?? pluginId;
    return this.project.execute([{ type: 'setDrumPad', trackId: dmdId, note, pluginId }], `Pad Instrument ${name}`);
  }

  /** Moves a pad to another note (refused by the Server when a pad is there); the selection follows it. */
  async moveTo(dmdId: string, note: number, toNote: number): Promise<CommandResult | null> {
    if (toNote === note) {
      return null;
    }
    const result = await this.project.execute(
      [{ type: 'setDrumPad', trackId: dmdId, note, toNote }],
      `Move Pad to ${noteName(toNote)}`
    );
    if (result) {
      this.selectPad(dmdId, toNote);
    }
    return result;
  }

  remove(dmdId: string, note: number): Promise<CommandResult | null> {
    return this.project.execute([{ type: 'removeDrumPad', trackId: dmdId, note }], 'Remove Pad');
  }

  private pad(dmdId: string, note: number) {
    return this.project.track(dmdId)?.drumMachine?.pads.find(p => p.note === note) ?? null;
  }
}

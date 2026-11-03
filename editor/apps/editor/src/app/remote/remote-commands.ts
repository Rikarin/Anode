import { DestroyRef, Injectable, inject } from '@angular/core';
import type { AutomationMode } from '@anode/api-client';
import { AutomationStore } from '../automation/automation.store';
import { KeyCommands } from '../keys/key-commands';
import { HistoryStore } from '../state/history.store';
import { KeyCommandsStore } from '../state/key-commands.store';
import { ProjectSession } from '../state/project-session';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { TransportStore } from '../state/transport.store';

/** Why a pad cannot run a command on the remote. */
export const DesktopOnly =
  'Runs in the desktop editor only: the remote has no Tracks area or editors, and the Server has no way yet to ask the desktop to run a key command.';

/**
 * The key commands the remote runs itself (docs/11-phase-10.md, "key-command pads"): the transport, undo and redo,
 * save, a marker at the playhead, the selected tracks' automation mode, and the Live Loops grid. They are registered
 * in the remote's own {@link KeyCommandsStore} while the remote is open (the desktop shell's registry does not exist
 * here; the remote page provides it, so they go when it does); the rest of Logic's commands act on the desktop's views
 * and show as unavailable.
 */
@Injectable()
export class RemoteCommands {
  private readonly keys = inject(KeyCommandsStore);
  private readonly transport = inject(TransportStore);
  private readonly history = inject(HistoryStore);
  private readonly session = inject(ProjectSession);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  private readonly automation = inject(AutomationStore);

  private readonly actions: Readonly<Record<string, () => void>> = {
    'transport.play': () => this.transport.togglePlay(),
    'transport.record': () => this.transport.record(),
    'transport.cycle': () => this.transport.toggleCycle(),
    'transport.goToStart': () => this.transport.goToStart(),
    'transport.flashback': () => void this.transport.flashbackCapture(),
    'transport.autopunch': () => this.transport.toggleAutopunch(),
    'transport.replace': () => this.transport.toggleReplace(),
    'transport.metronome': () => this.transport.toggleMetronome(),
    'edit.undo': () => void this.history.undo(),
    'edit.redo': () => void this.history.redo(),
    'project.save': () => void this.session.save(),
    'track.marker': () => void this.addMarker(),
    'automation.read': () => this.setMode('read'),
    'automation.touch': () => this.setMode('touch'),
    'automation.latch': () => this.setMode('latch'),
    'automation.write': () => this.setMode('write'),
    'automation.off': () => this.setMode('off'),
    'view.liveLoops': () =>
      void this.project.execute(
        [{ type: 'setLiveLoops', shown: !this.project.liveLoops().shown }],
        this.project.liveLoops().shown ? 'Hide Live Loops' : 'Show Live Loops'
      )
  };

  /** Every key command, with whether the remote runs it, for the pad editor's command list. */
  readonly commands = KeyCommands.map(c => ({ ...c, runnable: c.id in this.actions }));

  constructor() {
    const unregister = Object.entries(this.actions).map(([id, action]) => this.keys.register(id, action));
    inject(DestroyRef).onDestroy(() => unregister.forEach(off => off()));
  }

  runnable(commandId: string): boolean {
    return commandId in this.actions;
  }

  /** Runs a command; false when the remote cannot. */
  run(commandId: string): boolean {
    return this.runnable(commandId) && this.keys.run(commandId);
  }

  label(commandId: string): string {
    return KeyCommands.find(c => c.id === commandId)?.label ?? commandId;
  }

  private async addMarker(): Promise<void> {
    const tick = Math.max(0, Math.round(this.transport.positionAt(performance.now())));
    await this.project.execute([{ type: 'addMarker', name: '', tick, lengthTicks: 0 }], 'Create Marker');
  }

  private setMode(mode: AutomationMode): void {
    for (const id of this.selection.trackSelection()) {
      if (this.project.track(id)) {
        void this.automation.setMode(id, mode);
      }
    }
  }
}

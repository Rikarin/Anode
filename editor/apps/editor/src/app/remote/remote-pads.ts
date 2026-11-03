import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import type { KeyCommandPad } from '@anode/api-client';
import { XuiTooltipImports } from '@xui/tooltip';
import { RemotePadsStore } from './pads.store';
import { DesktopOnly, RemoteCommands } from './remote-commands';
import { Touch } from './remote-transport';

/**
 * The remote's key-command pads (Logic Remote's Key Commands view, docs/11-phase-10.md): the pad sets from
 * `/remote/pads` (user settings), one shown at a time; a tap runs the pad's command. Commands the remote cannot run
 * (they act on the desktop's views) show disabled with why. Edit turns the grid into the set's editor: rename the set,
 * add a pad for any key command, rename, move or remove pads; each change is saved at once.
 */
@Component({
  selector: 'an-remote-pads',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiTooltipImports],
  host: { class: 'flex min-h-0 flex-col gap-2 p-2' },
  template: `
    <div class="flex flex-wrap items-center gap-2">
      <div class="bezel-group text-foreground h-11 text-[12px] font-semibold" role="radiogroup" aria-label="Pad set">
        @for (set of pads.pads().sets; track set.id) {
          <button
            type="button"
            role="radio"
            class="px-3"
            [attr.aria-checked]="pads.set()?.id === set.id"
            [class.text-lit]="pads.set()?.id === set.id"
            (click)="pads.setId.set(set.id)"
          >
            {{ set.name }}
          </button>
        }
      </div>
      <button
        type="button"
        [class]="touch + ' ms-auto px-3 text-[12px] font-semibold'"
        data-testid="pads-edit"
        [attr.aria-pressed]="pads.editing()"
        [class.text-lit]="pads.editing()"
        (click)="pads.editing.set(!pads.editing())"
      >
        {{ pads.editing() ? 'Done' : 'Edit' }}
      </button>
    </div>
    @if (pads.set(); as set) {
      @if (pads.editing()) {
        <div class="flex flex-col gap-2 overflow-y-auto" data-testid="pads-editor">
          <div class="flex flex-wrap items-center gap-2">
            <label class="text-foreground-muted text-[12px]" for="an-pad-set-name">Set</label>
            <input
              #setName
              id="an-pad-set-name"
              class="bg-surface-inset border-hairline text-foreground h-11 min-w-0 flex-1 rounded-md border px-2 text-sm"
              [value]="set.name"
              (change)="pads.renameSet(setName.value)"
            />
            <button type="button" [class]="touch + ' px-3 text-[12px]'" (click)="pads.addSet('New Set')">
              New Set
            </button>
            <button
              type="button"
              [class]="touch + ' px-3 text-[12px]'"
              [attr.disabled]="pads.pads().sets.length <= 1 ? '' : null"
              (click)="pads.removeSet()"
            >
              Delete Set
            </button>
          </div>
          <ul class="flex flex-col gap-1" aria-label="Pads">
            @for (pad of set.pads; track pad.id; let first = $first; let last = $last) {
              <li
                class="bg-surface border-hairline flex items-center gap-2 rounded-md border p-1"
                data-testid="pad-row"
              >
                <input
                  #label
                  class="bg-surface-inset border-hairline text-foreground h-11 min-w-0 flex-1 rounded-md border px-2 text-sm"
                  [attr.aria-label]="'Label of ' + pad.label"
                  [value]="pad.label"
                  (change)="pads.updatePad(pad.id, { label: label.value })"
                />
                <select
                  class="bg-surface-inset border-hairline text-foreground h-11 max-w-48 rounded-md border px-1 text-[12px]"
                  [attr.aria-label]="'Command of ' + pad.label"
                  (change)="pads.updatePad(pad.id, { commandId: value($event) })"
                >
                  @for (command of commands.commands; track command.id) {
                    <option [value]="command.id" [selected]="command.id === pad.commandId">
                      {{ command.label }}{{ command.runnable ? '' : ' (desktop)' }}
                    </option>
                  }
                </select>
                <button
                  type="button"
                  [class]="touch"
                  [attr.aria-label]="'Move ' + pad.label + ' earlier'"
                  [attr.disabled]="first ? '' : null"
                  (click)="pads.movePad(pad.id, -1)"
                >
                  ↑
                </button>
                <button
                  type="button"
                  [class]="touch"
                  [attr.aria-label]="'Move ' + pad.label + ' later'"
                  [attr.disabled]="last ? '' : null"
                  (click)="pads.movePad(pad.id, 1)"
                >
                  ↓
                </button>
                <button
                  type="button"
                  [class]="touch"
                  [attr.aria-label]="'Remove ' + pad.label"
                  (click)="pads.removePad(pad.id)"
                >
                  ✕
                </button>
              </li>
            }
          </ul>
          <div class="flex items-center gap-2">
            <select
              #newCommand
              class="bg-surface-inset border-hairline text-foreground h-11 min-w-0 flex-1 rounded-md border px-1 text-[12px]"
              aria-label="Command for a new pad"
              data-testid="pad-new-command"
            >
              @for (command of commands.commands; track command.id) {
                <option [value]="command.id">{{ command.label }}{{ command.runnable ? '' : ' (desktop)' }}</option>
              }
            </select>
            <button
              type="button"
              [class]="touch + ' px-3 text-[12px] font-semibold'"
              data-testid="pad-add"
              (click)="pads.addPad(newCommand.value, commands.label(newCommand.value))"
            >
              Add Pad
            </button>
          </div>
        </div>
      } @else {
        <div
          class="grid grid-cols-3 gap-2 overflow-y-auto sm:grid-cols-4 lg:grid-cols-6"
          role="group"
          [attr.aria-label]="set.name + ' pads'"
        >
          @for (pad of set.pads; track pad.id) {
            <button
              type="button"
              class="bezel text-foreground relative flex min-h-20 flex-col items-start justify-end overflow-hidden rounded-md p-2 text-start text-[13px] font-semibold disabled:opacity-35"
              data-testid="key-pad"
              [attr.data-command]="pad.commandId"
              [attr.disabled]="commands.runnable(pad.commandId) ? null : ''"
              [xuiTooltip]="tooltip(pad)"
              (click)="commands.run(pad.commandId)"
            >
              <span
                class="absolute inset-x-0 top-0 h-1"
                aria-hidden="true"
                [style.background]="pad.color || 'var(--color-primary)'"
              ></span>
              {{ pad.label }}
            </button>
          } @empty {
            <p class="text-foreground-muted col-span-full text-sm">This set has no pads: Edit adds some.</p>
          }
        </div>
      }
    } @else if (pads.loaded()) {
      <p class="text-foreground-muted text-sm">No pad sets: Edit, then New Set.</p>
    }
  `
})
export class RemotePads {
  protected readonly pads = inject(RemotePadsStore);
  protected readonly commands = inject(RemoteCommands);
  protected readonly touch = Touch;

  constructor() {
    if (!this.pads.loaded()) {
      void this.pads.load();
    }
  }

  protected tooltip(pad: KeyCommandPad): string {
    const label = this.commands.label(pad.commandId);
    return this.commands.runnable(pad.commandId) ? label : `${label}: ${DesktopOnly}`;
  }

  protected value(event: Event): string {
    return (event.target as HTMLSelectElement).value;
  }
}

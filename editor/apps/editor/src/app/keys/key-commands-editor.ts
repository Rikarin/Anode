import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiInputImports } from '@xui/input';
import { XuiKbdImports } from '@xui/kbd';
import { KeyCommandsStore } from '../state/key-commands.store';
import { UiStore } from '../state/ui.store';
import { bindingOf, conflicts, formatBinding, type KeyCommand, type KeyCommandGroup } from './key-commands';

/**
 * The Key Commands editor (Logic's ⌥K): every command of the registry by group, searchable by name or key, with its
 * binding. "Learn" takes the next key press as the command's binding (taking it from a command that had it, which the
 * row says); Clear unbinds; Reset returns to the defaults. Saved per user through `/settings/key-commands`.
 */
@Component({
  selector: 'an-key-commands-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiInputImports, XuiKbdImports],
  template: `
    <xui-dialog [(open)]="ui.keyCommandsOpen" title="Key Commands" size="lg">
      <xui-dialog-body class="flex min-h-0 flex-col gap-3">
        <input
          #search
          xuiInput
          type="search"
          placeholder="Search commands or keys"
          aria-label="Search key commands"
          data-testid="key-search"
          [value]="query()"
          (input)="query.set(search.value)"
        />
        <div
          class="border-hairline bg-surface-inset max-h-[55vh] min-h-0 overflow-y-auto rounded-md border"
          role="table"
          aria-label="Key commands"
          (keydown)="learnKey($event)"
        >
          @for (group of groups(); track group.name) {
            <div role="rowgroup">
              <div
                class="border-hairline bg-surface-raised text-foreground sticky top-0 border-b px-3 py-1 text-[11px] font-bold"
                role="row"
              >
                <span role="columnheader">{{ group.name }}</span>
              </div>
              @for (command of group.commands; track command.id) {
                <div
                  class="border-hairline flex items-center gap-3 border-b px-3 py-1 text-xs last:border-b-0"
                  role="row"
                  [attr.data-testid]="'key-' + command.id"
                  [class.bg-muted]="learning() === command.id"
                >
                  <span class="text-foreground min-w-0 flex-1 truncate" role="cell">{{ command.label }}</span>
                  <span class="flex w-36 shrink-0 justify-end gap-1" role="cell">
                    @if (learning() === command.id) {
                      <span class="text-lit text-[11px] font-semibold">Press a key…</span>
                    } @else {
                      @for (binding of keys.bindings().get(command.id) ?? []; track binding) {
                        <kbd xuiKbd size="sm">{{ format(binding) }}</kbd>
                      } @empty {
                        <span class="text-foreground-muted">—</span>
                      }
                    }
                  </span>
                  <span class="flex shrink-0 gap-1" role="cell">
                    <button
                      xuiButton
                      type="button"
                      size="sm"
                      variant="ghost"
                      [attr.aria-label]="'Learn a key for ' + command.label"
                      (click)="learn(command)"
                    >
                      {{ learning() === command.id ? 'Cancel' : 'Learn' }}
                    </button>
                    <button
                      xuiButton
                      type="button"
                      size="sm"
                      variant="ghost"
                      [attr.aria-label]="'Clear the key of ' + command.label"
                      [disabled]="(keys.bindings().get(command.id) ?? []).length === 0"
                      (click)="keys.rebind(command.id, null)"
                    >
                      Clear
                    </button>
                  </span>
                </div>
              }
            </div>
          } @empty {
            <p class="text-foreground-muted p-3 text-xs">No command matches “{{ query() }}”.</p>
          }
        </div>
        <p class="text-foreground-muted min-h-4 text-xs" aria-live="polite">{{ message() }}</p>
      </xui-dialog-body>
      <xui-dialog-footer>
        <button xuiButton type="button" variant="ghost" (click)="keys.reset()">Reset to Defaults</button>
        <button xuiButton type="button" (click)="ui.keyCommandsOpen.set(false)">Done</button>
      </xui-dialog-footer>
    </xui-dialog>
  `
})
export class KeyCommandsEditor {
  protected readonly keys = inject(KeyCommandsStore);
  protected readonly ui = inject(UiStore);
  protected readonly query = signal('');
  /** The command waiting for its key. */
  protected readonly learning = signal<string | null>(null);
  protected readonly message = signal('');

  protected readonly groups = computed(() => {
    const query = this.query().trim().toLowerCase();
    const bindings = this.keys.bindings();
    const matches = (command: KeyCommand) =>
      !query ||
      command.label.toLowerCase().includes(query) ||
      command.id.toLowerCase().includes(query) ||
      (bindings.get(command.id) ?? []).some(
        b => b.toLowerCase().includes(query) || formatBinding(b).toLowerCase().includes(query)
      );
    const groups = new Map<KeyCommandGroup, KeyCommand[]>();
    for (const command of this.keys.commands.filter(matches)) {
      groups.set(command.group, [...(groups.get(command.group) ?? []), command]);
    }
    return [...groups].map(([name, commands]) => ({ name, commands }));
  });

  protected format(binding: string): string {
    return formatBinding(binding);
  }

  protected learn(command: KeyCommand): void {
    this.learning.set(this.learning() === command.id ? null : command.id);
    this.message.set(this.learning() ? `Press the key for “${command.label}”, or Escape to cancel.` : '');
  }

  /** While learning, the next key press (with its modifiers) becomes the binding. */
  protected learnKey(event: KeyboardEvent): void {
    const id = this.learning();
    if (!id) {
      return;
    }
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      this.learning.set(null);
      this.message.set('');
      return;
    }
    const binding = bindingOf(event);
    if (!binding) {
      return; // a lone modifier: wait for the key
    }
    event.preventDefault();
    event.stopPropagation();
    const taken = conflicts(this.keys.bindings(), binding, id)
      .map(other => this.keys.commands.find(c => c.id === other)?.label ?? other)
      .join(', ');
    this.learning.set(null);
    void this.keys.rebind(id, binding);
    this.message.set(
      `${formatBinding(binding)} now runs “${this.keys.commands.find(c => c.id === id)?.label}”${taken ? `; it was on ${taken}` : ''}.`
    );
  }
}

import { computed, inject, Injectable, signal } from '@angular/core';
import { ANODE_API } from '../core/api';
import { EventStream } from '../core/events';
import { Notify } from '../core/notify';
import { bindingIndex, effectiveBindings, KeyCommands, type KeyStroke, rebind, resolveKey } from '../keys/key-commands';

/**
 * The key commands in force: the registry's Logic-like defaults with the user's rebindings over them (kept per user by
 * the Server, `/settings/key-commands`), and what each command does. Parts of the editor register the actions they
 * own; the app shell resolves every key press here, so one table drives all shortcuts.
 */
@Injectable({ providedIn: 'root' })
export class KeyCommandsStore {
  private readonly api = inject(ANODE_API);
  private readonly notify = inject(Notify);

  /** The user's bindings by command id (only the commands they changed). */
  readonly user = signal<Readonly<Record<string, readonly string[]>>>({});
  readonly bindings = computed(() => effectiveBindings(this.user()));
  private readonly index = computed(() => bindingIndex(this.bindings()));
  private readonly actions = new Map<string, () => void>();
  readonly commands = KeyCommands;

  constructor() {
    inject(EventStream).connected$.subscribe(() => void this.load());
  }

  async load(): Promise<void> {
    try {
      const set = await this.api.keyCommands();
      this.user.set(set.bindings);
    } catch {
      // A Server without key commands yet: the defaults.
    }
  }

  /** Registers what a command does; returns the unregister. */
  register(id: string, action: () => void): () => void {
    this.actions.set(id, action);
    return () => {
      if (this.actions.get(id) === action) {
        this.actions.delete(id);
      }
    };
  }

  /** The command a key press is bound to, if it has an action. */
  resolve(event: KeyStroke): string | null {
    const id = resolveKey(this.index(), event);
    return id && this.actions.has(id) ? id : null;
  }

  run(id: string): boolean {
    const action = this.actions.get(id);
    action?.();
    return action !== undefined;
  }

  /** Gives `binding` to `id` (null clears it) and saves the user's set. */
  async rebind(id: string, binding: string | null): Promise<void> {
    const next = rebind(this.user(), id, binding);
    const previous = this.user();
    this.user.set(next);
    try {
      await this.api.saveKeyCommands({ bindings: next });
    } catch (error) {
      this.user.set(previous);
      this.notify.failure(error, 'Could not save the key commands');
    }
  }

  /** Back to the defaults. */
  async reset(): Promise<void> {
    const previous = this.user();
    this.user.set({});
    try {
      await this.api.saveKeyCommands({ bindings: {} });
    } catch (error) {
      this.user.set(previous);
      this.notify.failure(error, 'Could not save the key commands');
    }
  }

  /** The first binding of a command, for tooltips and menus. */
  bindingOf(id: string): string | null {
    return this.bindings().get(id)?.[0] ?? null;
  }
}

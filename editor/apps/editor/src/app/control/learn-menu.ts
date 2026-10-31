import { CdkContextMenuTrigger } from '@angular/cdk/menu';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  Directive,
  inject,
  Injectable,
  input,
  signal,
  viewChild
} from '@angular/core';
import type { ControlTarget } from '@anode/api-client';
import { XuiContextMenuImports } from '@xui/context-menu';
import { XuiMenuImports } from '@xui/menu';
import { learnKey } from './assignments';
import { ControlStore } from './control.store';

/** What a "Learn Assignment for …" menu offers: one or more targets, each with its name. */
export interface LearnChoice {
  target: ControlTarget;
  label: string;
}

/**
 * The editor's one "Learn Assignment for …" context menu (docs/09-phase-8.md, workstream C): controls that have no menu
 * of their own (a fader, a pan knob, a Smart Control, a plug-in parameter) open it at the pointer with their target;
 * choosing it opens Controller Assignments in Learn for that target.
 */
@Injectable({ providedIn: 'root' })
export class LearnMenu {
  private readonly control = inject(ControlStore);
  readonly choices = signal<readonly LearnChoice[]>([]);
  private trigger: CdkContextMenuTrigger | null = null;

  /** The app's host registers its trigger. */
  attach(trigger: CdkContextMenuTrigger | null): void {
    this.trigger = trigger;
  }

  /** Opens the menu where a context-menu event happened; returns whether it did (else the browser's own shows). */
  open(event: MouseEvent, choices: readonly LearnChoice[]): boolean {
    if (!this.trigger || choices.length === 0) {
      return false;
    }
    event.preventDefault();
    event.stopPropagation();
    this.choices.set(choices);
    const trigger = this.trigger;
    const show = () => {
      trigger.close();
      trigger.open({ x: event.clientX, y: event.clientY });
    };
    if (event.buttons & 2) {
      // On macOS the context menu event comes on the right button's press; its release then sends an `auxclick` that
      // the menu's overlay, anchored away from the control, takes for a click outside and closes on. Open after it.
      window.addEventListener('mouseup', () => setTimeout(show), { once: true, capture: true });
    } else {
      show();
    }
    return true;
  }

  learn(choice: LearnChoice): void {
    void this.control.learnFor(choice.target);
  }
}

/** The menu's anchor in the app shell (`an-learn-menu-host`): the template every `open` shows. */
@Component({
  selector: 'an-learn-menu-host',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiContextMenuImports, XuiMenuImports],
  host: { class: 'contents' },
  template: `
    <span class="hidden" aria-hidden="true" [xuiContextMenuTriggerFor]="learnMenu"></span>
    <ng-template #learnMenu>
      <xui-menu class="min-w-56" aria-label="Controller assignment" data-testid="learn-menu">
        @for (choice of menu.choices(); track choice.label) {
          <button xuiMenuItem type="button" data-testid="learn-assignment" (click)="menu.learn(choice)">
            Learn Assignment for “{{ choice.label }}”
          </button>
        }
      </xui-menu>
    </ng-template>
  `
})
export class LearnMenuHost {
  protected readonly menu = inject(LearnMenu);
  private readonly trigger = viewChild(CdkContextMenuTrigger);

  constructor() {
    afterNextRender(() => this.menu.attach(this.trigger() ?? null));
    inject(DestroyRef).onDestroy(() => this.menu.attach(null));
  }
}

/**
 * Marks a control as an assignment target (`[anLearnTarget]`, with `learnLabel`): a click on it while learning makes it
 * Learn's target (`data-learn`, read by the Controller Assignments window), and its context menu offers "Learn
 * Assignment for …".
 */
@Directive({
  selector: '[anLearnTarget]',
  host: {
    '[attr.data-learn]': 'key()',
    '(contextmenu)': 'menu($event)'
  }
})
export class LearnTarget {
  private readonly learnMenu = inject(LearnMenu);
  readonly anLearnTarget = input<ControlTarget | null>(null);
  readonly learnLabel = input('');
  protected readonly key = computed(() => {
    const target = this.anLearnTarget();
    return target ? learnKey(target) : null;
  });

  protected menu(event: MouseEvent): void {
    const target = this.anLearnTarget();
    if (target) {
      this.learnMenu.open(event, [{ target, label: this.learnLabel() }]);
    }
  }
}

/** Learn targets by what they name, so a template hands a control the same object on every check. */
export class LearnTargets {
  private readonly cache = new Map<string, ControlTarget>();

  /** A parameter (automation address) on a track; null when there is no track (the master's plug-ins). */
  parameter(trackId: string | null | undefined, address: string): ControlTarget | null {
    return trackId ? this.get(`p:${trackId}:${address}`, () => ({ kind: 'parameter', trackId, address })) : null;
  }

  /** A Smart Control of the selected track (`knob1`…). */
  smartControl(address: string): ControlTarget {
    return this.get(`s:${address}`, () => ({ kind: 'smartControl', trackId: null, address }));
  }

  private get(key: string, make: () => ControlTarget): ControlTarget {
    let target = this.cache.get(key);
    if (!target) {
      target = make();
      this.cache.set(key, target);
    }
    return target;
  }
}

import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  signal,
  viewChild
} from '@angular/core';
import type { ControlAssignment, ControlTarget } from '@anode/api-client';
import { provideIcons } from '@ng-icons/core';
import { matAddRound, matDeleteOutlineRound, matExpandMoreRound } from '@ng-icons/material-icons/round';
import { XuiIconImports } from '@xui/icon';
import { XuiInputImports } from '@xui/input';
import { XuiMenuImports } from '@xui/menu';
import { XuiTooltipImports } from '@xui/tooltip';
import { FloatingWindow } from '../shell/floating-window';
import { UiStore } from '../state/ui.store';
import { type AssignmentChange, AssignmentInspector } from './assignment-inspector';
import {
  describeInput,
  describeMessage,
  learnTargetAt,
  sameTarget,
  SmartKnobs,
  TrackSwitches,
  TransportActions
} from './assignments';
import { ControlStore } from './control.store';
import { Band } from './styles';

/** What a rename field edits. */
interface Renaming {
  kind: 'zone' | 'mode';
  id: string;
}

/**
 * Controller Assignments (Logic Pro ▸ Control Surfaces ▸ Controller Assignments…, docs/09-phase-8.md): Learn mode with
 * its banner (what the next moved control is assigned to, and the last message received), the assignments by zone and
 * mode (the active mode of each zone chosen in its band), and the inspector of the selected one. While learning, a
 * fader, knob, Smart Control or plug-in parameter clicked anywhere in the editor becomes the target; the target menu
 * offers the rest (track switches and transport buttons on the selected track).
 */
@Component({
  selector: 'an-controller-assignments-window',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AssignmentInspector,
    FloatingWindow,
    NgTemplateOutlet,
    XuiIconImports,
    XuiInputImports,
    XuiMenuImports,
    XuiTooltipImports
  ],
  providers: [provideIcons({ matAddRound, matDeleteOutlineRound, matExpandMoreRound })],
  template: `
    <an-floating-window
      title="Controller Assignments"
      testId="controller-assignments"
      width="56rem"
      [origin]="{ x: 140, y: 72 }"
      (closed)="close()"
    >
      <div
        class="glass-bar flex h-9 shrink-0 items-center gap-2 px-2"
        role="toolbar"
        aria-label="Controller assignments"
      >
        <button
          type="button"
          class="bezel text-foreground flex h-6.5 items-center gap-1.5 px-2.5 text-[11px] font-semibold"
          data-testid="controller-learn"
          [class.text-lit]="control.learning()"
          [attr.aria-pressed]="control.learning() ? 'true' : 'false'"
          xuiTooltip="Learn: choose a target, then move a control on your controller"
          (click)="control.toggleLearn()"
        >
          <span
            class="size-1.5 rounded-full"
            aria-hidden="true"
            [class.bg-transport-record]="control.learning()"
            [class.bg-foreground-muted]="!control.learning()"
          ></span>
          Learn
        </button>
        <div class="bezel-group text-foreground h-6.5">
          <button
            type="button"
            class="grid w-8 place-items-center"
            aria-label="New Assignment"
            data-testid="assignment-add"
            xuiTooltip="New assignment"
            (click)="control.addAssignment()"
          >
            <ng-icon xui name="matAddRound" size="sm" />
          </button>
          <button
            type="button"
            class="grid w-8 place-items-center disabled:opacity-35"
            aria-label="Delete Assignment"
            data-testid="assignment-delete"
            xuiTooltip="Delete the selected assignment"
            [attr.disabled]="control.selected() ? null : ''"
            (click)="deleteSelected()"
          >
            <ng-icon xui name="matDeleteOutlineRound" size="sm" />
          </button>
        </div>
        <span class="flex-1"></span>
        <button
          type="button"
          class="bezel text-foreground flex h-6.5 items-center gap-1 px-2 text-[11px] font-semibold"
          data-testid="zones-menu"
          [xuiMenuTriggerFor]="zonesMenu"
        >
          Zones and Modes
          <ng-icon xui name="matExpandMoreRound" size="14px" />
        </button>
        <ng-template #zonesMenu>
          <xui-menu class="min-w-52" aria-label="Zones and modes">
            <button xuiMenuItem type="button" data-testid="zone-add" (click)="control.addZone()">New Zone</button>
            @if (currentZone(); as zone) {
              <button xuiMenuItem type="button" data-testid="zone-rename" (click)="rename('zone', zone.id)">
                Rename “{{ zone.name }}”…
              </button>
              <button
                xuiMenuItem
                type="button"
                color="error"
                data-testid="zone-delete"
                (click)="control.deleteZone(zone.id)"
              >
                Delete “{{ zone.name }}”
              </button>
              <xui-menu-divider />
              <button xuiMenuItem type="button" data-testid="mode-add" (click)="control.addMode(zone.id)">
                New Mode in “{{ zone.name }}”
              </button>
              @if (currentMode(); as mode) {
                <button xuiMenuItem type="button" data-testid="mode-rename" (click)="rename('mode', mode.id)">
                  Rename “{{ mode.name }}”…
                </button>
                <button
                  xuiMenuItem
                  type="button"
                  color="error"
                  data-testid="mode-delete"
                  [disabled]="zone.modes.length < 2"
                  (click)="control.deleteMode(mode.id)"
                >
                  Delete “{{ mode.name }}”
                </button>
              }
            }
          </xui-menu>
        </ng-template>
      </div>

      @if (control.learning()) {
        <div
          class="border-primary bg-primary/15 text-foreground flex shrink-0 items-center gap-3 border-b px-3 py-2"
          role="status"
          data-testid="learn-banner"
        >
          <span class="bg-transport-record size-2 shrink-0 rounded-full" aria-hidden="true"></span>
          <div class="flex min-w-0 flex-1 flex-col gap-0.5 text-xs">
            <span data-testid="learn-target">
              @if (control.learnState().target; as target) {
                Move a control to assign it to <strong>{{ control.targetLabel(target) }}</strong
                >.
              } @else {
                Click a fader, knob, Smart Control or plug-in parameter, or choose a target; then move a control.
              }
            </span>
            <span class="text-foreground-muted readout truncate" data-testid="learn-last">
              @if (control.learnState().last; as last) {
                Last received: {{ message(last) }} · {{ control.portName(last.port) }}
              } @else {
                Nothing received yet.
              }
            </span>
          </div>
          <button
            type="button"
            class="bezel text-foreground flex h-6.5 shrink-0 items-center gap-1 px-2 text-[11px] font-semibold"
            data-testid="learn-target-menu"
            [xuiMenuTriggerFor]="targetMenu"
          >
            Target
            <ng-icon xui name="matExpandMoreRound" size="14px" />
          </button>
          <button
            type="button"
            class="bezel text-foreground flex h-6.5 shrink-0 items-center px-2.5 text-[11px] font-semibold"
            data-testid="learn-done"
            (click)="control.stopLearn()"
          >
            Done
          </button>
        </div>
        <ng-template #targetMenu>
          <xui-menu class="max-h-96 min-w-56 overflow-y-auto" aria-label="Learn target">
            @for (choice of targetChoices; track choice.label) {
              <button
                xuiMenuItem
                type="button"
                role="menuitemradio"
                [attr.aria-checked]="isTarget(choice.target)"
                [attr.data-testid]="'learn-target-' + choice.id"
                (click)="control.setLearnTarget(choice.target)"
              >
                {{ choice.label }}
              </button>
            }
          </xui-menu>
        </ng-template>
      }

      <div class="flex min-h-[22rem] min-w-0 flex-1">
        <div
          class="border-hairline bg-surface flex w-[24rem] shrink-0 flex-col overflow-y-auto border-e"
          data-testid="assignment-list"
        >
          @for (zone of control.assignments().zones; track zone.id) {
            <section class="flex flex-col" [attr.aria-label]="'Zone ' + zone.name" [attr.data-zone]="zone.id">
              <div [class]="band + ' flex items-center gap-2'">
                @if (renaming()?.kind === 'zone' && renaming()?.id === zone.id) {
                  <ng-container [ngTemplateOutlet]="renameField" [ngTemplateOutletContext]="{ $implicit: zone.name }" />
                } @else {
                  <h3 class="min-w-0 flex-1 truncate" (dblclick)="rename('zone', zone.id)">{{ zone.name }}</h3>
                }
                <div
                  class="bezel-group text-foreground h-5.5 shrink-0 text-[10px] font-semibold"
                  role="radiogroup"
                  [attr.aria-label]="'Active mode of ' + zone.name"
                >
                  @for (mode of zone.modes; track mode.id) {
                    <button
                      type="button"
                      role="radio"
                      class="max-w-24 truncate px-2"
                      [attr.data-testid]="'mode-' + mode.id"
                      [attr.aria-checked]="zone.activeModeId === mode.id"
                      [class.text-lit]="zone.activeModeId === mode.id"
                      (click)="control.setActiveMode(zone.id, mode.id)"
                    >
                      {{ mode.name }}
                    </button>
                  }
                </div>
              </div>
              @for (mode of zone.modes; track mode.id) {
                <div
                  class="text-foreground-muted flex h-6 items-center gap-2 px-3 text-[10px] font-semibold tracking-wide uppercase"
                >
                  @if (renaming()?.kind === 'mode' && renaming()?.id === mode.id) {
                    <ng-container
                      [ngTemplateOutlet]="renameField"
                      [ngTemplateOutletContext]="{ $implicit: mode.name }"
                    />
                  } @else {
                    <span class="truncate" (dblclick)="rename('mode', mode.id)">{{ mode.name }}</span>
                    @if (zone.activeModeId === mode.id) {
                      <span class="text-foreground normal-case">(active)</span>
                    }
                  }
                </div>
                <ul class="flex flex-col pb-1" role="listbox" [attr.aria-label]="zone.name + ' ▸ ' + mode.name">
                  @for (assignment of mode.assignments; track assignment.id) {
                    <li
                      class="flex h-7 cursor-default items-center gap-2 px-3 text-xs outline-none focus-visible:ring-2 focus-visible:ring-(--color-focus)"
                      role="option"
                      tabindex="0"
                      data-testid="assignment-row"
                      [attr.data-assignment]="assignment.id"
                      [attr.aria-selected]="control.selectedId() === assignment.id"
                      [class.bg-primary/25]="control.selectedId() === assignment.id"
                      (click)="control.selectedId.set(assignment.id)"
                      (focus)="control.selectedId.set(assignment.id)"
                      (keydown)="listKey($event)"
                    >
                      <span class="text-foreground min-w-0 flex-1 truncate">
                        <span class="font-semibold">{{
                          assignment.name || control.targetLabel(assignment.target)
                        }}</span>
                        @if (assignment.name) {
                          <span class="text-foreground-muted"> · {{ control.targetLabel(assignment.target) }}</span>
                        }
                      </span>
                      <span class="text-foreground-muted readout shrink-0 text-[11px]">{{ input(assignment) }}</span>
                      @if (control.learnedId() === assignment.id) {
                        <span class="sr-only">(just learned)</span>
                      }
                    </li>
                  } @empty {
                    <li class="text-foreground-muted px-3 py-1 text-xs">No assignments in this mode.</li>
                  }
                </ul>
              }
            </section>
          } @empty {
            <p class="text-foreground-muted p-3 text-xs" data-testid="assignments-empty">
              No assignments yet. Turn on Learn, click a control in the editor, and move a knob, fader or button on your
              controller.
            </p>
          }
        </div>
        <div class="flex min-w-0 flex-1 flex-col overflow-y-auto">
          @if (control.selected(); as found) {
            <an-assignment-inspector
              [assignment]="found.assignment"
              [modeId]="found.mode.id"
              (changed)="change(found.assignment, $event)"
              (moved)="control.moveAssignment(found.assignment.id, $event)"
            />
          } @else {
            <p class="text-foreground-muted p-3 text-xs">Select an assignment to see its input, target and range.</p>
          }
        </div>
      </div>
      <ng-template #renameField let-name>
        <input
          #renameInput
          xuiInput
          class="h-5.5 min-w-0 flex-1 text-[11px] font-semibold normal-case"
          aria-label="New name"
          data-testid="rename-field"
          [value]="name"
          (keydown.enter)="commitRename(renameInput.value)"
          (keydown.escape)="renaming.set(null); $event.stopPropagation()"
          (blur)="commitRename(renameInput.value)"
        />
      </ng-template>
    </an-floating-window>
  `
})
export class ControllerAssignmentsWindow {
  protected readonly control = inject(ControlStore);
  private readonly ui = inject(UiStore);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly band = Band;
  protected readonly renaming = signal<Renaming | null>(null);
  private readonly renameInput = viewChild<ElementRef<HTMLInputElement>>('renameInput');

  /** The zone and mode the menu acts on: the selected assignment's, else the first zone and its active mode. */
  protected readonly currentZone = computed(
    () => this.control.selected()?.zone ?? this.control.assignments().zones[0] ?? null
  );
  protected readonly currentMode = computed(() => {
    const zone = this.currentZone();
    return this.control.selected()?.mode ?? zone?.modes.find(m => m.id === zone.activeModeId) ?? null;
  });

  /** The targets the Learn banner's menu offers (what has no control to click): the selected track's and transport. */
  protected readonly targetChoices: readonly { id: string; label: string; target: ControlTarget }[] = [
    { id: 'volume', label: 'Selected Track: Volume', target: { kind: 'parameter', trackId: null, address: 'volume' } },
    { id: 'pan', label: 'Selected Track: Pan', target: { kind: 'parameter', trackId: null, address: 'pan' } },
    ...TrackSwitches.map(s => ({
      id: s.value,
      label: `Selected Track: ${s.label}`,
      target: { kind: 'trackSwitch' as const, trackId: null, switch: s.value }
    })),
    ...SmartKnobs.map((knob, i) => ({
      id: knob,
      label: `Selected Track: Smart Control ${i + 1}`,
      target: { kind: 'smartControl' as const, trackId: null, address: knob }
    })),
    ...TransportActions.map(a => ({
      id: `transport-${a.value}`,
      label: `Transport: ${a.label}`,
      target: { kind: 'transport' as const, transport: a.value }
    }))
  ];

  constructor() {
    void this.control.load();
    effect(() => {
      const input = this.renameInput()?.nativeElement;
      input?.focus();
      input?.select();
    });
    // While learning, a control clicked anywhere in the editor (not in this window) becomes the target.
    const pick = (event: PointerEvent) => {
      if (!this.control.learning() || event.button !== 0) {
        return;
      }
      const element = event.target instanceof Element ? event.target : null;
      if (!element || this.host.nativeElement.contains(element)) {
        return;
      }
      const target = learnTargetAt(element);
      if (target && !sameTarget(target, this.control.learnState().target)) {
        void this.control.setLearnTarget(target);
      }
    };
    document.addEventListener('pointerdown', pick, true);
    inject(DestroyRef).onDestroy(() => document.removeEventListener('pointerdown', pick, true));
  }

  protected input(assignment: ControlAssignment): string {
    return describeInput(assignment.input, id => this.control.portName(id));
  }

  protected readonly message = describeMessage;

  protected isTarget(target: ControlTarget): boolean {
    return sameTarget(target, this.control.learnState().target);
  }

  protected change(assignment: ControlAssignment, change: AssignmentChange): void {
    void this.control.updateAssignment(assignment.id, change);
  }

  protected deleteSelected(): void {
    const id = this.control.selectedId();
    if (id) {
      void this.control.deleteAssignment(id);
    }
  }

  protected rename(kind: Renaming['kind'], id: string): void {
    this.renaming.set({ kind, id });
  }

  protected commitRename(name: string): void {
    const renaming = this.renaming();
    if (!renaming) {
      return;
    }
    this.renaming.set(null);
    void (renaming.kind === 'zone'
      ? this.control.renameZone(renaming.id, name)
      : this.control.renameMode(renaming.id, name));
  }

  /** ↑ and ↓ move through the assignments of a mode; Delete deletes the selected one. */
  protected listKey(event: KeyboardEvent): void {
    const rows = [...this.host.nativeElement.querySelectorAll<HTMLElement>('[data-testid="assignment-row"]')];
    const at = rows.findIndex(row => row === document.activeElement);
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      rows[Math.min(rows.length - 1, Math.max(0, at + (event.key === 'ArrowDown' ? 1 : -1)))]?.focus();
    } else if (event.key === 'Delete' || event.key === 'Backspace') {
      event.preventDefault();
      this.deleteSelected();
    }
  }

  protected close(): void {
    if (this.control.learning()) {
      void this.control.stopLearn();
    }
    this.ui.controllerAssignmentsOpen.set(false);
  }
}

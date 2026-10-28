import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/** Logic's automation modes (the contract's `AutomationMode`). */
export type AutomationModeValue = 'off' | 'read' | 'touch' | 'latch' | 'write';

/** The modes in Logic's menu order, with their names. */
export const AutomationModes: readonly { value: AutomationModeValue; label: string }[] = [
  { value: 'off', label: 'Off' },
  { value: 'read', label: 'Read' },
  { value: 'touch', label: 'Touch' },
  { value: 'latch', label: 'Latch' },
  { value: 'write', label: 'Write' }
];

/** A mode's name ("Read"); the mode itself for one this list does not know. */
export function automationModeLabel(mode: string): string {
  return AutomationModes.find(m => m.value === mode)?.label ?? mode;
}

/**
 * A track's automation mode, as on Logic's track headers and channel strips: an LED in the mode's colour (Off grey, Read
 * green, Touch amber, Latch purple, Write red), its name, and a caret. A component on a native `<button>`, so its owner
 * attaches the mode menu (`<button anAutomationMode [xuiMenuTriggerFor]="modes">`).
 */
@Component({
  // A component on a native button (so menus attach to it): the prefix rule expects an element selector.
  // eslint-disable-next-line @angular-eslint/component-selector
  selector: 'button[anAutomationMode]',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    type: 'button',
    class: 'bezel text-foreground inline-flex h-5 items-center gap-1 px-1.5 text-[10px] font-semibold',
    'aria-haspopup': 'menu',
    '[attr.aria-label]': '"Automation mode of " + channel() + ": " + name()',
    '[attr.data-mode]': 'mode()'
  },
  template: `
    <span
      class="size-1.5 shrink-0 rounded-full"
      [class.bg-automation-off]="mode() === 'off'"
      [class.bg-automation-read]="mode() === 'read'"
      [class.bg-automation-touch]="mode() === 'touch'"
      [class.bg-automation-latch]="mode() === 'latch'"
      [class.bg-automation-write]="mode() === 'write'"
      aria-hidden="true"
    ></span>
    <span class="min-w-0 truncate">{{ name() }}</span>
    <svg viewBox="0 0 8 8" class="text-foreground-muted size-2 shrink-0" aria-hidden="true">
      <path d="M1.5 3l2.5 2.5L6.5 3" fill="none" stroke="currentColor" stroke-width="1.25" stroke-linecap="round" />
    </svg>
  `
})
export class AnAutomationMode {
  readonly mode = input.required<AutomationModeValue>();
  /** The track's name, for the accessible name. */
  readonly channel = input.required<string>();

  protected readonly name = computed(() => automationModeLabel(this.mode()));
}

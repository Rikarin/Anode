import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { SmartControl } from '@anode/api-client';
import { AnKnob } from '@anode/audio-ui';
import { SmartControlsStore } from '../smart-controls/smart-controls.store';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';

/**
 * The remote's Smart Controls (docs/11-phase-10.md): the selected track's knobs (8 or 12, named and valued as on the
 * desktop) at touch size, its switches as 44 px toggles. Moves go through the Smart Controls store (`setSmartControl`,
 * coalesced while dragging), so their mappings and automation behave as on the desktop.
 */
@Component({
  selector: 'an-remote-smart-controls',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AnKnob],
  host: { class: 'flex min-h-0 flex-col gap-2 p-2' },
  template: `
    @if (track(); as t) {
      <span class="text-foreground text-[13px] font-semibold">{{ t.name }} · Smart Controls</span>
      <div class="grid grid-cols-4 gap-3 sm:grid-cols-6" role="group" [attr.aria-label]="t.name + ' Smart Controls'">
        @for (control of knobs(); track control.id) {
          <div class="flex flex-col items-center gap-1 [&_[role=slider]]:size-14" data-testid="remote-smart-knob">
            <an-knob
              [label]="control.name || control.id"
              [value]="smart.value(t.id, control.id)"
              (valueChange)="smart.set(t.id, control.id, $event)"
              (committed)="smart.release(t.id, control.id)"
            />
            <span class="readout text-foreground text-[11px]">{{
              smart.format(t.id, control.id, smart.value(t.id, control.id))
            }}</span>
          </div>
        }
      </div>
      @if (switches().length) {
        <div class="flex flex-wrap gap-2">
          @for (control of switches(); track control.id) {
            <button
              type="button"
              class="bezel text-foreground min-h-11 rounded-md px-4 text-[12px] font-semibold"
              [attr.aria-pressed]="smart.value(t.id, control.id) >= 0.5"
              [class.text-lit]="smart.value(t.id, control.id) >= 0.5"
              (click)="toggle(t.id, control)"
            >
              {{ control.name || control.id }}
            </button>
          }
        </div>
      }
    } @else {
      <p class="text-foreground-muted text-sm">Select a track to see its Smart Controls.</p>
    }
  `
})
export class RemoteSmartControls {
  protected readonly smart = inject(SmartControlsStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);

  protected readonly track = computed(() => {
    const id = this.selection.track();
    const track = id ? this.project.track(id) : undefined;
    return track && track.kind !== 'vca' ? track : null;
  });
  private readonly controls = computed(() => {
    const track = this.track();
    return track ? this.smart.controls(track.id).controls : [];
  });
  protected readonly knobs = computed(() => this.controls().filter(c => c.kind === 'knob'));
  protected readonly switches = computed(() => this.controls().filter(c => c.kind === 'switch'));

  protected toggle(trackId: string, control: SmartControl): void {
    const on = this.smart.value(trackId, control.id) >= 0.5;
    void this.smart.set(trackId, control.id, on ? 0 : 1);
    this.smart.release(trackId, control.id);
  }
}

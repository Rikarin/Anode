import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { PhysicalModelPanel } from './physical-model-panel';
import type { DeferredPanelKind, PluginPanelHost } from './plugin-panel';
import { SampleAlchemyPanel } from './sample-alchemy-panel';

/**
 * The T3 instruments' custom panels (Phase 11): the physical model's object and pickups, Sample Alchemy's source view.
 * The plugin window loads this with `@defer`, so neither is in the initial bundle.
 */
@Component({
  selector: 'an-t3-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PhysicalModelPanel, SampleAlchemyPanel],
  host: { class: 'contents' },
  template: `
    @switch (kind()) {
      @case ('physicalModel') {
        <an-physical-model-panel [host]="host()" />
      }
      @case ('sampleAlchemy') {
        <an-sample-alchemy-panel [host]="host()" />
      }
    }
  `
})
export class T3Panel {
  readonly kind = input.required<DeferredPanelKind>();
  readonly host = input.required<PluginPanelHost>();
}

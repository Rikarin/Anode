import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { EqPanel } from './eq-panel';
import { PhysicalModelPanel } from './physical-model-panel';
import type { DeferredPanelKind, PluginPanelHost } from './plugin-panel';
import { SampleAlchemyPanel } from './sample-alchemy-panel';

/**
 * The custom panels the plugin window loads with `@defer`, so none is in the initial bundle: the T3 instruments'
 * (Phase 11: the physical model's object and pickups, Sample Alchemy's source view) and an EQ's curve.
 */
@Component({
  selector: 'an-t3-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [EqPanel, PhysicalModelPanel, SampleAlchemyPanel],
  host: { class: 'contents' },
  template: `
    @switch (kind()) {
      @case ('physicalModel') {
        <an-physical-model-panel [host]="host()" />
      }
      @case ('sampleAlchemy') {
        <an-sample-alchemy-panel [host]="host()" />
      }
      @case ('eqCurve') {
        <an-eq-panel [host]="host()" />
      }
    }
  `
})
export class T3Panel {
  readonly kind = input.required<DeferredPanelKind>();
  readonly host = input.required<PluginPanelHost>();
}

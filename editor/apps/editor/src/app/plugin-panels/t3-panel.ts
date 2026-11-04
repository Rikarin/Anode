import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { EqPanel } from './eq-panel';
import { PhasePanel, ScopePanel, TunerPanel } from './meter-panels';
import { PhysicalModelPanel } from './physical-model-panel';
import type { DeferredPanelKind, PluginPanelHost } from './plugin-panel';
import { SampleAlchemyPanel } from './sample-alchemy-panel';
import { ExpanderPanel, OscillatorPanel, SpreadPanel } from './utility-panels';
import { VocoderPanel, VoiceShaperPanel } from './voice-panels';

/**
 * The custom panels the plugin window loads with `@defer`, so none is in the initial bundle: the T3 instruments'
 * (Phase 11: the physical model's object and pickups, Sample Alchemy's source view), an EQ's curve, and the utility
 * effects' and meters' displays.
 */
@Component({
  selector: 'an-t3-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    EqPanel,
    ExpanderPanel,
    OscillatorPanel,
    PhasePanel,
    PhysicalModelPanel,
    SampleAlchemyPanel,
    ScopePanel,
    SpreadPanel,
    TunerPanel,
    VocoderPanel,
    VoiceShaperPanel
  ],
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
      @case ('tuner') {
        <an-tuner-panel [host]="host()" />
      }
      @case ('scope') {
        <an-scope-panel [host]="host()" />
      }
      @case ('phaseMeter') {
        <an-phase-panel [host]="host()" />
      }
      @case ('expander') {
        <an-expander-panel [host]="host()" />
      }
      @case ('testOscillator') {
        <an-oscillator-panel [host]="host()" />
      }
      @case ('frequencySpread') {
        <an-spread-panel [host]="host()" />
      }
      @case ('vocoder') {
        <an-vocoder-panel [host]="host()" />
      }
      @case ('voiceShaper') {
        <an-voice-shaper-panel [host]="host()" />
      }
    }
  `
})
export class T3Panel {
  readonly kind = input.required<DeferredPanelKind>();
  readonly host = input.required<PluginPanelHost>();
}

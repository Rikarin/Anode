import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import type { SampleRateChangeMode } from '@anode/api-client';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiSegmentedControlImports, type XuiSegmentedOption } from '@xui/segmented-control';
import { ProjectSession } from '../state/project-session';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';

const Rates: XuiSegmentedOption<number>[] = [
  { value: 44_100, label: '44.1' },
  { value: 48_000, label: '48' },
  { value: 88_200, label: '88.2' },
  { value: 96_000, label: '96' },
  { value: 176_400, label: '176.4' },
  { value: 192_000, label: '192' }
];

/**
 * File ▸ Project Settings ▸ Sample Rate…: the new rate, and what happens to the audio, said plainly, because the two
 * sound nothing alike. The Server keeps a backup first and saves the project at the new rate, so the way back is Revert
 * to Backup, not undo.
 */
@Component({
  selector: 'an-change-sample-rate',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiSegmentedControlImports],
  template: `
    <xui-dialog [(open)]="ui.sampleRateOpen" title="Change Sample Rate" size="md">
      <form (submit)="$event.preventDefault(); change()">
        <xui-dialog-body class="flex flex-col gap-4">
          <div class="flex flex-col gap-1 text-sm">
            <span class="text-foreground-muted text-xs"
              >New sample rate (kHz). The project is at {{ current() }} Hz.</span
            >
            <xui-segmented-control
              size="sm"
              ariaLabel="New sample rate"
              [options]="rates"
              [value]="rate()"
              (valueChange)="rate.set($event ?? current())"
            />
          </div>
          <fieldset class="flex flex-col gap-2">
            <legend class="text-foreground-muted mb-1 text-xs">What happens to the audio</legend>
            <label class="flex gap-2 text-sm">
              <input
                type="radio"
                name="an-rate-mode"
                data-testid="rate-mode-convert"
                [checked]="mode() === 'convert'"
                (change)="mode.set('convert')"
              />
              <span class="flex flex-col">
                <span class="font-medium">Convert the audio</span>
                <span class="text-foreground-muted text-xs">
                  Every file is resampled to the new rate. The project sounds the same: same pitch, same tempo.
                </span>
              </span>
            </label>
            <label class="flex gap-2 text-sm">
              <input
                type="radio"
                name="an-rate-mode"
                data-testid="rate-mode-keep"
                [checked]="mode() === 'keepSamples'"
                (change)="mode.set('keepSamples')"
              />
              <span class="flex flex-col">
                <span class="font-medium">Keep the samples</span>
                <span class="text-foreground-muted text-xs">
                  Not one sample changes, and the whole project plays {{ speed() }}: the tempo moves with it and the
                  audio's pitch shifts. MIDI notes keep their pitch.
                </span>
              </span>
            </label>
          </fieldset>
          <p class="text-foreground-muted text-xs">
            Anode keeps a backup first, then saves the project, unsaved edits included. This cannot be undone: go back
            with Revert to Backup. Files stay on the disk, so the project grows. Set your audio device to the new rate
            in Settings; on another rate the project plays off-speed and cannot record.
          </p>
        </xui-dialog-body>
        <xui-dialog-footer>
          <button xuiButton type="button" variant="ghost" (click)="ui.sampleRateOpen.set(false)">Cancel</button>
          <button
            xuiButton
            type="submit"
            data-testid="change-sample-rate"
            [disabled]="rate() === current()"
            [loading]="changing()"
          >
            Change Sample Rate
          </button>
        </xui-dialog-footer>
      </form>
    </xui-dialog>
  `
})
export class ChangeSampleRateDialog {
  protected readonly ui = inject(UiStore);
  private readonly session = inject(ProjectSession);
  private readonly project = inject(ProjectStore);

  protected readonly rates = Rates;
  protected readonly current = computed(() => this.project.info()?.sampleRate ?? 48_000);
  protected readonly rate = signal(this.current());
  protected readonly mode = signal<SampleRateChangeMode>('convert');
  protected readonly changing = signal(false);

  /** How much faster or slower keeping the samples plays. */
  protected readonly speed = computed(() => {
    const ratio = this.rate() / this.current();
    if (ratio === 1) {
      return 'at another speed';
    }

    return ratio > 1 ? `${ratio.toFixed(2)} times faster` : `${(1 / ratio).toFixed(2)} times slower`;
  });

  protected async change(): Promise<void> {
    this.changing.set(true);
    try {
      if (await this.session.changeSampleRate(this.rate(), this.mode())) {
        this.ui.sampleRateOpen.set(false);
      }
    } finally {
      this.changing.set(false);
    }
  }
}

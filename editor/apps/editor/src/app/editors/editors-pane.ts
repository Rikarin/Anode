import { ChangeDetectionStrategy, Component, effect, inject, untracked } from '@angular/core';
import { AudioTrackEditor } from '../audio-editor/audio-track-editor';
import { SmartTempoEditor } from '../audio-editor/smart-tempo-editor';
import { DrumMachineEditor } from '../drum-machine/drum-machine-editor';
import { PianoRoll } from '../piano-roll/piano-roll';
import { ProjectStore } from '../state/project.store';
import { SelectionStore } from '../state/selection.store';
import { type EditorView, UiStore } from '../state/ui.store';
import { StepSequencer } from '../step-sequencer/step-sequencer';

const Views: readonly { value: EditorView; label: string }[] = [
  { value: 'pianoRoll', label: 'Piano Roll' },
  { value: 'audio', label: 'Audio Track' },
  { value: 'smartTempo', label: 'Smart Tempo' },
  { value: 'stepSequencer', label: 'Step Sequencer' },
  { value: 'drumMachine', label: 'Drum Machine' }
];

/**
 * The Editors pane (Logic's editor area): the Piano Roll, the Audio Track Editor, the Smart Tempo Editor, the Step
 * Sequencer or the Drum Machine Designer, switched by the tabs in its corner. It follows the selection as Logic does: an
 * audio region brings the Audio Track Editor when the Piano Roll showed, a MIDI region the Piano Roll, a pattern
 * region the Step Sequencer.
 */
@Component({
  selector: 'an-editors-pane',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AudioTrackEditor, DrumMachineEditor, PianoRoll, SmartTempoEditor, StepSequencer],
  host: { class: 'flex h-full min-h-0 flex-col' },
  template: `
    <div
      class="border-hairline bg-surface flex h-7 shrink-0 items-center gap-1 border-b px-2"
      role="tablist"
      aria-label="Editor"
      data-testid="editor-view"
    >
      @for (view of views; track view.value) {
        <button
          type="button"
          role="tab"
          class="h-5 rounded-[5px] px-2 text-[11px] font-semibold"
          [class.bezel]="ui.editorView() === view.value"
          [class.text-foreground]="ui.editorView() === view.value"
          [class.text-foreground-muted]="ui.editorView() !== view.value"
          [attr.aria-selected]="ui.editorView() === view.value"
          [attr.tabindex]="ui.editorView() === view.value ? 0 : -1"
          (click)="ui.editorView.set(view.value)"
          (keydown)="arrow($event)"
        >
          {{ view.label }}
        </button>
      }
    </div>
    <div class="min-h-0 flex-1" role="tabpanel" [attr.aria-label]="label()">
      @switch (ui.editorView()) {
        @case ('audio') {
          <an-audio-track-editor />
        }
        @case ('smartTempo') {
          <an-smart-tempo-editor />
        }
        @case ('stepSequencer') {
          <an-step-sequencer />
        }
        @case ('drumMachine') {
          <an-drum-machine-editor />
        }
        @default {
          <an-piano-roll />
        }
      }
    </div>
  `
})
export class EditorsPane {
  protected readonly ui = inject(UiStore);
  private readonly project = inject(ProjectStore);
  private readonly selection = inject(SelectionStore);
  protected readonly views = Views;

  constructor() {
    effect(() => {
      const selected = this.selection.regions();
      untracked(() => {
        const audio = [...selected].some(id => this.project.regions().has(id));
        const midi = [...selected].some(id => this.project.midiRegions().has(id));
        const pattern = [...selected].some(id => this.project.patternRegions().has(id));
        const view = this.ui.editorView();
        if (pattern && !audio && !midi) {
          this.ui.editorView.set('stepSequencer');
        } else if (audio && !midi && view === 'pianoRoll') {
          this.ui.editorView.set('audio');
        } else if (midi && !audio && view !== 'pianoRoll') {
          this.ui.editorView.set('pianoRoll');
        }
      });
    });
  }

  protected label(): string {
    return Views.find(v => v.value === this.ui.editorView())?.label ?? '';
  }

  /** ←/→ move between the tabs. */
  protected arrow(event: KeyboardEvent): void {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') {
      return;
    }
    event.preventDefault();
    const index = Views.findIndex(v => v.value === this.ui.editorView());
    const next = Views[(index + (event.key === 'ArrowRight' ? 1 : Views.length - 1)) % Views.length];
    if (next) {
      this.ui.editorView.set(next.value);
      const tabs = (event.currentTarget as HTMLElement).parentElement?.querySelectorAll<HTMLElement>('[role="tab"]');
      tabs?.[Views.indexOf(next)]?.focus();
    }
  }
}

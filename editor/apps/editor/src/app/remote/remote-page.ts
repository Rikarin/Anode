import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * The `remote` route (docs/11-phase-10.md, workstream C): Logic Remote's equivalent for phones and tablets on the LAN,
 * in the same build: transport and LCD, the mixer with banks, Smart Controls, the Live Loops grid, key-command pads,
 * the touch keyboard and drum pads, the Step Sequencer, the track list, and listen-in. Lazily loaded, so the desktop
 * editor's bundle does not carry it. A placeholder until the editor workstream builds it.
 */
@Component({
  selector: 'an-remote-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'flex h-full items-center justify-center p-4',
    role: 'region',
    'aria-label': 'Remote'
  },
  template: `
    <section class="panel flex max-w-md flex-col gap-2 p-5" data-testid="remote">
      <h1 class="text-[15px] font-semibold">{{ title }}</h1>
      <p class="text-sm">{{ note }}</p>
    </section>
  `
})
export class RemotePage {
  protected readonly title = 'Anode Remote';
  protected readonly note = 'The remote is not here yet.';
}

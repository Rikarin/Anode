import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import type { GroupProperties } from '@anode/api-client';
import { XuiButtonImports } from '@xui/button';
import { XuiDialogImports } from '@xui/dialog';
import { XuiInputImports } from '@xui/input';
import { GroupColors, MixerStore } from '../state/mixer.store';
import { ProjectStore } from '../state/project.store';
import { UiStore } from '../state/ui.store';

/** The properties a group links, as Logic's Group Settings window lists them. */
export const LinkedProperties: readonly { key: keyof GroupProperties; label: string; section: 'Editing' | 'Mixer' }[] =
  [
    { key: 'selection', label: 'Editing (Selection)', section: 'Editing' },
    { key: 'volume', label: 'Volume', section: 'Mixer' },
    { key: 'mute', label: 'Mute', section: 'Mixer' },
    { key: 'solo', label: 'Solo', section: 'Mixer' },
    { key: 'pan', label: 'Pan', section: 'Mixer' },
    { key: 'sends', label: 'Sends', section: 'Mixer' },
    { key: 'recordArm', label: 'Record Arm', section: 'Mixer' },
    { key: 'automation', label: 'Automation Mode', section: 'Mixer' }
  ];

/**
 * Logic's Group Settings window for one mixer group: its name, colour and whether it is on, which properties its
 * members share (volume keeps their offsets), the members, and Delete. Each change is an undoable `setGroup`.
 */
@Component({
  selector: 'an-group-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [XuiButtonImports, XuiDialogImports, XuiInputImports],
  template: `
    <xui-dialog [open]="group() !== null" (openChange)="$event || ui.groupSettings.set(null)" title="Group Settings">
      @if (group(); as g) {
        <xui-dialog-body class="flex flex-col gap-3 text-xs" data-testid="group-settings">
          <div class="grid grid-cols-[5.5rem_1fr] items-center gap-x-2 gap-y-2">
            <label class="text-foreground-muted" for="an-group-name">Group {{ g.number }}</label>
            <input
              #name
              id="an-group-name"
              xuiInput
              class="h-7 text-xs"
              data-testid="group-name"
              [value]="g.name"
              (change)="rename(name.value)"
              (keydown.enter)="rename(name.value)"
            />
            <span class="text-foreground-muted">Colour</span>
            <div class="flex gap-1.5" role="group" aria-label="Group colour">
              @for (color of colors; track color) {
                <button
                  type="button"
                  class="focus-visible:outline-focus aria-pressed:ring-foreground size-5 rounded-[5px] shadow-[inset_0_0_0_1px_var(--hairline)] focus-visible:outline-2 aria-pressed:ring-2 aria-pressed:ring-offset-1"
                  [style.background-color]="color"
                  [attr.aria-label]="'Colour ' + color"
                  [attr.aria-pressed]="color === g.color"
                  (click)="mixer.setGroup(g, { color })"
                ></button>
              }
            </div>
            <span class="text-foreground-muted">Settings</span>
            <label class="text-foreground flex items-center gap-2">
              <input
                type="checkbox"
                class="accent-(--color-primary)"
                data-testid="group-enabled"
                [checked]="g.enabled"
                (change)="mixer.setGroup(g, { enabled: !g.enabled })"
              />
              Enabled
            </label>
          </div>
          @for (section of sections; track section) {
            <fieldset class="border-hairline flex flex-col gap-1.5 rounded-md border px-3 py-2">
              <legend class="text-foreground-muted px-1 text-[10px] font-semibold tracking-wide uppercase">
                {{ section }}
              </legend>
              <div class="grid grid-cols-2 gap-x-4 gap-y-1.5">
                @for (property of propertiesOf(section); track property.key) {
                  <label class="text-foreground flex items-center gap-2">
                    <input
                      type="checkbox"
                      class="accent-(--color-primary)"
                      [attr.data-testid]="'group-link-' + property.key"
                      [checked]="g.properties[property.key]"
                      (change)="toggle(property.key)"
                    />
                    {{ property.label }}
                  </label>
                }
              </div>
            </fieldset>
          }
          <div class="flex flex-col gap-1">
            <span class="text-foreground-muted text-[10px] font-semibold tracking-wide uppercase">Members</span>
            <p class="text-foreground">
              @for (member of members(); track member.id; let last = $last) {
                {{ member.name }}{{ last ? '' : ', ' }}
              } @empty {
                <span class="text-foreground-muted">No strips yet: pick this group in a strip's group slot.</span>
              }
            </p>
          </div>
        </xui-dialog-body>
        <xui-dialog-footer>
          <button xuiButton type="button" variant="ghost" color="error" (click)="remove(g.number)">Delete Group</button>
          <button xuiButton type="button" (click)="ui.groupSettings.set(null)">Done</button>
        </xui-dialog-footer>
      }
    </xui-dialog>
  `
})
export class GroupSettings {
  protected readonly ui = inject(UiStore);
  protected readonly mixer = inject(MixerStore);
  private readonly project = inject(ProjectStore);

  protected readonly colors = GroupColors;
  protected readonly sections = ['Editing', 'Mixer'] as const;
  protected readonly group = computed(() => {
    const number = this.ui.groupSettings();
    return number === null ? null : (this.project.groups().get(number) ?? null);
  });
  protected readonly members = computed(() => {
    const g = this.group();
    return g ? this.mixer.groupMembers(g.number) : [];
  });

  protected propertiesOf(section: 'Editing' | 'Mixer') {
    return LinkedProperties.filter(p => p.section === section);
  }

  protected rename(name: string): void {
    const g = this.group();
    const trimmed = name.trim();
    if (g && trimmed && trimmed !== g.name) {
      void this.mixer.setGroup(g, { name: trimmed });
    }
  }

  protected toggle(key: keyof GroupProperties): void {
    const g = this.group();
    if (g) {
      void this.mixer.setGroup(g, { properties: { ...g.properties, [key]: !g.properties[key] } });
    }
  }

  protected remove(number: number): void {
    this.ui.groupSettings.set(null);
    void this.mixer.deleteGroup(number);
  }
}

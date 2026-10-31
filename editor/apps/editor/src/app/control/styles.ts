/** A native select or field in the control windows (the Grooves window's, DESIGN.md "MIDI Transform"). */
export const Field =
  'border-border bg-surface-inset text-foreground focus-visible:outline-focus h-6 w-full min-w-0 rounded-[5px] border px-1 text-xs focus-visible:outline-2';

/** An Inspector band heading a section (DESIGN.md "Typography"). */
export const Band =
  'text-foreground border-hairline bg-surface-raised border-y px-3 py-1 text-[11px] font-bold shadow-[inset_0_1px_0_var(--highlight)]';

/** The value of a select's change event. */
export function selectValue(event: Event): string {
  return (event.target as HTMLSelectElement | HTMLInputElement).value;
}

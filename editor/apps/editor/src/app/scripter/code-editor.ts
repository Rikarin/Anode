import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  effect,
  inject,
  input,
  output,
  untracked
} from '@angular/core';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { javascript } from '@codemirror/lang-javascript';
import { HighlightStyle, bracketMatching, indentOnInput, syntaxHighlighting } from '@codemirror/language';
import { EditorState, type Extension, RangeSetBuilder, StateEffect, StateField } from '@codemirror/state';
import {
  Decoration,
  type DecorationSet,
  EditorView,
  drawSelection,
  highlightActiveLine,
  highlightActiveLineGutter,
  keymap,
  lineNumbers
} from '@codemirror/view';
import { tags } from '@lezer/highlight';

/** Sets the line to mark as the error's (1-based; null: none). */
const setErrorLine = StateEffect.define<number | null>();

/** The error line's decoration, mapped through edits until the next {@link setErrorLine}. */
const errorLineField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(decorations, transaction) {
    let next = decorations.map(transaction.changes);
    for (const effect of transaction.effects) {
      if (effect.is(setErrorLine)) {
        const line = effect.value;
        const builder = new RangeSetBuilder<Decoration>();
        if (line !== null && line >= 1 && line <= transaction.state.doc.lines) {
          const from = transaction.state.doc.line(line).from;
          builder.add(from, from, Decoration.line({ class: 'cm-an-error-line', attributes: { 'data-error': '' } }));
        }
        next = builder.finish();
      }
    }
    return next;
  },
  provide: field => EditorView.decorations.from(field)
});

/** Syntax colours from the design tokens (`--code-*`, both themes). */
const highlight = HighlightStyle.define([
  {
    tag: [tags.keyword, tags.controlKeyword, tags.definitionKeyword, tags.moduleKeyword],
    color: 'var(--code-keyword)'
  },
  { tag: [tags.bool, tags.null, tags.atom, tags.self], color: 'var(--code-keyword)' },
  { tag: [tags.string, tags.special(tags.string), tags.regexp], color: 'var(--code-string)' },
  { tag: [tags.number], color: 'var(--code-number)' },
  { tag: [tags.comment, tags.lineComment, tags.blockComment], color: 'var(--code-comment)', fontStyle: 'italic' },
  {
    tag: [
      tags.function(tags.variableName),
      tags.function(tags.propertyName),
      tags.definition(tags.function(tags.variableName))
    ],
    color: 'var(--code-function)'
  },
  { tag: [tags.className, tags.typeName], color: 'var(--code-function)', fontWeight: '600' },
  { tag: tags.invalid, color: 'var(--code-error)' }
]);

/** The editor's chrome from the design tokens: an inset field, `font-mono`, hairline gutters. */
const theme = EditorView.theme({
  '&': {
    height: '100%',
    backgroundColor: 'var(--surface-inset)',
    color: 'var(--foreground)',
    fontSize: '12px'
  },
  '&.cm-focused': { outline: 'none' },
  '.cm-scroller': { fontFamily: 'var(--font-mono, ui-monospace, monospace)', lineHeight: '1.5' },
  '.cm-content': { caretColor: 'var(--foreground)', padding: '6px 0' },
  '.cm-cursor, .cm-dropCursor': { borderLeftColor: 'var(--foreground)' },
  '&.cm-focused > .cm-scroller > .cm-selectionLayer .cm-selectionBackground, .cm-selectionBackground, ::selection': {
    backgroundColor: 'var(--selection)'
  },
  '.cm-gutters': {
    backgroundColor: 'var(--surface)',
    color: 'var(--foreground-subtle)',
    borderRight: '1px solid var(--hairline)'
  },
  '.cm-activeLine': { backgroundColor: 'var(--hairline)' },
  '.cm-activeLineGutter': { backgroundColor: 'var(--hairline)', color: 'var(--foreground)' },
  '&.cm-focused .cm-matchingBracket': { backgroundColor: 'var(--selection)', outline: '1px solid var(--focus)' },
  '&.cm-focused .cm-nonmatchingBracket': { color: 'var(--code-error)' },
  '.cm-an-error-line': {
    backgroundColor: 'var(--code-error-line)',
    boxShadow: 'inset 2px 0 0 var(--code-error)'
  }
});

/**
 * A JavaScript code editor (CodeMirror 6) for the Script Editor: line numbers, highlighting, bracket matching, undo
 * history, and a marked error line. It reports every change (`valueChange`) and ⌘↩ (`run`); a new `value` from outside
 * (a preset, Revert) replaces the text. Its content is the focusable, named text box (`role="textbox"`, `label`,
 * `data-testid` from `testId`); Tab indents, and Escape then Tab leaves it (CodeMirror's convention).
 */
@Component({
  selector: 'an-code-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-h-0 overflow-hidden' },
  template: ''
})
export class CodeEditor {
  /** The text. */
  readonly value = input.required<string>();
  /** The line to mark as an error's (1-based; null: none). */
  readonly errorLine = input<number | null>(null);
  /** The text box's accessible name. */
  readonly label = input('Code');
  readonly testId = input<string | null>(null);
  readonly valueChange = output<string>();
  /** ⌘↩ (Ctrl+↩). */
  readonly run = output();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private view: EditorView | null = null;

  constructor() {
    afterNextRender(() => {
      this.view = new EditorView({
        parent: this.host.nativeElement,
        state: EditorState.create({ doc: untracked(this.value), extensions: this.extensions() })
      });
      this.view.dispatch({ effects: setErrorLine.of(untracked(this.errorLine)) });
    });
    effect(() => {
      const value = this.value();
      const view = this.view;
      if (view && view.state.doc.toString() !== value) {
        view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: value } });
      }
    });
    effect(() => {
      const line = this.errorLine();
      this.view?.dispatch({ effects: setErrorLine.of(line) });
    });
    inject(DestroyRef).onDestroy(() => this.view?.destroy());
  }

  /** The text as edited now. */
  text(): string {
    return this.view?.state.doc.toString() ?? this.value();
  }

  focus(): void {
    this.view?.focus();
  }

  private extensions(): Extension[] {
    const attributes: Record<string, string> = { 'aria-label': this.label() };
    const testId = this.testId();
    if (testId) {
      attributes['data-testid'] = testId;
    }
    return [
      lineNumbers(),
      highlightActiveLineGutter(),
      highlightActiveLine(),
      history(),
      drawSelection(),
      indentOnInput(),
      bracketMatching(),
      javascript(),
      syntaxHighlighting(highlight),
      errorLineField,
      keymap.of([
        {
          key: 'Mod-Enter',
          run: () => {
            this.run.emit();
            return true;
          }
        },
        ...defaultKeymap,
        ...historyKeymap,
        indentWithTab
      ]),
      EditorView.contentAttributes.of(attributes),
      EditorView.updateListener.of(update => {
        if (update.docChanged) {
          this.valueChange.emit(update.state.doc.toString());
        }
      }),
      theme
    ];
  }
}

import type { XuiDockManagerLayout, XuiDockPane } from '@xui/dock-manager';

/** The panes of the main window, by content id. */
export type PaneId =
  'library' | 'inspector' | 'tracks' | 'mixer' | 'editors' | 'keyboard' | 'browsers' | 'lists' | 'notes';

/**
 * The layout a new project (and a new screenset) opens with, as in Logic: Library and Inspector on the left, the
 * Tracks area in the middle over the Mixer, Editors and Keyboard, and the Browsers, List Editors and Note Pad tucked
 * away on the right edge until shown (F, D, ⌥⌘P).
 */
export function defaultLayout(): XuiDockManagerLayout {
  return {
    rootPane: {
      type: 'splitPane',
      orientation: 'horizontal',
      panes: [
        {
          type: 'splitPane',
          orientation: 'vertical',
          size: 1,
          panes: [
            { type: 'contentPane', contentId: 'library', header: 'Library', size: 1, allowClose: false },
            { type: 'contentPane', contentId: 'inspector', header: 'Inspector', size: 3, allowClose: false }
          ]
        },
        {
          type: 'splitPane',
          orientation: 'vertical',
          size: 4,
          panes: [
            { type: 'contentPane', contentId: 'tracks', header: 'Tracks', size: 3, allowClose: false },
            {
              type: 'tabGroupPane',
              size: 2,
              panes: [
                { type: 'contentPane', contentId: 'mixer', header: 'Mixer', allowClose: false },
                { type: 'contentPane', contentId: 'editors', header: 'Editors', allowClose: false },
                { type: 'contentPane', contentId: 'keyboard', header: 'Keyboard', allowClose: false }
              ]
            }
          ]
        },
        {
          type: 'splitPane',
          orientation: 'vertical',
          size: 1.2,
          panes: [
            {
              type: 'contentPane',
              contentId: 'browsers',
              header: 'Browsers',
              size: 2,
              isPinned: false,
              allowClose: false
            },
            {
              type: 'contentPane',
              contentId: 'lists',
              header: 'List Editors',
              size: 1,
              isPinned: false,
              allowClose: false
            },
            { type: 'contentPane', contentId: 'notes', header: 'Note Pad', size: 1, isPinned: false, allowClose: false }
          ]
        }
      ]
    }
  };
}

/**
 * The layout with a pane brought forward: pinned if it was tucked away, its tab selected if it is in a tab group, and
 * restored if another pane is maximised. Unchanged when the layout has no such pane.
 */
export function showPane(layout: XuiDockManagerLayout, contentId: string): XuiDockManagerLayout {
  const next = structuredClone(layout);
  const visit = (pane: XuiDockPane): void => {
    switch (pane.type) {
      case 'contentPane':
        if (pane.contentId === contentId) {
          pane.isPinned = true;
        } else if (pane.isMaximized) {
          pane.isMaximized = false;
        }
        break;
      case 'tabGroupPane': {
        const index = pane.panes.findIndex(p => p.contentId === contentId);
        if (index >= 0) {
          pane.selectedIndex = index;
        }
        pane.panes.forEach(visit);
        break;
      }
      case 'splitPane':
        pane.panes.forEach(visit);
        break;
      case 'documentHost':
        visit(pane.rootPane);
        break;
    }
  };
  visit(next.rootPane);
  next.floatingPanes?.forEach(visit);
  return hasPane(layout, contentId) ? next : layout;
}

/** Whether a layout shows a pane (pinned, docked or floating). */
export function hasPane(layout: XuiDockManagerLayout, contentId: string): boolean {
  return JSON.stringify(layout).includes(`"contentId":"${contentId}"`);
}

/** A screenset's stored layout, or null when it is not one this editor can use. */
export function parseLayout(text: string): XuiDockManagerLayout | null {
  try {
    const layout = JSON.parse(text) as Partial<XuiDockManagerLayout> | null;
    const type = layout?.rootPane?.type;
    if (!layout?.rootPane || (type !== 'splitPane' && type !== 'documentHost')) {
      return null;
    }
    // A layout saved before a pane existed gets the default's arrangement for the panes it lacks.
    const whole = layout as XuiDockManagerLayout;
    return ['tracks', 'browsers', 'lists', 'notes'].every(id => hasPane(whole, id)) ? whole : null;
  } catch {
    return null;
  }
}

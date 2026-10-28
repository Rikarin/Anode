# S6: Native plugin editor windows next to the Electron window

- **Date:** 2026-10-28
- **Question** (ADR-0003, plan §12 Phase 0 spike 6, answered in Phase 4 workstream E): sandboxed plugins (VST3, AU,
  CLAP) run in `anode-plugin` processes, not in Electron's. Can their own editors open as native windows that sit with
  the desktop app's window, parented to it through its native handle, or else as floating utility windows?
- **Code:** `engine/native/an_plug/src/an_plug_mac.mm` (the windows), `Anode.Plugins` (`IHostedPlugin.OpenEditor`,
  `EditorPlacement`), the sandbox's `editor` request. **Tests:** `engine/tests/Anode.Plugins.Tests/EditorWindowTests.cs`
  (they put real windows on screen for a moment, so they run only with `ANODE_TEST_WINDOWS=1`).

## Verdict

**Yes on macOS, as top-level windows owned by the sandbox, not as child windows of Electron's.** The sandbox (an
accessory `NSApplication`: no Dock icon, no menu bar) opens the plugin's view in its own window; by default a
**floating utility panel** over the desktop app's window. Ordering a normal window **directly above another process's
window** works too. What does not exist in public API is a *child* window across processes (one that moves, minimises
and closes with its parent), and Electron's native handle is useless for it:

- `BrowserWindow.getNativeWindowHandle()` is an `NSView *` in Electron's main process: a pointer that means nothing in
  the sandbox. `-[NSWindow addChildWindow:ordered:]` needs both windows in one process. Embedding a view across processes
  needs private API (`CALayerHost`/remote views), which Anode does not use.
- What crosses processes is the **window number** (`CGWindowID`): `-[NSWindow orderWindow:relativeTo:]` accepts another
  process's, and `CGWindowListCopyWindowInfo` reads any window's owner, bounds and layer **without the screen-recording
  permission** (only titles need it). So the sandbox finds the desktop app's main window by the app's process id and no
  handle has to travel through the host and the server: the IPC and REST contracts stay as committed.

## Measured (M1 Max, macOS 27.2, built-in display; the NativeAOT sandbox and the JIT one alike)

| Case | Opened in | Window (owner, position, size in points, layer) |
|---|---|---|
| Anode Test Gain (VST3, its own NSView), floating panel at (120, 140) | 39–57 ms | the sandbox's pid, exactly (120, 140), 320 × 110 content + utility title bar (129–134), layer 3 (`kCGFloatingWindowLevel`), on screen |
| AUDelay (its own Cocoa view) | 188–203 ms | 484 × 255 content, layer 3 |
| AUReverb2 (no view of its own: Apple's `AUGenericView`) | 89–117 ms | 522 × 276 content, layer 3 |
| A normal window in sandbox B ordered over sandbox A's window (two processes, as Electron and a sandbox are) | — | B is **directly above** A in the window server's order (z 6 over 7), at A's top-left + (48, 72) as placed |
| Re-opening an open editor with a new position | — | the same window moves (no second window) |
| The sandbox crashes with an editor open | — | the window disappears with the process; the instance is bypassed and reported |
| Closing the editor from the host | — | gone from the screen |

"Opened in" is the whole request: host → sandbox over the control pipe, `createView`/`uiViewForAudioUnit`, attach, show.

## Design as built

- `IHostedPlugin.OpenEditor(open, EditorPlacement?)` on the instance the factory created. Default placement: a floating
  panel over the top-left corner of the **app's window**, which the sandbox finds as the largest on-screen normal-level
  window of `PluginHostOptions.AppProcessId` (default: the host's parent process, i.e. Electron's main process when the
  desktop app starts the host as a sidecar). `EditorPlacement(ParentWindow, X, Y, Floating)` overrides it: a window
  number to order above (or 0), a position, a normal window instead of a panel.
- The panel: `NSPanel`, utility style, `NSFloatingWindowLevel`, `hidesOnDeactivate = NO` (it stays while Electron is
  the active app), `becomesKeyOnlyIfNeeded`, full-screen auxiliary (it can join the app's full-screen space).
- VST3 views attach to an `NSView` container (`IPlugView::attached`, `kPlatformTypeNSView`) with an `IPlugFrame` that
  resizes the window; AU v2 views come from `kAudioUnitProperty_CocoaUI`, otherwise `AUGenericView`; CLAP through
  `clap.gui` (`CLAP_WINDOW_API_COCOA`, embedded).
- Parameter moves in an editor reach the host as `param` events (VST3 `performEdit` on the main thread; AU parameter
  listeners; CLAP output events), become the factory's `ParameterChanged(owner, parameterId, value)`, and are meant to
  become `ParameterChangedMessage`. Values the host sets are pushed to the editor about 30 times a second.
- The user closing the window reports `editorClosed`; the instance's `EditorWindow` returns to 0.

## Not measured, and open

- **Clicks and keyboard focus** in the plugin's window while Electron is the active app: synthesising input needs the
  Accessibility permission, which the tests must not prompt for. Expected (AppKit): the first click activates the
  sandbox (an accessory app may activate), so Anode's own keyboard shortcuts do not reach Electron until it is clicked
  again. Logic has the same split for AUv3 out-of-process views. To try by hand: open an editor and drag a slider.
- **Following the app's window** when it moves, minimises or changes Spaces: a top-level window in another process does
  not follow. The floating panel sidesteps most of it; a "follow" mode would need Electron's main process to forward
  `move`/`minimize` to the host (re-opening the editor re-places it), which needs a contract change (a placement on
  `openPluginEditor`) and was not done.
- The editor's own edit gestures were exercised only through code paths, not by a person: the test plug-in's sliders
  call `performEdit` exactly as VSTGUI editors do.
- **Windows and Linux:** not built (`an_plug_editor_open` fails there). The plan: Windows, a top-level window owned by
  Electron's HWND (`GWLP_HWNDPARENT` ownership works across processes, unlike macOS); Linux/X11, `WM_TRANSIENT_FOR`.
- The Electron app needs no change for any of this; `editor/apps/desktop` was left alone.

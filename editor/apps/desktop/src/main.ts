import {
  BrowserWindow,
  Menu,
  type MenuItemConstructorOptions,
  app,
  dialog,
  ipcMain,
  net,
  protocol,
  session,
  shell
} from 'electron';
import { existsSync, statSync } from 'node:fs';
import { extname, isAbsolute, join, normalize, resolve, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { networkStatus, newToken, portFree } from './network';
import { type AudioSettings, type NetworkSettings, type ServerTarget, loadSettings, saveSettings } from './settings';
import { type Connection, Sidecars, listDevices, waitForServer } from './sidecars';

// Anode desktop: the editor in a sandboxed window, and the engine (anode-host + anode-server) as supervised sidecars on
// loopback (and, when the user allows control from other devices, on the network too), or a remote Server
// (docs/01-implementation-plan.md §3, §6.5).

app.setName('Anode');
if (process.env['ANODE_USER_DATA']) {
  app.setPath('userData', process.env['ANODE_USER_DATA']); // tests run with throwaway settings
}

/**
 * One Anode at a time (per settings folder): a second would start a second engine on the same audio interface and a
 * second Server on the same projects. Launching it again brings the running one to the front instead.
 */
const primary = app.requestSingleInstanceLock();
if (!primary) {
  app.quit();
}

/** The renderer comes from the built editor through this scheme; nothing else is served from disk. */
const Scheme = 'app';
/** The app icon (apps/desktop/assets/icon.png), copied next to main.js by the build. */
const Icon = join(__dirname, 'icon.png');
const editorRoot = resolve(
  process.env['ANODE_EDITOR_DIR'] ?? join(__dirname, app.isPackaged ? 'editor' : '../editor/browser')
);
const devServer = process.env['ANODE_EDITOR_URL']; // `nx serve editor` during development
/** The same build for anode-server to serve on the network: it cannot read inside the asar archive. */
const editorFiles = editorRoot.replace(`${sep}app.asar${sep}`, `${sep}app.asar.unpacked${sep}`);

protocol.registerSchemesAsPrivileged([
  { scheme: Scheme, privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true } }
]);

/** How long to wait for a remote Server before the editor reports it unreachable. */
const RemoteTimeoutMs = 10_000;

let settings = loadSettings();
let sidecars: Sidecars | null = null;
let window: BrowserWindow | null = null;
let quitting = false;
/** Whether the window's editor answers "may the app quit?" (it says so once loaded; a reload starts over). */
let quitGuard = false;
/** The pending question, so a second ⌘Q or close while the editor asks does not ask twice. */
let askingToQuit: Promise<boolean> | null = null;
let quitQuestions = 0;
const quitAnswers = new Map<number, (ok: boolean) => void>();

/**
 * Asks the editor whether the app may quit: with unsaved edits it asks the person, since the open project lives in the
 * local Server and goes with it. A remote Server keeps it, so then nobody is asked; nor when
 * there is no editor to ask.
 */
function mayQuit(): Promise<boolean> {
  const contents = window?.webContents;
  if (!sidecars || !contents || contents.isDestroyed() || !quitGuard) {
    return Promise.resolve(true);
  }

  askingToQuit ??= new Promise<boolean>(done => {
    const id = ++quitQuestions;
    const gone = () => answer(true);
    const answer = (ok: boolean) => {
      quitAnswers.delete(id);
      contents.off('render-process-gone', gone);
      contents.off('destroyed', gone);
      done(ok);
    };
    quitAnswers.set(id, answer);
    contents.once('render-process-gone', gone);
    contents.once('destroyed', gone);
    contents.send('anode:mayQuit', id);
  }).finally(() => (askingToQuit = null));
  return askingToQuit;
}

function connection(): Promise<Connection> {
  const target = settings.target;
  if (target.mode === 'remote') {
    // A studio machine on the network answers within a few seconds or not at all: say so, and let the editor offer the way back.
    return waitForServer(
      target,
      RemoteTimeoutMs,
      `No answer from the Anode server at ${target.baseUrl}. Check that it is on and on this network.`
    ).then(() => ({ baseUrl: target.baseUrl, token: target.token }));
  }

  sidecars ??= new Sidecars(
    settings.audio,
    settings.network,
    editorFiles,
    process.env['ANODE_DATA_DIR'] ?? null,
    statuses => window?.webContents.send('anode:sidecars', statuses)
  );
  return sidecars.start();
}

/** Allows the renderer to reach exactly the Server it uses, and nothing else. */
function contentSecurityPolicy(): string {
  const remote = settings.target.mode === 'remote' ? new URL(settings.target.baseUrl) : null;
  const servers = remote
    ? `${remote.origin} ${remote.protocol === 'https:' ? 'wss' : 'ws'}://${remote.host}`
    : 'http://127.0.0.1:* ws://127.0.0.1:*';
  return [
    "default-src 'self'",
    "script-src 'self'",
    // Angular and the overlay CDK set inline styles.
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data: blob:",
    "font-src 'self' data:",
    `connect-src 'self' ${servers}`,
    "object-src 'none'",
    "base-uri 'self'",
    "frame-ancestors 'none'"
  ].join('; ');
}

function serveEditor(): void {
  protocol.handle(Scheme, async request => {
    const { pathname } = new URL(request.url);
    const file = normalize(join(editorRoot, decodeURIComponent(pathname)));
    // Refuse anything outside the editor build. Paths without an extension, like the root a reload asks for
    // (app://editor/#/mixer), get the app shell; a missing file is a 404, so a page older than the build fails to load a
    // chunk instead of being handed the shell as JavaScript.
    const inside = file.startsWith(editorRoot + sep);
    if (inside && extname(file) && !isFile(file)) {
      return new Response('Not found', { status: 404 });
    }

    const target = inside && isFile(file) ? file : join(editorRoot, 'index.html');
    const response = await net.fetch(pathToFileURL(target).toString());
    const headers = new Headers(response.headers);
    headers.set('Content-Security-Policy', contentSecurityPolicy());
    return new Response(response.body, { status: response.status, headers });
  });
}

function isFile(path: string): boolean {
  return statSync(path, { throwIfNoEntry: false })?.isFile() ?? false;
}

function createWindow(): void {
  window = new BrowserWindow({
    // The main window's areas (control bar, Library, inspector, Tracks area, editors) need room; smaller collapses them.
    width: 1600,
    height: 1000,
    minWidth: 1024,
    minHeight: 640,
    title: 'Anode',
    backgroundColor: '#111113',
    show: false,
    icon: Icon,
    // On macOS the editor's control bar is the title bar: no system bar, just the traffic lights, centred in its 48 px.
    ...(process.platform === 'darwin'
      ? { titleBarStyle: 'hidden' as const, trafficLightPosition: { x: 16, y: 17 } }
      : {}),
    webPreferences: {
      preload: join(__dirname, 'preload.js'),
      contextIsolation: true,
      sandbox: true,
      nodeIntegration: false,
      spellcheck: false
    }
  });

  window.once('ready-to-show', () => window?.show());
  // Closing the window quits on Windows and Linux: ask first, as quitting does (macOS keeps the app and its engine).
  let mayClose = false;
  window.on('close', event => {
    if (quitting || mayClose || process.platform === 'darwin' || !sidecars) {
      return;
    }

    event.preventDefault();
    void mayQuit().then(ok => {
      if (ok) {
        mayClose = true;
        window?.close();
      }
    });
  });
  window.webContents.on('did-navigate', () => (quitGuard = false));
  // Full screen hides the traffic lights, so the control bar can give their space back.
  window.on('enter-full-screen', () => window?.webContents.send('anode:fullScreen', true));
  window.on('leave-full-screen', () => window?.webContents.send('anode:fullScreen', false));
  // The window never navigates away from the editor; links to the web open in the browser.
  window.webContents.on('will-navigate', (event, url) => {
    if (!url.startsWith(`${Scheme}://`) && !(devServer && url.startsWith(devServer))) {
      event.preventDefault();
    }
  });
  window.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://')) {
      shell.openExternal(url).catch((error: unknown) => console.error('Anode: could not open', url, error));
    }

    return { action: 'deny' };
  });
  window.on('closed', () => (window = null));
  window
    .loadURL(devServer ?? `${Scheme}://editor/index.html`)
    .catch((error: unknown) => console.error('Anode: the editor did not load', error));
}

function handleIpc(): void {
  ipcMain.on('anode:mayQuit:ready', event => {
    if (event.sender === window?.webContents) {
      quitGuard = true;
    }
  });
  ipcMain.on('anode:mayQuit:reply', (_event, id: unknown, ok: unknown) => {
    if (typeof id === 'number') {
      quitAnswers.get(id)?.(ok === true);
    }
  });
  ipcMain.handle('anode:connection', () => connection());
  // Native file dialogs for the project chooser and Import. They take nothing from the renderer (titles, filters and
  // starting folders are main's), answer only the editor's own window, and return what the person picked.
  ipcMain.handle('anode:dialog:openProject', async event =>
    first(await pick(event.sender, { title: 'Open Project', properties: ['openDirectory'] }))
  );
  ipcMain.handle('anode:dialog:chooseFolder', async event =>
    first(
      await pick(event.sender, {
        title: 'Choose Where to Save the Project',
        buttonLabel: 'Choose',
        properties: ['openDirectory', 'createDirectory']
      })
    )
  );
  ipcMain.handle('anode:dialog:importAudio', async event =>
    pick(event.sender, {
      title: 'Import Audio',
      buttonLabel: 'Import',
      properties: ['openFile', 'multiSelections'],
      filters: [{ name: 'WAV audio', extensions: ['wav', 'wave'] }]
    })
  );
  ipcMain.handle('anode:target', () => settings.target);
  ipcMain.handle('anode:setTarget', async (_event, target: unknown) => {
    settings = { ...settings, target: validTarget(target) };
    saveSettings(settings);
    if (settings.target.mode === 'remote') {
      await sidecars?.stop();
      sidecars = null;
    }

    window?.reload();
  });
  ipcMain.handle('anode:sidecars', () => sidecars?.statuses() ?? []);
  ipcMain.handle('anode:sidecars:logs', () => sidecars?.logs() ?? { host: [], server: [] });
  ipcMain.handle('anode:audio:devices', () => listDevices());
  ipcMain.handle('anode:audio:settings', () => settings.audio);
  ipcMain.handle('anode:audio:apply', async (_event, audio: unknown) => {
    settings = { ...settings, audio: validAudio(audio) };
    saveSettings(settings);
    await sidecars?.applyAudio(settings.audio);
  });
  ipcMain.handle('anode:network:status', () => networkStatus(settings.network, sidecars?.networkError ?? null));
  ipcMain.handle('anode:network:apply', async (_event, network: unknown) => {
    const next = await validNetwork(network);
    settings = { ...settings, network: next };
    saveSettings(settings);
    // The Server restarts with the new listeners and token; the window reloads to take the token.
    if (sidecars && settings.target.mode === 'local') {
      await sidecars.applyNetwork(settings.network);
      window?.reload();
    }

    return networkStatus(settings.network, sidecars?.networkError ?? null);
  });
}

/**
 * Shows an open dialog over the editor's window and returns the absolute paths picked (none when cancelled). Refuses a
 * request from anything but that window's own page.
 */
async function pick(sender: Electron.WebContents, options: Electron.OpenDialogOptions): Promise<string[]> {
  const owner = sender === window?.webContents ? window : null;
  if (!owner) {
    throw new Error('File dialogs are for the Anode window only.');
  }

  const result = await dialog.showOpenDialog(owner, options);
  return result.canceled ? [] : result.filePaths.filter(path => isAbsolute(path));
}

function first(paths: string[]): string | null {
  return paths[0] ?? null;
}

/** What the renderer sent for a `T`: an object whose fields are all still to be checked. */
type Unchecked<T> = { readonly [K in keyof T]?: unknown };

/** The renderer is trusted less than main: everything it sends is checked, starting with its being an object. */
function fields(value: unknown): Record<string, unknown> {
  return typeof value === 'object' && value !== null ? (value as Record<string, unknown>) : {};
}

function isWholeIn(value: unknown, min: number, max: number): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= min && value <= max;
}

function validAudio(value: unknown): AudioSettings {
  const audio: Unchecked<AudioSettings> = fields(value);
  const oneOf = (option: unknown, options: readonly number[], fallback: number) =>
    options.find(o => o === option) ?? fallback;
  const device = typeof audio.device === 'string' && audio.device.length < 512 ? audio.device : null;
  const sampleRate = oneOf(audio.sampleRate, [44_100, 48_000, 88_200, 96_000, 176_400, 192_000], 48_000);
  const bufferFrames = oneOf(audio.bufferFrames, [32, 64, 128, 256, 512, 1024], 128);
  return { device, sampleRate, bufferFrames };
}

/** What the renderer asks for: on or off, the port, and optionally a new token. */
type NetworkRequest = Pick<NetworkSettings, 'enabled' | 'port'> & { renewToken?: boolean };

/**
 * A port of 1024 and up, and a token made the first time the setting goes on, or anew when asked (`renewToken`), which
 * signs every other device out. A port that is taken now is refused here, so the setting cannot be saved broken.
 */
async function validNetwork(value: unknown): Promise<NetworkSettings> {
  const network: Unchecked<NetworkRequest> = fields(value);
  const enabled = network.enabled === true;
  const port = isWholeIn(network.port, 1024, 65_535) ? network.port : null;
  if (port === null) {
    throw new Error('The port must be a whole number from 1024 to 65535.');
  }

  const current = settings.network;
  const listening = current.enabled && current.port === port && sidecars?.networkError === null;
  if (enabled && !listening && !(await portFree(port))) {
    throw new Error(`Port ${port} is in use by another program. Choose another port.`);
  }

  const token = network.renewToken === true || !current.token ? newToken() : current.token;
  return { enabled, port, token };
}

function validTarget(value: unknown): ServerTarget {
  const target: Unchecked<{ mode: string; baseUrl: string; token: string }> = fields(value);
  if (target.mode === 'remote') {
    const url = new URL(typeof target.baseUrl === 'string' ? target.baseUrl : '');
    if (url.protocol !== 'http:' && url.protocol !== 'https:') {
      throw new Error('The Server URL must be http or https.');
    }

    return { mode: 'remote', baseUrl: url.origin, token: typeof target.token === 'string' ? target.token : '' };
  }

  return { mode: 'local' };
}

/**
 * The application menu: Electron's default menus, with Settings… (⌘,) where a Mac app keeps it, in the app menu
 * (and in File elsewhere). It asks the editor to open its Settings dialog.
 */
function buildMenu(): void {
  const settings: MenuItemConstructorOptions = {
    label: 'Settings…',
    accelerator: 'CmdOrCtrl+,',
    click: () => window?.webContents.send('anode:openSettings')
  };
  // File menu items ask the editor (apps/editor/src/app/app.ts, AppCommand), which owns the project's state. ⌘S, ⌘Z and
  // ⇧⌘Z are the editor's own keys too, so the window handles them first and these accelerators only label the items.
  const command = (label: string, name: string, accelerator?: string): MenuItemConstructorOptions => ({
    label,
    ...(accelerator ? { accelerator } : {}),
    click: () => window?.webContents.send('anode:command', name)
  });
  const fileItems: MenuItemConstructorOptions[] = [
    command('New Project…', 'newProject', 'CmdOrCtrl+N'),
    command('Open Project…', 'openProject', 'CmdOrCtrl+O'),
    { type: 'separator' },
    command('Save', 'saveProject', 'CmdOrCtrl+S'),
    command('Close Project', 'closeProject', 'CmdOrCtrl+Shift+W'),
    { type: 'separator' },
    command('Import Audio…', 'importAudio', 'CmdOrCtrl+Shift+I')
  ];
  const template: MenuItemConstructorOptions[] =
    process.platform === 'darwin'
      ? [
          {
            label: 'Anode',
            submenu: [
              { role: 'about' },
              { type: 'separator' },
              settings,
              { type: 'separator' },
              { role: 'services' },
              { type: 'separator' },
              { role: 'hide' },
              { role: 'hideOthers' },
              { role: 'unhide' },
              { type: 'separator' },
              { role: 'quit' }
            ]
          },
          { label: 'File', submenu: [...fileItems, { type: 'separator' }, { role: 'close' }] },
          { role: 'editMenu' },
          { role: 'viewMenu' },
          { role: 'windowMenu' }
        ]
      : [
          {
            label: 'File',
            submenu: [...fileItems, { type: 'separator' }, settings, { type: 'separator' }, { role: 'quit' }]
          },
          { role: 'editMenu' },
          { role: 'viewMenu' },
          { role: 'windowMenu' }
        ];
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

app.on('second-instance', () => {
  if (window) {
    if (window.isMinimized()) {
      window.restore();
    }

    window.show();
    window.focus();
  }
});

app
  .whenReady()
  .then(() => {
    if (!primary) {
      return;
    }

    // Unpackaged, macOS would show Electron's own icon in the Dock.
    if (existsSync(Icon)) {
      app.dock?.setIcon(Icon);
    }

    // No camera, microphone, notifications or anything else: the editor needs none of it (audio input belongs to the
    // engine host, not the window). Writing the clipboard (Copy the access token) is the one exception.
    session.defaultSession.setPermissionRequestHandler((_contents, permission, callback) =>
      callback(permission === 'clipboard-sanitized-write')
    );
    serveEditor();
    handleIpc();
    buildMenu();
    createWindow();
    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) {
        createWindow();
      }
    });
  })
  .catch((error: unknown) => console.error('Anode: startup failed', error));

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') {
    app.quit();
  }
});

app.on('before-quit', event => {
  if (quitting || !sidecars) {
    return;
  }

  // Ask about unsaved edits; then stop the engine cleanly (the host releases the audio device and deletes its socket)
  // and really quit.
  event.preventDefault();
  void mayQuit().then(ok => {
    if (!ok || quitting) {
      return;
    }

    quitting = true;
    void (sidecars?.stop() ?? Promise.resolve()).finally(() => app.quit());
  });
});

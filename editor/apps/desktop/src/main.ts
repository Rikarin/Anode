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
import { writeFile } from 'node:fs/promises';
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
  const server = remote ? remote.origin : 'http://127.0.0.1:*';
  const servers = remote
    ? `${remote.origin} ${remote.protocol === 'https:' ? 'wss' : 'ws'}://${remote.host}`
    : 'http://127.0.0.1:* ws://127.0.0.1:*';
  return [
    "default-src 'self'",
    // Verovio (the Score Editor's engraver, ADR-0011) compiles its WebAssembly in a worker.
    "script-src 'self' 'wasm-unsafe-eval'",
    // Angular and the overlay CDK set inline styles.
    "style-src 'self' 'unsafe-inline'",
    // Phase 10: the movie's frames and thumbnails (<img>) and its stream (<video>) load from the Server themselves.
    `img-src 'self' data: blob: ${server}`,
    `media-src 'self' ${server}`,
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
  ipcMain.handle('anode:dialog:importMidi', async event =>
    first(
      await pick(event.sender, {
        title: 'Import MIDI File',
        buttonLabel: 'Import',
        properties: ['openFile'],
        filters: [{ name: 'Standard MIDI files', extensions: ['mid', 'midi', 'smf'] }]
      })
    )
  );
  // Phase 9's imports (docs/10-phase-9.md): a MusicXML or AAF file; Phase 10's: a movie or an ADM BWF. The kind is
  // checked against the ones main knows.
  ipcMain.handle('anode:dialog:importFile', async (event, kind: unknown) => {
    const filter = typeof kind === 'string' && Object.hasOwn(ImportFilters, kind) ? ImportFilters[kind] : undefined;
    if (!filter) {
      return null;
    }
    return first(
      await pick(event.sender, {
        title: filter.title,
        buttonLabel: 'Import',
        properties: ['openFile'],
        filters: [filter.filter]
      })
    );
  });
  // Prints the editor's print view (the Score Editor's pages, which the renderer lays out for print) to a PDF the person
  // names: the page size in millimetres and a default file name from the renderer, checked; answers the path, or null.
  ipcMain.handle('anode:printToPdf', async (event, request: unknown) => {
    const valid = validPrintRequest(request);
    const owner = BrowserWindow.fromWebContents(event.sender);
    if (!owner || owner !== window || !valid) {
      return null;
    }
    const target = await dialog.showSaveDialog(owner, {
      title: 'Save Score as PDF',
      defaultPath: `${valid.title}.pdf`,
      filters: [{ name: 'PDF', extensions: ['pdf'] }]
    });
    if (target.canceled || !target.filePath) {
      return null;
    }
    const pdf = await event.sender.printToPDF({
      pageSize: { width: valid.pageWidthMm / 25.4, height: valid.pageHeightMm / 25.4 },
      margins: { top: 0, bottom: 0, left: 0, right: 0 },
      printBackground: true,
      preferCSSPageSize: true,
      generateTaggedPDF: true
    });
    await writeFile(target.filePath, pdf);
    return target.filePath;
  });
  ipcMain.handle('anode:dialog:contentPack', async event =>
    first(
      await pick(event.sender, {
        title: 'Install Content Pack',
        buttonLabel: 'Install',
        properties: ['openFile'],
        filters: [{ name: 'Anode content packs', extensions: ['anodepack'] }]
      })
    )
  );
  // A file for an instrument to load (a sampler's SFZ or WAV): the renderer names the plugin's file types, which are
  // checked to be plain extensions before they become the dialog's filter.
  ipcMain.handle('anode:dialog:instrumentFile', async (event, fileTypes: unknown) =>
    first(
      await pick(event.sender, {
        title: 'Load Instrument File',
        buttonLabel: 'Load',
        properties: ['openFile'],
        filters: [{ name: 'Instrument files', extensions: validFileTypes(fileTypes) }]
      })
    )
  );
  // Shows a bounced file in Finder: only an existing audio file at an absolute path (what the local engine wrote), and
  // only for the editor's own window. Answers whether it could.
  ipcMain.handle('anode:reveal', (event, path: unknown) => {
    const file = revealablePath(path);
    if (event.sender !== window?.webContents || !file) {
      return false;
    }
    shell.showItemInFolder(file);
    return true;
  });
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
    if (sidecars && settings.target.mode === 'local') {
      await openProject(await connection()); // refuses before anything changes when there are unsaved edits
    }
    settings = { ...settings, network: next };
    saveSettings(settings);
    // The Server restarts with the new listeners and token; the window reloads to take the token. The open project
    // goes with the old Server, so it must be saved (refused otherwise) and is opened again on the new one.
    if (sidecars && settings.target.mode === 'local') {
      const open = await openProject(await connection());
      await sidecars.applyNetwork(settings.network);
      if (open) {
        await request(await connection(), 'POST', '/api/v1/projects/open', { path: open });
      }
      window?.reload();
    }

    return networkStatus(settings.network, sidecars?.networkError ?? null);
  });
}

/**
 * The path of the project the local Server has open, or null; throws when it has unsaved edits (the Server is about to
 * restart, and they would be lost).
 */
async function openProject(server: Connection): Promise<string | null> {
  const response = await fetch(`${server.baseUrl}/api/v1/project`, {
    headers: server.token ? { Authorization: `Bearer ${server.token}` } : {}
  });
  if (!response.ok) {
    return null; // none open
  }
  const project = (await response.json()) as { path: string; dirty: boolean };
  if (project.dirty) {
    throw new Error('Save the project first: turning network access on or off restarts the Server.');
  }
  return project.path;
}

/** A JSON request to the local Server; throws with its answer when it fails. */
async function request(server: Connection, method: string, path: string, body: unknown): Promise<void> {
  const response = await fetch(`${server.baseUrl}${path}`, {
    method,
    headers: {
      'Content-Type': 'application/json',
      ...(server.token ? { Authorization: `Bearer ${server.token}` } : {})
    },
    body: JSON.stringify(body)
  });
  if (!response.ok) {
    throw new Error(`${method} ${path}: ${response.status} ${await response.text()}`);
  }
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

/** File types as a plugin lists them (`.sfz`, `.wav`): 1–16 extensions of letters and digits, without the dot. */
function validFileTypes(value: unknown): string[] {
  if (!Array.isArray(value) || value.length === 0 || value.length > 16) {
    throw new Error('File types must be a list of 1 to 16 extensions.');
  }

  return value.map(type => {
    const match = typeof type === 'string' ? /^\.?([a-z0-9]{1,10})$/i.exec(type) : null;
    if (!match?.[1]) {
      throw new Error(`Not a file extension: ${String(type)}`);
    }
    return match[1].toLowerCase();
  });
}

/** A path the renderer may reveal: an absolute path to an existing WAV or AIFF file; null for anything else. */
/** The files the editor may show in Finder: bounces, and Phase 9's exports and printed scores. */
const RevealableExtensions = ['.wav', '.wave', '.aif', '.aiff', '.musicxml', '.mxl', '.aaf', '.mid', '.midi', '.pdf'];

/** The import dialogs' filters by kind (`anode:dialog:importFile`). */
const ImportFilters: Record<string, { title: string; filter: { name: string; extensions: string[] } } | undefined> = {
  musicxml: { title: 'Import MusicXML', filter: { name: 'MusicXML', extensions: ['musicxml', 'mxl', 'xml'] } },
  aaf: { title: 'Import AAF', filter: { name: 'AAF', extensions: ['aaf'] } },
  movie: { title: 'Open Movie', filter: { name: 'Movies', extensions: ['mov', 'mp4', 'm4v'] } },
  adm: { title: 'Import ADM BWF', filter: { name: 'ADM BWF', extensions: ['wav', 'bwf'] } }
};

/** A print request: a page of 50…2000 mm a side and a file name of at most 200 characters without folders. */
function validPrintRequest(value: unknown): { title: string; pageWidthMm: number; pageHeightMm: number } | null {
  if (typeof value !== 'object' || value === null) {
    return null;
  }
  const { title, pageWidthMm, pageHeightMm } = value as Record<string, unknown>;
  const size = (n: unknown): n is number => typeof n === 'number' && Number.isFinite(n) && n >= 50 && n <= 2000;
  if (
    typeof title !== 'string' ||
    title.length > 200 ||
    /[\\/\0]/.test(title) ||
    !size(pageWidthMm) ||
    !size(pageHeightMm)
  ) {
    return null;
  }
  return { title: title.trim() || 'Score', pageWidthMm, pageHeightMm };
}

function revealablePath(value: unknown): string | null {
  if (typeof value !== 'string' || value.length > 4096 || value.includes('\0') || !isAbsolute(value)) {
    return null;
  }
  const path = normalize(value);
  if (!RevealableExtensions.includes(extname(path).toLowerCase())) {
    return null;
  }
  try {
    return statSync(path).isFile() ? path : null;
  } catch {
    return null;
  }
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
  // Each item has an id (`anode:<name>`), so the e2e golden paths can choose it (Menu.getMenuItemById).
  const command = (label: string, name: string, accelerator?: string): MenuItemConstructorOptions => ({
    id: `anode:${name}`,
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
    command('Import Audio…', 'importAudio', 'CmdOrCtrl+Shift+I'),
    command('Import MIDI File…', 'importMidi'),
    // Phase 9 (docs/10-phase-9.md): interchange, through the editor's dialogs and the Server's jobs.
    {
      label: 'Import',
      submenu: [
        command('MusicXML…', 'importMusicXml'),
        command('AAF…', 'importAaf'),
        command('MIDI File (with options)…', 'importMidiOptions'),
        // Phase 10 (docs/11-phase-10.md): an ADM BWF's bed and objects as tracks.
        command('ADM BWF…', 'importAdm')
      ]
    },
    {
      label: 'Export',
      submenu: [
        command('Score as MusicXML…', 'exportMusicXml'),
        command('Project as AAF…', 'exportAaf'),
        command('Project as MIDI File…', 'exportMidi'),
        command('Project as ADM BWF…', 'exportAdm')
      ]
    },
    { type: 'separator' },
    command('Print Score…', 'printScore', 'CmdOrCtrl+P'),
    { type: 'separator' },
    command('Bounce Project or Section…', 'bounce', 'CmdOrCtrl+B'),
    // Phase 11 (docs/12-phase-11.md): the master's Mastering Assistant (analysis, character, loudness target).
    command('Mastering Assistant…', 'masteringAssistant'),
    command('I/O Labels…', 'ioLabels'),
    { type: 'separator' },
    command('Save as Alternative…', 'saveAlternative'),
    command('Key Commands…', 'keyCommands'),
    { type: 'separator' },
    // Phase 8 (docs/09-phase-8.md): the project's synchronisation, as Logic's File ▸ Project Settings has it.
    {
      label: 'Project Settings',
      submenu: [
        command('Synchronization…', 'syncSettings'),
        // Phase 10: the project's surround format, and the movie's position.
        command('Surround…', 'surroundSettings'),
        command('Movie…', 'movieSettings')
      ]
    }
  ];
  // Phase 10 (docs/11-phase-10.md): Logic's Movie menu (File ▸ Movie in Logic), through the editor's dialogs and jobs.
  const movieMenu: MenuItemConstructorOptions = {
    label: 'Movie',
    submenu: [
      command('Open Movie…', 'openMovie', 'CmdOrCtrl+Alt+O'),
      command('Remove Movie', 'removeMovie'),
      { type: 'separator' },
      command('Show Movie Window', 'movieWindow'),
      { type: 'separator' },
      command('Import Audio from Movie', 'importMovieAudio'),
      command('Export Audio to Movie…', 'exportMovieAudio'),
      { type: 'separator' },
      command('Detect Scene Cuts…', 'detectSceneCuts')
    ]
  };
  // Logic Pro ▸ Control Surfaces: the surfaces and controller assignments are the user's, not the project's.
  const controlSurfaces: MenuItemConstructorOptions = {
    label: 'Control Surfaces',
    submenu: [command('Setup…', 'controlSurfaces'), command('Controller Assignments…', 'controllerAssignments')]
  };
  // Window: Electron's window items, and Phase 11's MIDI Environment (Logic's Window ▸ Open MIDI Environment; ⌥⌘0, as
  // the View menu's Actual Size has ⌘0).
  const windowMenu: MenuItemConstructorOptions = {
    role: 'windowMenu',
    submenu: [
      { role: 'minimize' },
      { role: 'zoom' },
      { type: 'separator' },
      command('Open MIDI Environment', 'environment', 'CmdOrCtrl+Alt+0'),
      ...(process.platform === 'darwin'
        ? [{ type: 'separator' } as const, { role: 'front' } as const]
        : [{ role: 'close' } as const])
    ]
  };
  // Edit: the standard text roles (fields need them), then the Stem Splitter on the selected audio region.
  const editMenu: MenuItemConstructorOptions = {
    label: 'Edit',
    submenu: [
      { role: 'undo' },
      { role: 'redo' },
      { type: 'separator' },
      { role: 'cut' },
      { role: 'copy' },
      { role: 'paste' },
      { role: 'delete' },
      { role: 'selectAll' },
      { type: 'separator' },
      command('Stem Splitter…', 'stemSplitter')
    ]
  };
  const template: MenuItemConstructorOptions[] =
    process.platform === 'darwin'
      ? [
          {
            label: 'Anode',
            submenu: [
              { role: 'about' },
              { type: 'separator' },
              settings,
              controlSurfaces,
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
          editMenu,
          movieMenu,
          { role: 'viewMenu' },
          windowMenu
        ]
      : [
          {
            label: 'File',
            submenu: [
              ...fileItems,
              { type: 'separator' },
              settings,
              controlSurfaces,
              { type: 'separator' },
              { role: 'quit' }
            ]
          },
          editMenu,
          movieMenu,
          { role: 'viewMenu' },
          windowMenu
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

import { contextBridge, ipcRenderer, webUtils } from 'electron';

// The only surface the sandboxed renderer gets (typed in apps/editor/src/app/core/desktop.ts).
contextBridge.exposeInMainWorld('anode', {
  platform: process.platform,
  // A dropped File's path (Electron no longer puts it on File.path); '' for one that has none.
  pathForFile: (file: File) => {
    try {
      return webUtils.getPathForFile(file);
    } catch {
      return '';
    }
  },
  dialogs: {
    openProject: () => ipcRenderer.invoke('anode:dialog:openProject'),
    chooseFolder: () => ipcRenderer.invoke('anode:dialog:chooseFolder'),
    importAudio: () => ipcRenderer.invoke('anode:dialog:importAudio'),
    importMidi: () => ipcRenderer.invoke('anode:dialog:importMidi'),
    instrumentFile: (fileTypes: unknown) => ipcRenderer.invoke('anode:dialog:instrumentFile', fileTypes)
  },
  reveal: (path: unknown) => ipcRenderer.invoke('anode:reveal', path),
  onCommand: (listener: (command: unknown) => void) => {
    ipcRenderer.on('anode:command', (_event, command: unknown) => listener(command));
  },
  onFullScreen: (listener: (fullScreen: boolean) => void) => {
    ipcRenderer.on('anode:fullScreen', (_event, fullScreen: boolean) => listener(fullScreen));
  },
  onOpenSettings: (listener: () => void) => {
    ipcRenderer.on('anode:openSettings', () => listener());
  },
  onQuit: (mayQuit: () => boolean | Promise<boolean>) => {
    const answer = async (id: number) => {
      let ok = true;
      try {
        ok = await mayQuit();
      } catch {
        // an editor that cannot answer does not keep the app open
      }

      ipcRenderer.send('anode:mayQuit:reply', id, ok);
    };
    // answer() catches the editor's failure itself, so the promise never rejects.
    ipcRenderer.on('anode:mayQuit', (_event, id: number) => void answer(id));
    ipcRenderer.send('anode:mayQuit:ready');
  },
  connection: () => ipcRenderer.invoke('anode:connection'),
  target: () => ipcRenderer.invoke('anode:target'),
  setTarget: (target: unknown) => ipcRenderer.invoke('anode:setTarget', target),
  sidecars: () => ipcRenderer.invoke('anode:sidecars'),
  onSidecars: (listener: (statuses: unknown) => void) => {
    ipcRenderer.on('anode:sidecars', (_event, statuses: unknown) => listener(statuses));
  },
  logs: () => ipcRenderer.invoke('anode:sidecars:logs'),
  network: {
    status: () => ipcRenderer.invoke('anode:network:status'),
    apply: (network: unknown) => ipcRenderer.invoke('anode:network:apply', network)
  },
  audio: {
    devices: () => ipcRenderer.invoke('anode:audio:devices'),
    settings: () => ipcRenderer.invoke('anode:audio:settings'),
    apply: (settings: unknown) => ipcRenderer.invoke('anode:audio:apply', settings)
  }
});

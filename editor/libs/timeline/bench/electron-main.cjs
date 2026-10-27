// Electron entry for the S1 bench: one 1600 × 900 window, never throttled. run-bench.mts navigates it to each run.
const { app, BrowserWindow } = require('electron');

app.whenReady().then(() => {
  const window = new BrowserWindow({
    width: 1600,
    height: 900,
    useContentSize: true,
    webPreferences: { backgroundThrottling: false }
  });
  return window.loadURL('about:blank');
});

app.on('window-all-closed', () => app.quit());

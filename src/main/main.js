'use strict';

const path = require('path');
const fs = require('fs');
const {
  app, BrowserWindow, Tray, Menu, globalShortcut, ipcMain, screen,
  clipboard, nativeImage, dialog, shell, Notification, systemPreferences
} = require('electron');

const settings = require('./settings');
const capture = require('./capture');

const RENDERER = path.join(__dirname, '..', 'renderer');
const PRELOAD = path.join(__dirname, '..', 'preload');
const ASSETS = path.join(__dirname, '..', '..', 'assets');

let tray = null;
let settingsWindow = null;
let pickerWindow = null;
let recorderWindow = null;
let recordBarWindow = null;
let borderWindow = null;
let overlays = [];

/** Set while a region/window selection is in flight. */
let pendingSelection = null;
/** Set while a recording is running. */
let activeRecording = null;

const isMac = process.platform === 'darwin';

// ---------------------------------------------------------------- utilities

function notify(title, body, filePath) {
  if (!settings.get().showNotification || !Notification.isSupported()) return;
  const n = new Notification({ title, body });
  if (filePath) n.on('click', () => shell.showItemInFolder(filePath));
  n.show();
}

function fail(message) {
  console.error('[ScreenX]', message);
  dialog.showErrorBox('ScreenX', String(message));
}

/**
 * macOS gates every capture path behind Screen Recording permission and the
 * capturer silently returns black frames without it.
 */
async function ensureScreenAccess() {
  if (!isMac) return true;
  const status = systemPreferences.getMediaAccessStatus('screen');
  if (status === 'granted') return true;

  const { response } = await dialog.showMessageBox({
    type: 'warning',
    buttons: ['Open System Settings', 'Continue Anyway'],
    defaultId: 0,
    title: 'Screen Recording permission needed',
    message: 'ScreenX needs Screen Recording permission to capture your screen.',
    detail: 'Enable ScreenX under Privacy & Security > Screen Recording, then restart the app.'
  });
  if (response === 0) {
    shell.openExternal('x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture');
    return false;
  }
  return true;
}

/** Hide our own visible windows so they stay out of the capture. */
async function withWindowsHidden(fn) {
  const hidden = BrowserWindow.getAllWindows().filter((w) => w.isVisible() && !w.isDestroyed());
  hidden.forEach((w) => w.hide());
  if (hidden.length) await new Promise((r) => setTimeout(r, 180));
  try {
    return await fn();
  } finally {
    hidden.forEach((w) => !w.isDestroyed() && w.show());
  }
}

function featureEnabled(name) {
  return settings.get().features[name] !== false;
}

// ------------------------------------------------------------------ windows

function createWindow(file, options = {}) {
  const win = new BrowserWindow({
    show: false,
    backgroundColor: '#1e1e1e',
    ...options,
    webPreferences: {
      preload: path.join(PRELOAD, options.preload || 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
      backgroundThrottling: false,
      ...(options.webPreferences || {})
    }
  });
  win.loadFile(path.join(RENDERER, file));
  return win;
}

function openSettings() {
  if (settingsWindow && !settingsWindow.isDestroyed()) {
    settingsWindow.show();
    settingsWindow.focus();
    if (isMac) app.focus({ steal: true });
    return;
  }
  settingsWindow = createWindow('settings.html', {
    width: 860,
    height: 720,
    minWidth: 720,
    minHeight: 560,
    title: 'ScreenX Settings',
    preload: 'settings-preload.js'
  });
  settingsWindow.once('ready-to-show', () => {
    settingsWindow.show();
    if (isMac) app.focus({ steal: true });
  });
  settingsWindow.on('closed', () => { settingsWindow = null; });
}

function openEditor(image, meta = {}) {
  const size = image.getSize();
  const area = screen.getDisplayNearestPoint(screen.getCursorScreenPoint()).workAreaSize;
  const width = Math.min(Math.max(size.width + 80, 900), Math.round(area.width * 0.95));
  const height = Math.min(Math.max(size.height + 190, 640), Math.round(area.height * 0.95));

  const win = createWindow('editor.html', {
    width,
    height,
    minWidth: 720,
    minHeight: 520,
    title: 'ScreenX Editor',
    preload: 'editor-preload.js'
  });

  win.webContents.once('did-finish-load', () => {
    win.webContents.send('editor:load', {
      dataURL: image.toDataURL(),
      meta: { ...meta, width: size.width, height: size.height }
    });
    win.show();
    if (isMac) app.focus({ steal: true });
  });
  return win;
}

// ---------------------------------------------------------- result handling

function afterImage(image, meta = {}) {
  const config = settings.get();
  if (config.afterCapture === 'editor' && featureEnabled('editor')) {
    openEditor(image, meta);
    return;
  }
  if (config.afterCapture === 'copy') {
    clipboard.writeImage(image);
    notify('Copied to clipboard', `${image.getSize().width}x${image.getSize().height}`);
    return;
  }
  try {
    const saved = capture.saveImage(image, meta);
    if (config.afterCapture === 'saveCopy') clipboard.writeImage(image);
    if (config.copyPathAfterSave) clipboard.writeText(saved);
    notify('Screenshot saved', path.basename(saved), saved);
  } catch (err) {
    fail(`Could not save screenshot: ${err.message}`);
  }
}

// ------------------------------------------------------------ capture flows

async function captureFullscreen() {
  if (!featureEnabled('captureFullscreen') || !(await ensureScreenAccess())) return;
  try {
    const shots = await withWindowsHidden(() => capture.captureDisplays());
    const target = capture.displayUnderCursor();
    const shot = shots.find((s) => s.display.id === target.id) || shots[0];
    if (!shot || shot.image.isEmpty()) return fail('Screen capture returned an empty image.');
    afterImage(shot.image, { title: shot.name, kind: 'screen' });
  } catch (err) {
    fail(`Fullscreen capture failed: ${err.message}`);
  }
}

/** Freeze every display, then let the user drag a rectangle on the frozen copy. */
async function startRegionSelect(mode) {
  if (pendingSelection || activeRecording) return;
  if (!(await ensureScreenAccess())) return;

  let shots;
  try {
    shots = await withWindowsHidden(() => capture.captureDisplays());
  } catch (err) {
    return fail(`Screen capture failed: ${err.message}`);
  }
  if (!shots.length) return fail('No displays found.');

  pendingSelection = { mode, shots };
  overlays = shots.map(({ display, image }) => {
    const win = new BrowserWindow({
      x: display.bounds.x,
      y: display.bounds.y,
      width: display.bounds.width,
      height: display.bounds.height,
      frame: false,
      transparent: false,
      backgroundColor: '#000000',
      hasShadow: false,
      resizable: false,
      movable: false,
      minimizable: false,
      maximizable: false,
      fullscreenable: false,
      skipTaskbar: true,
      enableLargerThanScreen: true,
      show: false,
      ...(isMac ? { type: 'panel' } : {}),
      webPreferences: {
        preload: path.join(PRELOAD, 'overlay-preload.js'),
        contextIsolation: true,
        nodeIntegration: false,
        backgroundThrottling: false
      }
    });
    win.setAlwaysOnTop(true, 'screen-saver');
    win.setVisibleOnAllWorkspaces(true, { visibleOnFullScreenScreens: true });
    win.loadFile(path.join(RENDERER, 'overlay.html'));
    win.webContents.once('did-finish-load', () => {
      // JPEG keeps the preview payload small; the crop still comes from the
      // untouched original image held in the main process.
      win.webContents.send('overlay:init', {
        displayId: display.id,
        mode,
        dataURL: image.isEmpty()
          ? ''
          : `data:image/jpeg;base64,${image.toJPEG(92).toString('base64')}`,
        scaleFactor: display.scaleFactor,
        bounds: display.bounds
      });
      win.show();
      win.focus();
    });
    return win;
  });
  if (isMac) app.focus({ steal: true });
}

function closeOverlays() {
  overlays.forEach((w) => !w.isDestroyed() && w.close());
  overlays = [];
}

function onRegionSelected(displayId, rect) {
  const state = pendingSelection;
  closeOverlays();
  pendingSelection = null;
  if (!state) return;

  const shot = state.shots.find((s) => s.display.id === displayId);
  if (!shot) return fail('Selection landed on an unknown display.');

  if (state.mode === 'record') {
    startRecording({
      sourceId: shot.sourceId,
      display: shot.display,
      rect,
      title: shot.name
    });
    return;
  }

  const cropped = capture.cropToDisplayRect(shot.image, shot.display, rect);
  if (!cropped) return;
  afterImage(cropped, { title: shot.name, kind: 'region' });
}

function cancelSelection() {
  closeOverlays();
  pendingSelection = null;
}

async function startWindowPick(mode) {
  if (pendingSelection || activeRecording) return;
  if (!(await ensureScreenAccess())) return;

  let windows;
  try {
    windows = await withWindowsHidden(() => capture.listWindows());
  } catch (err) {
    return fail(`Could not list windows: ${err.message}`);
  }
  if (!windows.length) return fail('No capturable windows found.');

  pendingSelection = { mode, windows };

  const cursor = screen.getDisplayNearestPoint(screen.getCursorScreenPoint()).workAreaSize;
  pickerWindow = createWindow('picker.html', {
    width: Math.min(1000, Math.round(cursor.width * 0.9)),
    height: Math.min(700, Math.round(cursor.height * 0.85)),
    title: mode === 'record' ? 'Record which window?' : 'Capture which window?',
    preload: 'picker-preload.js'
  });
  pickerWindow.webContents.once('did-finish-load', () => {
    pickerWindow.webContents.send('picker:init', {
      mode,
      windows: windows.map((w) => ({
        id: w.id,
        name: w.name,
        appIcon: w.appIcon,
        thumbnail: `data:image/jpeg;base64,${w.thumbnail.toJPEG(80).toString('base64')}`
      }))
    });
    pickerWindow.show();
    if (isMac) app.focus({ steal: true });
  });
  pickerWindow.on('closed', () => {
    pickerWindow = null;
    if (pendingSelection && pendingSelection.windows) pendingSelection = null;
  });
}

function onWindowPicked(sourceId) {
  const state = pendingSelection;
  pendingSelection = null;
  if (pickerWindow && !pickerWindow.isDestroyed()) pickerWindow.close();
  if (!state) return;

  const chosen = state.windows.find((w) => w.id === sourceId);
  if (!chosen) return;

  if (state.mode === 'record') {
    startRecording({ sourceId: chosen.id, rect: null, title: chosen.name });
    return;
  }
  afterImage(chosen.thumbnail, { title: chosen.name, kind: 'window' });
}

// ---------------------------------------------------------------- recording

function ensureRecorder() {
  if (recorderWindow && !recorderWindow.isDestroyed()) return recorderWindow;
  recorderWindow = new BrowserWindow({
    show: false,
    width: 480,
    height: 320,
    webPreferences: {
      preload: path.join(PRELOAD, 'recorder-preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
      backgroundThrottling: false
    }
  });
  recorderWindow.loadFile(path.join(RENDERER, 'recorder.html'));
  return recorderWindow;
}

function showRecordingChrome(bounds) {
  const area = screen.getDisplayNearestPoint(
    bounds ? { x: bounds.x, y: bounds.y } : screen.getCursorScreenPoint()
  ).workArea;

  if (bounds) {
    borderWindow = new BrowserWindow({
      x: bounds.x - 2,
      y: bounds.y - 2,
      width: bounds.width + 4,
      height: bounds.height + 4,
      frame: false,
      transparent: true,
      backgroundColor: '#00000000',
      hasShadow: false,
      resizable: false,
      movable: false,
      focusable: false,
      skipTaskbar: true,
      show: false,
      ...(isMac ? { type: 'panel' } : {}),
      webPreferences: { contextIsolation: true, nodeIntegration: false }
    });
    borderWindow.setIgnoreMouseEvents(true);
    borderWindow.setAlwaysOnTop(true, 'screen-saver');
    borderWindow.loadFile(path.join(RENDERER, 'border.html'));
    borderWindow.once('ready-to-show', () => borderWindow.showInactive());
  }

  const barWidth = 300;
  const barHeight = 56;
  const x = bounds
    ? Math.min(Math.max(bounds.x, area.x), area.x + area.width - barWidth)
    : Math.round(area.x + (area.width - barWidth) / 2);
  const y = bounds
    ? Math.min(bounds.y + bounds.height + 8, area.y + area.height - barHeight)
    : area.y + area.height - barHeight - 24;

  recordBarWindow = new BrowserWindow({
    x, y, width: barWidth, height: barHeight,
    frame: false,
    transparent: true,
    backgroundColor: '#00000000',
    hasShadow: false,
    resizable: false,
    skipTaskbar: true,
    alwaysOnTop: true,
    show: false,
    webPreferences: {
      preload: path.join(PRELOAD, 'recordbar-preload.js'),
      contextIsolation: true,
      nodeIntegration: false
    }
  });
  recordBarWindow.setAlwaysOnTop(true, 'screen-saver');
  recordBarWindow.once('ready-to-show', () => recordBarWindow.showInactive());
  recordBarWindow.loadFile(path.join(RENDERER, 'recordbar.html'));
}

function closeRecordingChrome() {
  [borderWindow, recordBarWindow].forEach((w) => w && !w.isDestroyed() && w.close());
  borderWindow = null;
  recordBarWindow = null;
}

function startRecording({ sourceId, display, rect, title }) {
  if (!sourceId) return fail('That source cannot be recorded.');
  if (activeRecording) return;

  const config = settings.get();
  const scale = display ? display.scaleFactor : 1;
  // The renderer works in source pixels; region rects arrive in DIP.
  const crop = rect
    ? {
      x: Math.round(rect.x * scale),
      y: Math.round(rect.y * scale),
      width: Math.round(rect.width * scale),
      height: Math.round(rect.height * scale)
    }
    : null;

  const bounds = rect && display
    ? {
      x: display.bounds.x + Math.round(rect.x),
      y: display.bounds.y + Math.round(rect.y),
      width: Math.round(rect.width),
      height: Math.round(rect.height)
    }
    : null;

  activeRecording = { title, startedAt: Date.now() };
  showRecordingChrome(bounds);
  refreshTray();

  const win = ensureRecorder();
  const send = () => win.webContents.send('recorder:start', {
    sourceId,
    crop,
    fps: config.gif.fps,
    maxSeconds: config.gif.maxSeconds,
    maxWidth: config.gif.maxWidth,
    repeat: config.gif.repeat,
    // A GIF of a Retina region does not need the doubled pixels: encoding at
    // the size the user actually dragged is four times less work per frame.
    outputWidth: rect ? Math.round(rect.width) : 0,
    sourceSize: display
      ? { width: Math.round(display.size.width * scale), height: Math.round(display.size.height * scale) }
      : null
  });

  if (win.webContents.isLoading()) win.webContents.once('did-finish-load', send);
  else send();

  const stopKey = config.hotkeys.stopRecording;
  if (stopKey) {
    try { globalShortcut.register(stopKey, stopRecording); } catch { /* combo unavailable */ }
  }
}

function stopRecording() {
  if (!activeRecording || activeRecording.stopping) return;
  activeRecording.stopping = true;
  if (recordBarWindow && !recordBarWindow.isDestroyed()) {
    recordBarWindow.webContents.send('recordbar:encoding');
  }
  if (borderWindow && !borderWindow.isDestroyed()) borderWindow.close();
  borderWindow = null;
  if (recorderWindow && !recorderWindow.isDestroyed()) recorderWindow.webContents.send('recorder:stop');
}

function cancelRecording() {
  if (!activeRecording) return;
  activeRecording.cancelled = true;
  if (recorderWindow && !recorderWindow.isDestroyed()) recorderWindow.webContents.send('recorder:cancel');
  finishRecording();
}

function finishRecording() {
  const stopKey = settings.get().hotkeys.stopRecording;
  if (stopKey) globalShortcut.unregister(stopKey);
  closeRecordingChrome();
  activeRecording = null;
  refreshTray();
}

// ------------------------------------------------------------------ actions

const ACTIONS = {
  captureFullscreen: { label: 'Capture Entire Screen', run: captureFullscreen },
  captureRegion: { label: 'Capture Region', run: () => startRegionSelect('screenshot') },
  captureWindow: { label: 'Capture Window', run: () => startWindowPick('screenshot') },
  recordRegion: { label: 'Record Region as GIF', run: () => startRegionSelect('record') },
  recordWindow: { label: 'Record Window as GIF', run: () => startWindowPick('record') }
};

function runAction(name) {
  if (!featureEnabled(name)) return;
  const action = ACTIONS[name];
  if (action) Promise.resolve(action.run()).catch((err) => fail(err.message));
}

function registerHotkeys() {
  globalShortcut.unregisterAll();
  const config = settings.get();
  const conflicts = [];
  for (const [name, accelerator] of Object.entries(config.hotkeys)) {
    if (name === 'stopRecording' || !accelerator || !featureEnabled(name)) continue;
    try {
      if (!globalShortcut.register(accelerator, () => runAction(name))) conflicts.push(accelerator);
    } catch {
      conflicts.push(accelerator);
    }
  }
  return conflicts;
}

// --------------------------------------------------------------------- tray

function trayIcon() {
  const file = path.join(ASSETS, isMac ? 'trayTemplate.png' : 'tray.png');
  const image = fs.existsSync(file) ? nativeImage.createFromPath(file) : nativeImage.createEmpty();
  if (isMac) image.setTemplateImage(true);
  return image;
}

function buildTrayMenu() {
  const config = settings.get();
  const items = Object.entries(ACTIONS)
    .filter(([name]) => featureEnabled(name))
    .map(([name, action]) => ({
      label: action.label,
      accelerator: config.hotkeys[name] || undefined,
      registerAccelerator: false,
      enabled: !activeRecording,
      click: () => runAction(name)
    }));

  return Menu.buildFromTemplate([
    ...items,
    { type: 'separator' },
    ...(activeRecording
      ? [{ label: 'Stop Recording', click: stopRecording },
        { label: 'Cancel Recording', click: cancelRecording },
        { type: 'separator' }]
      : []),
    { label: 'Open Screenshots Folder', click: () => shell.openPath(capture.resolveFolder(config.screenshotFolder, 'Screenshots')) },
    { label: 'Open Recordings Folder', click: () => shell.openPath(capture.resolveFolder(config.gifFolder, 'Recordings')) },
    { type: 'separator' },
    { label: 'Settings...', accelerator: 'CommandOrControl+,', registerAccelerator: false, click: openSettings },
    { label: 'Quit ScreenX', click: () => { app.isQuitting = true; app.quit(); } }
  ]);
}

function refreshTray() {
  if (!tray) return;
  tray.setContextMenu(buildTrayMenu());
  tray.setToolTip(activeRecording ? 'ScreenX - recording' : 'ScreenX');
}

function createTray() {
  tray = new Tray(trayIcon());
  refreshTray();
  tray.on('click', () => tray.popUpContextMenu());
}

function buildAppMenu() {
  // Tray-only app, but macOS still routes clipboard/undo shortcuts through the
  // application menu, so the editor needs these roles to exist.
  const template = [
    ...(isMac ? [{ role: 'appMenu' }] : []),
    {
      label: 'Edit',
      submenu: [
        { role: 'undo' }, { role: 'redo' }, { type: 'separator' },
        { role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { role: 'selectAll' }
      ]
    },
    { label: 'Window', submenu: [{ role: 'minimize' }, { role: 'close' }] }
  ];
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

// ---------------------------------------------------------------------- IPC

function registerIpc() {
  ipcMain.handle('settings:get', () => settings.get());

  ipcMain.handle('settings:save', (_event, patch) => {
    const next = settings.save(patch);
    const conflicts = registerHotkeys();
    refreshTray();
    app.setLoginItemSettings({ openAtLogin: !!next.launchAtLogin, openAsHidden: true });
    return { settings: next, conflicts };
  });

  ipcMain.handle('settings:defaults', () => settings.defaults());

  ipcMain.handle('settings:pickFolder', async (event, current) => {
    const win = BrowserWindow.fromWebContents(event.sender);
    const result = await dialog.showOpenDialog(win, {
      properties: ['openDirectory', 'createDirectory'],
      defaultPath: current || app.getPath('pictures')
    });
    return result.canceled ? null : result.filePaths[0];
  });

  ipcMain.handle('settings:preview', (_event, { pattern, kind }) => {
    const { parseName } = require('./naming');
    return parseName(pattern, {
      ...capture.buildContext({ title: 'Example Window', width: 1920, height: 1080 }),
      counter: settings.get().autoIncrementNumber + 1
    }) + (kind === 'gif' ? '.gif' : `.${settings.get().imageFormat}`);
  });

  ipcMain.on('settings:openFolder', (_event, folder) => { if (folder) shell.openPath(folder); });

  ipcMain.on('overlay:select', (_event, { displayId, rect }) => onRegionSelected(displayId, rect));
  ipcMain.on('overlay:cancel', cancelSelection);

  ipcMain.on('picker:select', (_event, id) => onWindowPicked(id));
  ipcMain.on('picker:cancel', () => {
    pendingSelection = null;
    if (pickerWindow && !pickerWindow.isDestroyed()) pickerWindow.close();
  });

  ipcMain.on('recordbar:stop', stopRecording);
  ipcMain.on('recordbar:cancel', cancelRecording);

  ipcMain.on('recorder:progress', (_event, info) => {
    if (recordBarWindow && !recordBarWindow.isDestroyed()) {
      recordBarWindow.webContents.send('recordbar:progress', info);
    }
  });

  ipcMain.on('recorder:done', (_event, { bytes, width, height }) => {
    const meta = { title: activeRecording ? activeRecording.title : '', width, height, kind: 'gif' };
    finishRecording();
    if (!bytes || !bytes.byteLength) return;
    try {
      const saved = capture.saveGif(Buffer.from(bytes), meta);
      if (settings.get().copyPathAfterSave) clipboard.writeText(saved);
      notify('GIF saved', path.basename(saved), saved);
    } catch (err) {
      fail(`Could not save GIF: ${err.message}`);
    }
  });

  ipcMain.on('recorder:error', (_event, message) => {
    finishRecording();
    fail(`Recording failed: ${message}`);
  });

  ipcMain.handle('editor:save', (event, { dataURL, meta }) => {
    const image = nativeImage.createFromDataURL(dataURL);
    try {
      const saved = capture.saveImage(image, meta || {});
      if (settings.get().copyPathAfterSave) clipboard.writeText(saved);
      notify('Screenshot saved', path.basename(saved), saved);
      return saved;
    } catch (err) {
      fail(`Could not save: ${err.message}`);
      return null;
    }
  });

  ipcMain.handle('editor:saveAs', async (event, { dataURL, meta }) => {
    const win = BrowserWindow.fromWebContents(event.sender);
    const config = settings.get();
    const { parseName } = require('./naming');
    const suggested = parseName(config.screenshotNamePattern, capture.buildContext(meta || {}));
    const { canceled, filePath } = await dialog.showSaveDialog(win, {
      defaultPath: path.join(capture.resolveFolder(config.screenshotFolder, 'Screenshots'),
        `${suggested}.${config.imageFormat}`),
      filters: [{ name: 'Images', extensions: ['png', 'jpg'] }]
    });
    if (canceled || !filePath) return null;
    const image = nativeImage.createFromDataURL(dataURL);
    const buffer = path.extname(filePath).toLowerCase() === '.jpg'
      ? image.toJPEG(config.jpegQuality)
      : image.toPNG();
    fs.writeFileSync(filePath, buffer);
    notify('Screenshot saved', path.basename(filePath), filePath);
    return filePath;
  });

  ipcMain.on('editor:copy', (_event, dataURL) => {
    clipboard.writeImage(nativeImage.createFromDataURL(dataURL));
    notify('Copied to clipboard', 'Image is on the clipboard');
  });

  ipcMain.on('window:close', (event) => {
    const win = BrowserWindow.fromWebContents(event.sender);
    if (win && !win.isDestroyed()) win.close();
  });

  ipcMain.on('shell:reveal', (_event, target) => target && shell.showItemInFolder(target));
}

// --------------------------------------------------------------- lifecycle

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', openSettings);

  app.whenReady().then(() => {
    settings.init(app.getPath('userData'));
    if (isMac && app.dock) app.dock.hide();

    buildAppMenu();
    registerIpc();
    createTray();
    const conflicts = registerHotkeys();
    if (conflicts.length) {
      notify('Some hotkeys are unavailable', `${conflicts.join(', ')} — already taken by another app.`);
    }

    if (!fs.existsSync(path.join(app.getPath('userData'), 'settings.json'))) {
      settings.save({});
      openSettings();
    }
  });

  // Tray app: closing the last window must not quit.
  app.on('window-all-closed', () => {});
  app.on('will-quit', () => globalShortcut.unregisterAll());
}

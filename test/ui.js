'use strict';

// Loads every renderer with its real preload, drives it with synthetic events
// and fails on any console error or uncaught exception.
// Run with: npm run test:ui

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { app, BrowserWindow, ipcMain, nativeImage } = require('electron');

const settings = require('../src/main/settings');

const SRC = path.join(__dirname, '..', 'src');
const workdir = fs.mkdtempSync(path.join(os.tmpdir(), 'screenx-ui-'));

const problems = [];

function step(name) { process.stdout.write(`  ${name}... `); }
function ok(extra) { console.log(`ok${extra ? ` (${extra})` : ''}`); }

/** A 200x150 test image: red left half, blue right half. */
function testImage() {
  const width = 200;
  const height = 150;
  const pixels = Buffer.alloc(width * height * 4);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const i = (y * width + x) * 4;
      // nativeImage buffers are BGRA.
      pixels[i] = x < width / 2 ? 0 : 255;
      pixels[i + 1] = 0;
      pixels[i + 2] = x < width / 2 ? 255 : 0;
      pixels[i + 3] = 255;
    }
  }
  return nativeImage.createFromBuffer(pixels, { width, height });
}

function open(file, preload, options = {}) {
  const win = new BrowserWindow({
    show: false,
    width: options.width || 1000,
    height: options.height || 800,
    webPreferences: {
      preload: path.join(SRC, 'preload', preload),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false
    }
  });
  win.webContents.on('console-message', (event) => {
    const level = event.level ?? event.levelName;
    const text = String(event.message ?? '');
    if ((level === 'error' || level === 3) && !text.includes('Security Warning')) {
      problems.push(`${file}: ${text}`);
    }
  });
  win.webContents.on('render-process-gone', (_e, details) => {
    problems.push(`${file}: renderer gone (${details.reason})`);
  });
  return new Promise((resolve) => {
    win.webContents.once('did-finish-load', () => resolve(win));
    win.loadFile(path.join(SRC, 'renderer', file));
  });
}

/** Fire a real MouseEvent so the renderer's own listeners run unchanged. */
function mouse(win, type, x, y, extra = '') {
  return win.webContents.executeJavaScript(`
    (() => {
      const target = document.elementFromPoint(${x}, ${y}) || document.body;
      target.dispatchEvent(new MouseEvent('${type}', {
        clientX: ${x}, clientY: ${y}, button: 0, buttons: 1, bubbles: true, cancelable: true ${extra}
      }));
      return true;
    })()
  `);
}

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

/** Turn a missing IPC reply into a readable failure instead of a hang. */
function expect(channel, ms = 5000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      ipcMain.removeListener(channel, handler);
      reject(new Error(`no "${channel}" message within ${ms}ms`));
    }, ms);
    const handler = (_event, payload) => { clearTimeout(timer); resolve(payload); };
    ipcMain.once(channel, handler);
  });
}

// ---------------------------------------------------------------- overlay

async function testOverlay() {
  step('overlay drag selects a rectangle');
  const win = await open('overlay.html', 'overlay-preload.js', { width: 600, height: 400 });

  const selected = expect('overlay:select');
  win.webContents.send('overlay:init', {
    displayId: 7,
    mode: 'screenshot',
    dataURL: testImage().toDataURL(),
    scaleFactor: 2,
    bounds: { x: 0, y: 0, width: 600, height: 400 }
  });
  await wait(250);

  await mouse(win, 'mousedown', 100, 80);
  await mouse(win, 'mousemove', 260, 230);
  await mouse(win, 'mouseup', 260, 230);

  const payload = await selected;
  assert.strictEqual(payload.displayId, 7);
  assert.deepStrictEqual(payload.rect, { x: 100, y: 80, width: 160, height: 150 });
  win.destroy();
  ok('160x150 at 100,80');

  step('overlay ignores a stray click');
  const win2 = await open('overlay.html', 'overlay-preload.js', { width: 600, height: 400 });
  const cancelled = expect('overlay:cancel');
  win2.webContents.send('overlay:init', { displayId: 7, mode: 'record', dataURL: '', scaleFactor: 1, bounds: {} });
  await wait(150);
  await mouse(win2, 'mousedown', 300, 200);
  await mouse(win2, 'mouseup', 301, 200);
  await cancelled;
  win2.destroy();
  ok();
}

// ----------------------------------------------------------------- picker

async function testPicker() {
  step('picker returns the clicked window');
  const win = await open('picker.html', 'picker-preload.js');
  const thumbnail = testImage().toDataURL();
  win.webContents.send('picker:init', {
    mode: 'screenshot',
    windows: [
      { id: 'window:1', name: 'Alpha', appIcon: null, thumbnail },
      { id: 'window:2', name: 'Beta', appIcon: null, thumbnail }
    ]
  });
  await wait(200);

  const count = await win.webContents.executeJavaScript('document.querySelectorAll(".card").length');
  assert.strictEqual(count, 2, `expected 2 cards, got ${count}`);

  // Filtering must narrow the list and keep Enter pointed at the survivor.
  const chosen = expect('picker:select');
  await win.webContents.executeJavaScript(`
    const f = document.getElementById('filter');
    f.value = 'bet';
    f.dispatchEvent(new Event('input', { bubbles: true }));
    document.querySelectorAll('.card').length
  `);
  await win.webContents.executeJavaScript(`
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); true
  `);
  assert.strictEqual(await chosen, 'window:2');
  win.destroy();
  ok('filter + Enter');
}

// ----------------------------------------------------------------- editor

async function testEditor() {
  step('editor draws, undoes and saves');
  const win = await open('editor.html', 'editor-preload.js', { width: 900, height: 700 });

  win.webContents.send('editor:load', { dataURL: testImage().toDataURL(), meta: { title: 'ui-test' } });
  await wait(300);

  const box = await win.webContents.executeJavaScript(`
    (() => { const r = document.getElementById('stage').getBoundingClientRect();
      return { x: r.left, y: r.top, w: r.width, h: r.height }; })()
  `);
  assert.ok(box.w > 0, 'canvas has no size');

  await win.webContents.executeJavaScript(`
    document.querySelector('[data-tool="rect"]').click(); true
  `);
  await mouse(win, 'mousedown', Math.round(box.x + 20), Math.round(box.y + 20));
  await mouse(win, 'mousemove', Math.round(box.x + 90), Math.round(box.y + 70));
  await mouse(win, 'mouseup', Math.round(box.x + 90), Math.round(box.y + 70));

  let shapes = await win.webContents.executeJavaScript('shapes.length');
  assert.strictEqual(shapes, 1, 'rectangle was not recorded');

  await win.webContents.executeJavaScript('document.getElementById("undo").click(); shapes.length');
  shapes = await win.webContents.executeJavaScript('shapes.length');
  assert.strictEqual(shapes, 0, 'undo did not remove the rectangle');
  await win.webContents.executeJavaScript('document.getElementById("redo").click(); true');
  shapes = await win.webContents.executeJavaScript('shapes.length');
  assert.strictEqual(shapes, 1, 'redo did not restore the rectangle');

  // Text, step and pixelate must all survive a render pass.
  await win.webContents.executeJavaScript(`
    shapes.push({ type: 'text', x: 10, y: 10, text: 'hi\\nthere', stroke: '#fff', fontSize: 20, lineWidth: 3 });
    shapes.push({ type: 'step', x: 60, y: 60, radius: 14, number: 1, stroke: '#f00', lineWidth: 3 });
    shapes.push({ type: 'pixelate', x1: 10, y1: 90, x2: 120, y2: 140, stroke: '#000', lineWidth: 3 });
    shapes.push({ type: 'arrow', x1: 5, y1: 5, x2: 80, y2: 40, stroke: '#0f0', lineWidth: 4 });
    shapes.push({ type: 'highlight', x1: 120, y1: 10, x2: 190, y2: 40, stroke: '#ff0', lineWidth: 3 });
    shapes.push({ type: 'pen', points: [{x:10,y:10},{x:40,y:30},{x:70,y:20}], stroke: '#00f', lineWidth: 5 });
    render();
    shapes.length
  `);

  const saved = new Promise((resolve) => {
    ipcMain.handleOnce('editor:save', (_e, payload) => { resolve(payload); return '/tmp/fake.png'; });
  });
  await win.webContents.executeJavaScript('document.getElementById("save").click(); true');
  const payload = await saved;
  const image = nativeImage.createFromDataURL(payload.dataURL);
  assert.deepStrictEqual(image.getSize(), { width: 200, height: 150 }, 'saved image has the wrong size');
  assert.strictEqual(payload.meta.title, 'ui-test');
  win.destroy();
  ok('7 shapes, 200x150 png');

  step('editor crop resizes the canvas');
  const win2 = await open('editor.html', 'editor-preload.js', { width: 900, height: 700 });
  win2.webContents.send('editor:load', { dataURL: testImage().toDataURL(), meta: {} });
  await wait(300);
  await win2.webContents.executeJavaScript(`
    fitToWindow = false; applyScale();
    document.querySelector('[data-tool="crop"]').click(); true
  `);
  const box2 = await win2.webContents.executeJavaScript(`
    (() => { const r = document.getElementById('stage').getBoundingClientRect();
      return { x: r.left, y: r.top }; })()
  `);
  await mouse(win2, 'mousedown', Math.round(box2.x + 20), Math.round(box2.y + 10));
  await mouse(win2, 'mousemove', Math.round(box2.x + 120), Math.round(box2.y + 90));
  await mouse(win2, 'mouseup', Math.round(box2.x + 120), Math.round(box2.y + 90));
  await wait(100);
  const size = await win2.webContents.executeJavaScript('({ width: base.width, height: base.height })');
  assert.deepStrictEqual(size, { width: 100, height: 80 }, `crop produced ${size.width}x${size.height}`);
  win2.destroy();
  ok('200x150 -> 100x80');
}

// --------------------------------------------------------------- settings

async function testSettings() {
  step('settings load, preview and save');
  settings.init(workdir);

  ipcMain.handle('settings:get', () => settings.get());
  ipcMain.handle('settings:defaults', () => settings.defaults());
  ipcMain.handle('settings:save', (_e, patch) => ({ settings: settings.save(patch), conflicts: [] }));
  ipcMain.handle('settings:pickFolder', () => null);
  ipcMain.handle('settings:preview', (_e, { pattern, kind }) => {
    const { parseName } = require('../src/main/naming');
    return `${parseName(pattern, { counter: 1 })}.${kind === 'gif' ? 'gif' : 'png'}`;
  });

  const win = await open('settings.html', 'settings-preload.js');
  await wait(400);

  const preview = await win.webContents.executeJavaScript('document.getElementById("screenshotPreview").textContent');
  assert.ok(preview.startsWith('Example: ScreenX_'), `unexpected preview: ${preview}`);

  const rows = await win.webContents.executeJavaScript('document.querySelectorAll("[data-hotkey]").length');
  assert.strictEqual(rows, 6, `expected 6 hotkey rows, got ${rows}`);

  // Every tab must render without throwing.
  await win.webContents.executeJavaScript(`
    document.querySelectorAll('nav button').forEach(b => b.click()); true
  `);

  await win.webContents.executeJavaScript(`
    document.getElementById('gifFps').value = 20;
    document.getElementById('screenshotNamePattern').value = 'shot_%i{5}';
    document.querySelector('[data-feature="captureWindow"]').checked = false;
    document.getElementById('save').click(); true
  `);
  await wait(300);

  const stored = settings.get();
  assert.strictEqual(stored.gif.fps, 20);
  assert.strictEqual(stored.screenshotNamePattern, 'shot_%i{5}');
  assert.strictEqual(stored.features.captureWindow, false);
  assert.ok(fs.existsSync(path.join(workdir, 'settings.json')), 'settings file not written');
  win.destroy();
  ok('round-trip through disk');

  step('recording bar shows elapsed time');
  const bar = await open('recordbar.html', 'recordbar-preload.js', { width: 300, height: 56 });
  bar.webContents.send('recordbar:progress', { ms: 65000, frames: 812 });
  await wait(120);
  const label = await bar.webContents.executeJavaScript(`
    document.getElementById('time').textContent + '|' + document.getElementById('frames').textContent
  `);
  assert.strictEqual(label, '1:05|812 frames');
  bar.destroy();
  ok();
}

async function run() {
  await testOverlay();
  await testPicker();
  await testEditor();
  await testSettings();

  if (problems.length) {
    throw new Error(`renderer reported errors:\n  - ${problems.join('\n  - ')}`);
  }
  console.log('\nall UI checks passed');
}

// Destroying the last window must not end the run.
app.on('window-all-closed', () => {});

app.whenReady()
  .then(run)
  .then(() => app.exit(0))
  .catch((err) => {
    console.error('\nFAILED:', err && err.stack ? err.stack : err);
    app.exit(1);
  });

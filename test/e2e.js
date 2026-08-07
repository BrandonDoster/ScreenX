'use strict';

// Integration check for the parts that only work inside Electron: the desktop
// capturer, cropping/saving, and the full GIF recording pipeline.
// Run with: npm run test:e2e
// Needs Screen Recording permission on macOS.

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { app, BrowserWindow, ipcMain } = require('electron');

const settings = require('../src/main/settings');
const capture = require('../src/main/capture');

const workdir = fs.mkdtempSync(path.join(os.tmpdir(), 'screenx-e2e-'));
const RECORD_MS = 2000;

function step(name) { process.stdout.write(`  ${name}... `); }
function ok(extra) { console.log(`ok${extra ? ` (${extra})` : ''}`); }

async function run() {
  settings.init(workdir);
  settings.save({
    screenshotFolder: path.join(workdir, 'shots'),
    gifFolder: path.join(workdir, 'gifs'),
    screenshotNamePattern: 'e2e_%i{3}',
    gifNamePattern: 'e2e_%i{3}',
    gif: { fps: 10, maxSeconds: 30, maxWidth: 0, repeat: 0 }
  });

  step('captureDisplays');
  const shots = await capture.captureDisplays();
  assert.ok(shots.length > 0, 'no displays returned');
  const shot = shots[0];
  assert.ok(!shot.image.isEmpty(), 'display image was empty — check Screen Recording permission');
  assert.ok(shot.sourceId, 'display source id missing');
  const size = shot.image.getSize();
  assert.ok(size.width > 100 && size.height > 100, `implausible display size ${size.width}x${size.height}`);
  ok(`${shots.length} display(s), ${size.width}x${size.height}`);

  step('cropToDisplayRect');
  const cropped = capture.cropToDisplayRect(shot.image, shot.display, { x: 10, y: 10, width: 300, height: 200 });
  assert.ok(cropped, 'crop returned nothing');
  const scale = shot.display.scaleFactor;
  assert.strictEqual(cropped.getSize().width, Math.round(300 * scale));
  assert.strictEqual(cropped.getSize().height, Math.round(200 * scale));
  ok(`${cropped.getSize().width}x${cropped.getSize().height}`);

  step('saveImage');
  const saved = capture.saveImage(cropped, { title: 'e2e' });
  assert.ok(fs.existsSync(saved), 'saved file missing');
  assert.strictEqual(path.basename(saved), 'e2e_001.png');
  assert.ok(fs.readFileSync(saved).subarray(1, 4).toString() === 'PNG', 'not a PNG');
  // The counter has to advance so the next capture does not overwrite this one.
  const second = capture.saveImage(cropped, { title: 'e2e' });
  assert.strictEqual(path.basename(second), 'e2e_002.png');
  ok(path.basename(saved));

  step('listWindows');
  const windows = await capture.listWindows();
  console.log(`ok (${windows.length} window(s))`);

  step(`record ${RECORD_MS}ms region`);
  const gifPath = await recordRegion(shot);
  const bytes = fs.readFileSync(gifPath);
  assert.ok(bytes.subarray(0, 6).toString() === 'GIF89a', 'not a GIF89a file');
  assert.ok(bytes.length > 1024, `gif suspiciously small: ${bytes.length} bytes`);
  // Netscape looping extension must be present for the "loop forever" setting.
  assert.ok(bytes.includes(Buffer.from('NETSCAPE2.0')), 'loop extension missing');
  const frames = countFrames(bytes);
  assert.ok(frames >= 5, `expected several frames, got ${frames}`);
  ok(`${frames} frames, ${(bytes.length / 1024).toFixed(0)} KB`);

  console.log(`\nall integration checks passed (artifacts in ${workdir})`);
}

/** Count image descriptors (0x2C) that follow a graphic control extension. */
function countFrames(buffer) {
  let count = 0;
  for (let i = 0; i < buffer.length - 1; i++) {
    if (buffer[i] === 0x21 && buffer[i + 1] === 0xf9) count++;
  }
  return count;
}

function recordRegion(shot) {
  return new Promise((resolve, reject) => {
    const win = new BrowserWindow({
      show: false,
      webPreferences: {
        preload: path.join(__dirname, '..', 'src', 'preload', 'recorder-preload.js'),
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: false,
        backgroundThrottling: false
      }
    });

    const timeout = setTimeout(() => reject(new Error('recording never finished')), 30000);

    ipcMain.once('recorder:done', (_event, { bytes, width, height }) => {
      clearTimeout(timeout);
      // outputWidth must win over the Retina-doubled source resolution.
      assert.strictEqual(width, 320, `expected a 320px wide gif, got ${width}`);
      assert.strictEqual(height, 240, `expected a 240px tall gif, got ${height}`);
      try {
        resolve(capture.saveGif(Buffer.from(bytes), { title: 'e2e', width, height }));
      } catch (err) {
        reject(err);
      } finally {
        win.destroy();
      }
    });

    ipcMain.once('recorder:error', (_event, message) => {
      clearTimeout(timeout);
      win.destroy();
      reject(new Error(message));
    });

    win.loadFile(path.join(__dirname, '..', 'src', 'renderer', 'recorder.html'));
    win.webContents.once('did-finish-load', () => {
      const scale = shot.display.scaleFactor;
      win.webContents.send('recorder:start', {
        sourceId: shot.sourceId,
        crop: { x: 0, y: 0, width: Math.round(320 * scale), height: Math.round(240 * scale) },
        fps: 10,
        maxSeconds: 30,
        maxWidth: 0,
        repeat: 0,
        outputWidth: 320,
        sourceSize: {
          width: Math.round(shot.display.size.width * scale),
          height: Math.round(shot.display.size.height * scale)
        }
      });
      setTimeout(() => win.webContents.send('recorder:stop'), RECORD_MS);
    });
  });
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

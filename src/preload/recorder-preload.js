'use strict';

const { contextBridge, ipcRenderer } = require('electron');
const { GIFEncoder, quantize, applyPalette } = require('gifenc');

// Re-quantising every frame is the most expensive part of GIF encoding, so the
// palette is refreshed roughly once a second and reused in between. It is still
// attached to every frame as a local colour table, because a frame without one
// falls back to the *global* table rather than the previous local one.
// ponytail: fixed interval; make it adaptive if scene-change banding shows up.
const PALETTE_INTERVAL = 15;

let encoder = null;
let palette = null;
let frameCount = 0;
let size = { width: 0, height: 0 };
let repeat = 0;
// GIF stores how long a frame stays on screen, which is only known once the
// following frame arrives, so one frame is always held back.
let pending = null;

function flush(delayMs) {
  if (!pending || !encoder) return;
  encoder.writeFrame(pending.index, size.width, size.height, {
    palette: pending.palette,
    repeat, // only read for the first frame written
    delay: Math.min(6000, Math.max(20, Math.round(delayMs)))
  });
  pending = null;
}

contextBridge.exposeInMainWorld('screenx', {
  onStart: (fn) => ipcRenderer.on('recorder:start', (_e, payload) => fn(payload)),
  onStop: (fn) => ipcRenderer.on('recorder:stop', () => fn()),
  onCancel: (fn) => ipcRenderer.on('recorder:cancel', () => fn()),
  progress: (info) => ipcRenderer.send('recorder:progress', info),
  error: (message) => ipcRenderer.send('recorder:error', String(message)),

  begin(width, height, loop) {
    encoder = GIFEncoder({ auto: true });
    palette = null;
    pending = null;
    frameCount = 0;
    size = { width, height };
    repeat = loop;
  },

  /**
   * @param {ArrayBuffer} rgba raw RGBA pixels, width*height*4 bytes
   * @param {number} timestamp ms since the recording started
   */
  addFrame(rgba, timestamp) {
    if (!encoder) return;
    const data = new Uint8Array(rgba);
    if (!palette || frameCount % PALETTE_INTERVAL === 0) {
      palette = quantize(data, 256, { format: 'rgb565' });
    }
    const index = applyPalette(data, palette, 'rgb565');
    if (pending) flush(timestamp - pending.timestamp);
    pending = { index, palette, timestamp };
    frameCount++;
  },

  finish(finalDelay) {
    if (!encoder) return;
    flush(finalDelay || 100);
    encoder.finish();
    const bytes = encoder.bytes();
    const { width, height } = size;
    encoder = null;
    palette = null;
    ipcRenderer.send('recorder:done', { bytes, width, height });
  },

  abort() {
    encoder = null;
    palette = null;
    pending = null;
  }
});

'use strict';

const fs = require('fs');
const path = require('path');
const os = require('os');

let filePath = null;
let current = null;

function defaultFolders() {
  const pictures = path.join(os.homedir(), 'Pictures');
  const base = fs.existsSync(pictures) ? pictures : os.homedir();
  return {
    screenshotFolder: path.join(base, 'ScreenX', 'Screenshots'),
    gifFolder: path.join(base, 'ScreenX', 'Recordings')
  };
}

function defaults() {
  return {
    ...defaultFolders(),
    screenshotNamePattern: 'ScreenX_%y-%mo-%d_%h-%mi-%s',
    gifNamePattern: 'ScreenX_%y-%mo-%d_%h-%mi-%s',
    imageFormat: 'png',
    jpegQuality: 90,
    // editor | save | copy | saveCopy
    afterCapture: 'editor',
    copyPathAfterSave: false,
    showNotification: true,
    autoIncrementNumber: 0,
    launchAtLogin: false,
    features: {
      captureFullscreen: true,
      captureRegion: true,
      captureWindow: true,
      recordRegion: true,
      recordWindow: true,
      editor: true
    },
    hotkeys: {
      captureFullscreen: 'CommandOrControl+Alt+F',
      captureRegion: 'CommandOrControl+Alt+A',
      captureWindow: 'CommandOrControl+Alt+W',
      recordRegion: 'CommandOrControl+Alt+R',
      recordWindow: 'CommandOrControl+Alt+E',
      stopRecording: 'CommandOrControl+Alt+S'
    },
    gif: {
      fps: 15,
      maxSeconds: 60,
      // Caps window recordings, which come off a Retina display at double size.
      maxWidth: 800, // 0 = keep source width
      repeat: 0 // 0 = loop forever, -1 = play once
    }
  };
}

/** Merge saved values over defaults one level deep, dropping unknown keys. */
function merge(base, saved) {
  const out = { ...base };
  if (!saved || typeof saved !== 'object') return out;
  for (const key of Object.keys(base)) {
    const value = saved[key];
    if (value === undefined || value === null) continue;
    if (base[key] && typeof base[key] === 'object' && !Array.isArray(base[key])) {
      out[key] = merge(base[key], value);
    } else if (typeof value === typeof base[key]) {
      out[key] = value;
    }
  }
  return out;
}

function init(userDataPath) {
  filePath = path.join(userDataPath, 'settings.json');
  try {
    current = merge(defaults(), JSON.parse(fs.readFileSync(filePath, 'utf8')));
  } catch {
    current = defaults();
  }
  return current;
}

function get() {
  return current || defaults();
}

function save(patch) {
  current = merge(get(), patch);
  try {
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, JSON.stringify(current, null, 2));
  } catch (err) {
    console.error('[settings] save failed:', err.message);
  }
  return current;
}

module.exports = { init, get, save, defaults };

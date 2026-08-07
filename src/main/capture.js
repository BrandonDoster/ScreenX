'use strict';

const fs = require('fs');
const path = require('path');
const os = require('os');
const { desktopCapturer, screen, nativeImage, app } = require('electron');

const settings = require('./settings');
const { parseName } = require('./naming');

/** Upper bound for window thumbnails; real windows are smaller, so they come
 *  back at their native size rather than scaled down. */
const MAX_WINDOW_THUMB = { width: 4096, height: 4096 };

/** The window-list helper is a subprocess; do not let it stall a capture. */
const WINDOW_LIST_TIMEOUT = 1500;

/**
 * One entry per physical display, each with a frozen full-resolution image and
 * the capturer source id needed to open a video stream on it later.
 */
async function captureDisplays() {
  const displays = screen.getAllDisplays();
  const maxWidth = Math.max(...displays.map((d) => Math.round(d.size.width * d.scaleFactor)));
  const maxHeight = Math.max(...displays.map((d) => Math.round(d.size.height * d.scaleFactor)));

  const sources = await desktopCapturer.getSources({
    types: ['screen'],
    thumbnailSize: { width: maxWidth, height: maxHeight },
    fetchWindowIcons: false
  });

  return displays.map((display, index) => {
    // display_id is a string on every platform that reports it; some Linux/
    // Windows setups leave it blank, in which case order is the only clue.
    const source =
      sources.find((s) => String(s.display_id) === String(display.id)) || sources[index] || sources[0];
    return {
      display,
      sourceId: source ? source.id : null,
      name: source ? source.name : `Display ${index + 1}`,
      image: source ? source.thumbnail : nativeImage.createEmpty()
    };
  });
}

function displayUnderCursor() {
  return screen.getDisplayNearestPoint(screen.getCursorScreenPoint());
}

/**
 * Visible windows with their on-screen rectangles, front-most first, in the
 * same device-independent coordinate space Electron's screen module uses.
 *
 * No Electron API exposes foreign window geometry, so this shells out to the
 * helper binaries in get-windows. Every failure mode degrades to an empty list,
 * which turns window highlighting off and leaves dragging working.
 */
async function listWindowBounds() {
  try {
    const { openWindows } = await import('get-windows');
    const windows = await Promise.race([
      openWindows({ accessibilityPermission: false, screenRecordingPermission: true }),
      new Promise((resolve) => setTimeout(() => resolve(null), WINDOW_LIST_TIMEOUT))
    ]);
    if (!Array.isArray(windows)) return [];

    const ownProcess = app.getName();
    return windows
      .filter((win) => win && win.bounds && win.owner && win.owner.name !== ownProcess)
      .filter((win) => win.bounds.width >= 40 && win.bounds.height >= 40)
      .map((win) => ({
        title: win.title || win.owner.name,
        owner: win.owner.name,
        bounds: {
          x: Math.round(win.bounds.x),
          y: Math.round(win.bounds.y),
          width: Math.round(win.bounds.width),
          height: Math.round(win.bounds.height)
        }
      }));
  } catch (err) {
    console.error('[capture] window list unavailable:', err.message);
    return [];
  }
}

/**
 * Clip the window list to one display and move it into that display's own
 * coordinates, which is what the overlay draws in. Order is preserved because
 * the list arrives front-most first and the overlay picks the first hit.
 */
function windowsForDisplay(windows, display) {
  const { x, y, width, height } = display.bounds;
  return windows
    .filter((win) => {
      const b = win.bounds;
      return b.x < x + width && b.x + b.width > x && b.y < y + height && b.y + b.height > y;
    })
    .map((win) => {
      const left = Math.max(win.bounds.x - x, 0);
      const top = Math.max(win.bounds.y - y, 0);
      return {
        title: win.title,
        rect: {
          x: left,
          y: top,
          width: Math.min(win.bounds.x - x + win.bounds.width, width) - left,
          height: Math.min(win.bounds.y - y + win.bounds.height, height) - top
        }
      };
    })
    .filter((win) => win.rect.width >= 20 && win.rect.height >= 20);
}

async function listWindows() {
  const sources = await desktopCapturer.getSources({
    types: ['window'],
    thumbnailSize: MAX_WINDOW_THUMB,
    fetchWindowIcons: true
  });

  return sources
    .filter((s) => !s.thumbnail.isEmpty() && s.name && s.name !== 'ScreenX')
    .map((s) => ({
      id: s.id,
      name: s.name,
      appIcon: s.appIcon && !s.appIcon.isEmpty() ? s.appIcon.toDataURL() : null,
      thumbnail: s.thumbnail
    }));
}

/**
 * Crop with coordinates given in device-independent pixels relative to a
 * display's top-left corner. nativeImage.crop() works in real pixels.
 */
function cropToDisplayRect(image, display, rect) {
  const scale = image.getSize().width / display.size.width || display.scaleFactor;
  const bounds = image.getSize();
  const x = Math.max(0, Math.round(rect.x * scale));
  const y = Math.max(0, Math.round(rect.y * scale));
  const width = Math.min(bounds.width - x, Math.round(rect.width * scale));
  const height = Math.min(bounds.height - y, Math.round(rect.height * scale));
  if (width < 1 || height < 1) return null;
  return image.crop({ x, y, width, height });
}

function encode(image) {
  const config = settings.get();
  if (config.imageFormat === 'jpg') {
    return { buffer: image.toJPEG(config.jpegQuality), ext: 'jpg' };
  }
  return { buffer: image.toPNG(), ext: 'png' };
}

/** Append " (2)", " (3)" ... until the path is free. */
function uniquePath(dir, base, ext) {
  let candidate = path.join(dir, `${base}.${ext}`);
  for (let n = 2; fs.existsSync(candidate); n++) {
    candidate = path.join(dir, `${base} (${n}).${ext}`);
  }
  return candidate;
}

function buildContext(meta = {}) {
  const config = settings.get();
  return {
    now: new Date(),
    counter: config.autoIncrementNumber + 1,
    title: meta.title || '',
    width: meta.width || 0,
    height: meta.height || 0,
    appName: 'ScreenX',
    userName: os.userInfo().username,
    computerName: os.hostname().split('.')[0]
  };
}

function resolveFolder(folder, fallbackName) {
  const target = folder && folder.trim()
    ? folder
    : path.join(app.getPath('pictures'), 'ScreenX', fallbackName);
  fs.mkdirSync(target, { recursive: true });
  return target;
}

/** Write bytes using the screenshot pattern/folder. Returns the file path. */
function saveImageBuffer(buffer, ext, meta = {}) {
  const config = settings.get();
  const dir = resolveFolder(config.screenshotFolder, 'Screenshots');
  const name = parseName(config.screenshotNamePattern, buildContext(meta));
  const target = uniquePath(dir, name, ext);
  fs.writeFileSync(target, buffer);
  settings.save({ autoIncrementNumber: config.autoIncrementNumber + 1 });
  return target;
}

function saveImage(image, meta = {}) {
  const size = image.getSize();
  const { buffer, ext } = encode(image);
  return saveImageBuffer(buffer, ext, { ...meta, width: size.width, height: size.height });
}

function saveGif(buffer, meta = {}) {
  const config = settings.get();
  const dir = resolveFolder(config.gifFolder, 'Recordings');
  const name = parseName(config.gifNamePattern, buildContext(meta));
  const target = uniquePath(dir, name, 'gif');
  fs.writeFileSync(target, buffer);
  settings.save({ autoIncrementNumber: config.autoIncrementNumber + 1 });
  return target;
}

module.exports = {
  captureDisplays,
  displayUnderCursor,
  listWindows,
  listWindowBounds,
  windowsForDisplay,
  cropToDisplayRect,
  encode,
  saveImage,
  saveImageBuffer,
  saveGif,
  resolveFolder,
  uniquePath,
  buildContext
};

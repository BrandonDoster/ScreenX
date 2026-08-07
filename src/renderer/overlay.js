'use strict';

// Fullscreen selection overlay. The screen is already frozen into an image by
// the main process, so everything here is drawn on one canvas: the frozen
// screen, a dim mask, the selection rectangle, crosshair and a magnifier.
//
// Hovering highlights the window (or panel) under the pointer so a single click
// grabs it, the same way dragging a rectangle grabs an arbitrary area. No
// platform exposes foreign window rectangles to Electron, so the highlight is
// found by looking for the borders around the pointer in the frozen image.

const canvas = document.getElementById('stage');
const ctx = canvas.getContext('2d');

const MIN_SIZE = 4;
const MAG_SIZE = 132;
const MAG_ZOOM = 6;

let screenshot = null;
let displayId = null;
let mode = 'screenshot';
let cursor = { x: -1, y: -1 };
let start = null;
let selection = null;
let dragging = false;
let done = false;
let snapped = null;
let snapEnabled = true;

function resize() {
  const dpr = window.devicePixelRatio || 1;
  canvas.width = Math.round(window.innerWidth * dpr);
  canvas.height = Math.round(window.innerHeight * dpr);
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  updateSnap();
  draw();
}

function normalize(a, b) {
  return {
    x: Math.min(a.x, b.x),
    y: Math.min(a.y, b.y),
    width: Math.abs(a.x - b.x),
    height: Math.abs(a.y - b.y)
  };
}

// --------------------------------------------------------------- window hits

// Windows on this display, front-most first, in this overlay's coordinates.
let windows = [];
let snapTitle = '';

/** The front-most window containing the point. */
function windowAt(px, py) {
  for (const win of windows) {
    const r = win.rect;
    if (px >= r.x && px < r.x + r.width && py >= r.y && py < r.y + r.height) return win;
  }
  return null;
}

function updateSnap() {
  const hit = snapEnabled && !dragging && cursor.x >= 0 ? windowAt(cursor.x, cursor.y) : null;
  snapped = hit ? hit.rect : null;
  snapTitle = hit ? hit.title : '';
}

function drawMagnifier(w, h) {
  if (!screenshot || cursor.x < 0) return;
  const half = MAG_SIZE / 2;
  // Keep the loupe from running off the screen edges.
  let mx = cursor.x + 24;
  let my = cursor.y + 24;
  if (mx + MAG_SIZE > w) mx = cursor.x - 24 - MAG_SIZE;
  if (my + MAG_SIZE + 22 > h) my = cursor.y - 24 - MAG_SIZE - 22;

  const scaleX = screenshot.width / w;
  const scaleY = screenshot.height / h;
  const srcW = MAG_SIZE / MAG_ZOOM * scaleX;
  const srcH = MAG_SIZE / MAG_ZOOM * scaleY;

  ctx.save();
  ctx.beginPath();
  ctx.rect(mx, my, MAG_SIZE, MAG_SIZE);
  ctx.clip();
  ctx.imageSmoothingEnabled = false;
  ctx.fillStyle = '#000';
  ctx.fillRect(mx, my, MAG_SIZE, MAG_SIZE);
  ctx.drawImage(
    screenshot,
    cursor.x * scaleX - srcW / 2, cursor.y * scaleY - srcH / 2, srcW, srcH,
    mx, my, MAG_SIZE, MAG_SIZE
  );
  ctx.restore();

  ctx.strokeStyle = 'rgba(255,255,255,.9)';
  ctx.lineWidth = 1;
  ctx.strokeRect(mx + .5, my + .5, MAG_SIZE - 1, MAG_SIZE - 1);
  // Centre cell marks the exact pixel under the cursor.
  ctx.strokeStyle = 'rgba(61,139,253,.95)';
  ctx.strokeRect(mx + half - MAG_ZOOM / 2, my + half - MAG_ZOOM / 2, MAG_ZOOM, MAG_ZOOM);

  const label = `${Math.round(cursor.x)}, ${Math.round(cursor.y)}`;
  ctx.font = '12px ui-monospace, monospace';
  ctx.fillStyle = 'rgba(0,0,0,.75)';
  ctx.fillRect(mx, my + MAG_SIZE + 2, MAG_SIZE, 18);
  ctx.fillStyle = '#fff';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText(label, mx + half, my + MAG_SIZE + 11);
  ctx.textAlign = 'left';
}

function drawHint(w, h) {
  const verb = mode === 'record' ? 'record' : 'capture';
  const text = snapped
    ? `Click to ${verb} the highlighted window  ·  drag for a custom area  ·  Esc to cancel`
    : `Drag to choose an area to ${verb}  ·  Esc or right-click to cancel`;
  ctx.font = '13px -apple-system, "Segoe UI", system-ui, sans-serif';
  const width = ctx.measureText(text).width + 24;
  const x = (w - width) / 2;
  ctx.fillStyle = 'rgba(0,0,0,.7)';
  ctx.fillRect(x, 24, width, 30);
  ctx.fillStyle = '#fff';
  ctx.textBaseline = 'middle';
  ctx.fillText(text, x + 12, 39);
}

function drawSizeLabel(rect, title) {
  let text = `${Math.round(rect.width)} × ${Math.round(rect.height)}`;
  if (title) text += `  ${title.length > 48 ? `${title.slice(0, 47)}…` : title}`;
  ctx.font = '12px ui-monospace, monospace';
  const width = Math.min(ctx.measureText(text).width + 14, Math.max(rect.width, 220));
  const x = rect.x;
  const y = rect.y < 26 ? rect.y + rect.height + 4 : rect.y - 24;
  ctx.save();
  ctx.beginPath();
  ctx.rect(x, y, width, 20);
  ctx.fillStyle = 'rgba(0,0,0,.8)';
  ctx.fill();
  ctx.clip();
  ctx.fillStyle = '#fff';
  ctx.textBaseline = 'middle';
  ctx.fillText(text, x + 7, y + 10);
  ctx.restore();
}

function draw() {
  const w = window.innerWidth;
  const h = window.innerHeight;
  ctx.clearRect(0, 0, w, h);

  if (screenshot) ctx.drawImage(screenshot, 0, 0, w, h);
  else { ctx.fillStyle = '#111'; ctx.fillRect(0, 0, w, h); }

  ctx.fillStyle = 'rgba(0,0,0,.45)';
  ctx.fillRect(0, 0, w, h);

  const dragged = selection && selection.width >= 1 && selection.height >= 1;
  const rect = dragged ? selection : snapped;

  if (rect) {
    // Punch the highlighted area back out to full brightness.
    ctx.save();
    ctx.beginPath();
    ctx.rect(rect.x, rect.y, rect.width, rect.height);
    ctx.clip();
    if (screenshot) ctx.drawImage(screenshot, 0, 0, w, h);
    ctx.restore();

    ctx.save();
    ctx.strokeStyle = '#3d8bfd';
    ctx.lineWidth = 1;
    // A dashed outline marks a box that was found rather than dragged.
    if (!dragged) ctx.setLineDash([6, 4]);
    ctx.strokeRect(rect.x + .5, rect.y + .5, rect.width - 1, rect.height - 1);
    ctx.restore();
    drawSizeLabel(rect, dragged ? '' : snapTitle);
    if (!dragged) drawHint(w, h);
  } else if (cursor.x >= 0) {
    ctx.strokeStyle = 'rgba(255,255,255,.55)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(0, cursor.y + .5); ctx.lineTo(w, cursor.y + .5);
    ctx.moveTo(cursor.x + .5, 0); ctx.lineTo(cursor.x + .5, h);
    ctx.stroke();
    drawHint(w, h);
  }

  // The loupe is for picking exact pixels; a whole-window highlight is neither.
  if (!dragging && !snapped) drawMagnifier(w, h);
}

function finish() {
  if (done) return;
  // A click that never turned into a drag takes the highlighted box instead.
  const dragged = selection && selection.width >= MIN_SIZE && selection.height >= MIN_SIZE;
  const rect = dragged ? selection : snapped;
  if (!rect) return cancel();
  done = true;
  window.screenx.select(displayId, {
    x: Math.round(rect.x),
    y: Math.round(rect.y),
    width: Math.round(rect.width),
    height: Math.round(rect.height)
  });
}

function cancel() {
  if (done) return;
  done = true;
  window.screenx.cancel();
}

window.screenx.onInit((payload) => {
  displayId = payload.displayId;
  mode = payload.mode;
  windows = payload.windows || [];
  if (!payload.dataURL) return resize();
  const img = new Image();
  img.onload = () => { screenshot = img; resize(); };
  img.src = payload.dataURL;
});

window.addEventListener('resize', resize);

window.addEventListener('mousedown', (e) => {
  if (e.button !== 0) return cancel();
  dragging = true;
  start = { x: e.clientX, y: e.clientY };
  selection = { x: start.x, y: start.y, width: 0, height: 0 };
  draw();
});

window.addEventListener('mousemove', (e) => {
  cursor = { x: e.clientX, y: e.clientY };
  // Holding the platform modifier turns the highlight off for this move.
  snapEnabled = !(e.ctrlKey || e.metaKey);
  if (dragging && start) {
    selection = normalize(start, cursor);
    // Once a real drag starts the highlight is out of the way.
    if (selection.width >= MIN_SIZE || selection.height >= MIN_SIZE) snapped = null;
  } else {
    updateSnap();
  }
  draw();
});

window.addEventListener('mouseup', (e) => {
  if (!dragging) return;
  dragging = false;
  selection = normalize(start, { x: e.clientX, y: e.clientY });
  finish();
});

window.addEventListener('contextmenu', (e) => { e.preventDefault(); cancel(); });

window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') cancel();
  // Whole display without dragging.
  if (e.key === 'Enter' && !dragging) {
    snapped = null;
    selection = { x: 0, y: 0, width: window.innerWidth, height: window.innerHeight };
    finish();
  }
});

// Hide the crosshair on the displays the pointer is not on.
document.addEventListener('mouseleave', () => { cursor = { x: -1, y: -1 }; draw(); });

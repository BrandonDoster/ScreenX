'use strict';

// Fullscreen selection overlay. The screen is already frozen into an image by
// the main process, so everything here is drawn on one canvas: the frozen
// screen, a dim mask, the selection rectangle, crosshair and a magnifier.

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

function resize() {
  const dpr = window.devicePixelRatio || 1;
  canvas.width = Math.round(window.innerWidth * dpr);
  canvas.height = Math.round(window.innerHeight * dpr);
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
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
  const text = mode === 'record'
    ? 'Drag to choose the area to record  ·  Esc or right-click to cancel'
    : 'Drag to select an area  ·  Esc or right-click to cancel';
  ctx.font = '13px -apple-system, "Segoe UI", system-ui, sans-serif';
  const width = ctx.measureText(text).width + 24;
  const x = (w - width) / 2;
  ctx.fillStyle = 'rgba(0,0,0,.7)';
  ctx.fillRect(x, 24, width, 30);
  ctx.fillStyle = '#fff';
  ctx.textBaseline = 'middle';
  ctx.fillText(text, x + 12, 39);
}

function drawSizeLabel(rect) {
  const text = `${Math.round(rect.width)} × ${Math.round(rect.height)}`;
  ctx.font = '12px ui-monospace, monospace';
  const width = ctx.measureText(text).width + 14;
  let x = rect.x;
  let y = rect.y - 24;
  if (y < 2) y = rect.y + rect.height + 4;
  ctx.fillStyle = 'rgba(0,0,0,.8)';
  ctx.fillRect(x, y, width, 20);
  ctx.fillStyle = '#fff';
  ctx.textBaseline = 'middle';
  ctx.fillText(text, x + 7, y + 10);
}

function draw() {
  const w = window.innerWidth;
  const h = window.innerHeight;
  ctx.clearRect(0, 0, w, h);

  if (screenshot) ctx.drawImage(screenshot, 0, 0, w, h);
  else { ctx.fillStyle = '#111'; ctx.fillRect(0, 0, w, h); }

  ctx.fillStyle = 'rgba(0,0,0,.45)';
  ctx.fillRect(0, 0, w, h);

  const rect = selection;
  if (rect && rect.width >= 1 && rect.height >= 1) {
    // Punch the selection back out to full brightness.
    ctx.save();
    ctx.beginPath();
    ctx.rect(rect.x, rect.y, rect.width, rect.height);
    ctx.clip();
    if (screenshot) ctx.drawImage(screenshot, 0, 0, w, h);
    ctx.restore();

    ctx.strokeStyle = '#3d8bfd';
    ctx.lineWidth = 1;
    ctx.strokeRect(rect.x + .5, rect.y + .5, rect.width - 1, rect.height - 1);
    drawSizeLabel(rect);
  } else if (cursor.x >= 0) {
    ctx.strokeStyle = 'rgba(255,255,255,.55)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(0, cursor.y + .5); ctx.lineTo(w, cursor.y + .5);
    ctx.moveTo(cursor.x + .5, 0); ctx.lineTo(cursor.x + .5, h);
    ctx.stroke();
    drawHint(w, h);
  }

  if (!dragging) drawMagnifier(w, h);
}

function finish() {
  if (done) return;
  const rect = selection;
  if (!rect || rect.width < MIN_SIZE || rect.height < MIN_SIZE) return cancel();
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
  if (dragging && start) selection = normalize(start, cursor);
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
    selection = { x: 0, y: 0, width: window.innerWidth, height: window.innerHeight };
    finish();
  }
});

// Hide the crosshair on the displays the pointer is not on.
document.addEventListener('mouseleave', () => { cursor = { x: -1, y: -1 }; draw(); });

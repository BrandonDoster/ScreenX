'use strict';

// Annotation editor. The untouched capture lives on an offscreen "base" canvas;
// annotations are kept as plain objects and re-drawn on every frame, so undo is
// just a matter of restoring an earlier shapes array.

const stage = document.getElementById('stage');
const ctx = stage.getContext('2d');
const stageBox = document.getElementById('stageBox');
const stageWrap = document.getElementById('stageWrap');
const textInput = document.getElementById('textInput');

const TOOLS = [
  { id: 'select', icon: '⬚', label: 'Select and move' },
  { id: 'crop', icon: '⌗', label: 'Crop' },
  { id: 'rect', icon: '▭', label: 'Rectangle' },
  { id: 'ellipse', icon: '◯', label: 'Ellipse' },
  { id: 'line', icon: '／', label: 'Line' },
  { id: 'arrow', icon: '➜', label: 'Arrow' },
  { id: 'pen', icon: '✎', label: 'Freehand' },
  { id: 'highlight', icon: '▬', label: 'Highlighter' },
  { id: 'pixelate', icon: '▓', label: 'Pixelate' },
  { id: 'text', icon: 'T', label: 'Text' },
  { id: 'step', icon: '①', label: 'Step number' }
];

const SWATCHES = ['#e5484d', '#f5a524', '#f7e733', '#37b24d', '#3d8bfd', '#9b59f5', '#ffffff', '#000000'];

let base = document.createElement('canvas');
let baseCtx = base.getContext('2d');
let shapes = [];
let history = [];
let historyAt = -1;
let meta = {};

let tool = 'rect';
let scale = 1;
let fitToWindow = true;
let stepCounter = 1;

let drawing = null;
let selected = null;
let moving = null;
let editingText = null;
let dirty = false;

const options = {
  stroke: '#e5484d',
  fill: 'none',
  lineWidth: 3,
  fontSize: 28
};

const $ = (id) => document.getElementById(id);

// ------------------------------------------------------------------ history

function snapshot() {
  return { base, shapes: shapes.map((s) => ({ ...s, points: s.points ? s.points.slice() : undefined })) };
}

function pushHistory() {
  history = history.slice(0, historyAt + 1);
  history.push(snapshot());
  historyAt = history.length - 1;
  dirty = historyAt > 0;
  updateButtons();
}

function restore(entry) {
  base = entry.base;
  baseCtx = base.getContext('2d');
  shapes = entry.shapes.map((s) => ({ ...s, points: s.points ? s.points.slice() : undefined }));
  selected = null;
  resizeStage();
  render();
  updateButtons();
}

function undo() {
  if (historyAt <= 0) return;
  historyAt--;
  restore(history[historyAt]);
}

function redo() {
  if (historyAt >= history.length - 1) return;
  historyAt++;
  restore(history[historyAt]);
}

function updateButtons() {
  $('undo').disabled = historyAt <= 0;
  $('redo').disabled = historyAt >= history.length - 1;
  $('info').textContent = `${base.width} × ${base.height}${dirty ? '  ·  edited' : ''}`;
}

// ------------------------------------------------------------------ drawing

function shapeBounds(shape) {
  switch (shape.type) {
    case 'pen': {
      const xs = shape.points.map((p) => p.x);
      const ys = shape.points.map((p) => p.y);
      return { x: Math.min(...xs), y: Math.min(...ys), width: Math.max(...xs) - Math.min(...xs), height: Math.max(...ys) - Math.min(...ys) };
    }
    case 'text': {
      ctx.font = `${shape.fontSize}px -apple-system, "Segoe UI", system-ui, sans-serif`;
      const lines = shape.text.split('\n');
      const width = Math.max(...lines.map((l) => ctx.measureText(l).width));
      return { x: shape.x, y: shape.y, width, height: lines.length * shape.fontSize * 1.2 };
    }
    case 'step': {
      const r = shape.radius;
      return { x: shape.x - r, y: shape.y - r, width: r * 2, height: r * 2 };
    }
    default:
      return {
        x: Math.min(shape.x1, shape.x2),
        y: Math.min(shape.y1, shape.y2),
        width: Math.abs(shape.x2 - shape.x1),
        height: Math.abs(shape.y2 - shape.y1)
      };
  }
}

function drawArrowHead(target, x1, y1, x2, y2, width) {
  const angle = Math.atan2(y2 - y1, x2 - x1);
  const size = Math.max(10, width * 3.6);
  target.beginPath();
  target.moveTo(x2, y2);
  target.lineTo(x2 - size * Math.cos(angle - Math.PI / 7), y2 - size * Math.sin(angle - Math.PI / 7));
  target.lineTo(x2 - size * Math.cos(angle + Math.PI / 7), y2 - size * Math.sin(angle + Math.PI / 7));
  target.closePath();
  target.fill();
}

function drawShape(target, shape) {
  target.save();
  target.strokeStyle = shape.stroke;
  target.fillStyle = shape.stroke;
  target.lineWidth = shape.lineWidth;
  target.lineCap = 'round';
  target.lineJoin = 'round';

  const b = shapeBounds(shape);

  switch (shape.type) {
    case 'rect':
      if (shape.fill === 'solid') target.fillRect(b.x, b.y, b.width, b.height);
      else target.strokeRect(b.x, b.y, b.width, b.height);
      break;

    case 'ellipse':
      target.beginPath();
      target.ellipse(b.x + b.width / 2, b.y + b.height / 2, b.width / 2, b.height / 2, 0, 0, Math.PI * 2);
      if (shape.fill === 'solid') target.fill();
      else target.stroke();
      break;

    case 'line':
    case 'arrow':
      target.beginPath();
      target.moveTo(shape.x1, shape.y1);
      target.lineTo(shape.x2, shape.y2);
      target.stroke();
      if (shape.type === 'arrow') drawArrowHead(target, shape.x1, shape.y1, shape.x2, shape.y2, shape.lineWidth);
      break;

    case 'pen':
      target.beginPath();
      shape.points.forEach((p, i) => (i ? target.lineTo(p.x, p.y) : target.moveTo(p.x, p.y)));
      target.stroke();
      break;

    case 'highlight':
      target.globalAlpha = 0.35;
      target.globalCompositeOperation = 'multiply';
      target.fillRect(b.x, b.y, b.width, b.height);
      break;

    case 'pixelate': {
      if (b.width < 2 || b.height < 2) break;
      // Sample the untouched capture so stacked annotations do not smear.
      const blocks = Math.max(2, Math.round(Math.max(b.width, b.height) / (shape.lineWidth * 4)));
      const small = document.createElement('canvas');
      small.width = Math.max(1, Math.min(blocks, Math.round(b.width)));
      small.height = Math.max(1, Math.round(small.width * (b.height / b.width)) || 1);
      const smallCtx = small.getContext('2d');
      smallCtx.imageSmoothingEnabled = true;
      smallCtx.drawImage(base, b.x, b.y, b.width, b.height, 0, 0, small.width, small.height);
      target.imageSmoothingEnabled = false;
      target.drawImage(small, 0, 0, small.width, small.height, b.x, b.y, b.width, b.height);
      break;
    }

    case 'text': {
      target.font = `${shape.fontSize}px -apple-system, "Segoe UI", system-ui, sans-serif`;
      target.textBaseline = 'top';
      target.lineWidth = Math.max(2, shape.fontSize / 10);
      target.strokeStyle = 'rgba(0,0,0,.55)';
      shape.text.split('\n').forEach((line, i) => {
        const y = shape.y + i * shape.fontSize * 1.2;
        target.strokeText(line, shape.x, y);
        target.fillText(line, shape.x, y);
      });
      break;
    }

    case 'step': {
      target.beginPath();
      target.arc(shape.x, shape.y, shape.radius, 0, Math.PI * 2);
      target.fill();
      target.fillStyle = '#fff';
      target.font = `bold ${Math.round(shape.radius * 1.2)}px -apple-system, "Segoe UI", system-ui, sans-serif`;
      target.textAlign = 'center';
      target.textBaseline = 'middle';
      target.fillText(String(shape.number), shape.x, shape.y + 1);
      break;
    }
  }
  target.restore();
}

function drawSelection(shape) {
  const b = shapeBounds(shape);
  const pad = 4;
  ctx.save();
  ctx.setLineDash([5 / scale, 4 / scale]);
  ctx.lineWidth = 1 / scale;
  ctx.strokeStyle = '#3d8bfd';
  ctx.strokeRect(b.x - pad, b.y - pad, b.width + pad * 2, b.height + pad * 2);
  ctx.restore();
}

function render() {
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, stage.width, stage.height);
  ctx.drawImage(base, 0, 0);
  for (const shape of shapes) drawShape(ctx, shape);
  if (drawing) drawShape(ctx, drawing);
  if (drawing && drawing.type === 'crop') drawCropPreview(drawing);
  if (selected) drawSelection(selected);
}

function drawCropPreview(shape) {
  const b = shapeBounds(shape);
  ctx.save();
  ctx.fillStyle = 'rgba(0,0,0,.45)';
  ctx.beginPath();
  ctx.rect(0, 0, stage.width, stage.height);
  ctx.rect(b.x, b.y, b.width, b.height);
  ctx.fill('evenodd');
  ctx.strokeStyle = '#3d8bfd';
  ctx.lineWidth = 1;
  ctx.strokeRect(b.x, b.y, b.width, b.height);
  ctx.restore();
}

// -------------------------------------------------------------------- stage

function resizeStage() {
  stage.width = base.width;
  stage.height = base.height;
  applyScale();
}

function applyScale() {
  if (fitToWindow) {
    const available = stageWrap.getBoundingClientRect();
    const fitX = (available.width - 40) / base.width;
    const fitY = (available.height - 40) / base.height;
    scale = Math.min(1, fitX, fitY) || 1;
  } else {
    scale = 1;
  }
  stage.style.width = `${Math.round(base.width * scale)}px`;
  stage.style.height = `${Math.round(base.height * scale)}px`;
  stageBox.style.width = stage.style.width;
  stageBox.style.height = stage.style.height;
  $('zoom').textContent = fitToWindow ? `Fit ${Math.round(scale * 100)}%` : '100%';
}

function pointFrom(event) {
  const rect = stage.getBoundingClientRect();
  return {
    x: Math.round((event.clientX - rect.left) / scale),
    y: Math.round((event.clientY - rect.top) / scale)
  };
}

function hitTest(point) {
  for (let i = shapes.length - 1; i >= 0; i--) {
    const b = shapeBounds(shapes[i]);
    const pad = Math.max(6, shapes[i].lineWidth || 0);
    if (point.x >= b.x - pad && point.x <= b.x + b.width + pad &&
        point.y >= b.y - pad && point.y <= b.y + b.height + pad) {
      return shapes[i];
    }
  }
  return null;
}

function moveShape(shape, dx, dy) {
  if (shape.points) {
    shape.points.forEach((p) => { p.x += dx; p.y += dy; });
  } else if (shape.type === 'text' || shape.type === 'step') {
    shape.x += dx;
    shape.y += dy;
  } else {
    shape.x1 += dx; shape.x2 += dx;
    shape.y1 += dy; shape.y2 += dy;
  }
}

// --------------------------------------------------------------- crop / text

function applyCrop(shape) {
  const b = shapeBounds(shape);
  const x = Math.max(0, Math.round(b.x));
  const y = Math.max(0, Math.round(b.y));
  const width = Math.min(base.width - x, Math.round(b.width));
  const height = Math.min(base.height - y, Math.round(b.height));
  if (width < 2 || height < 2) return;

  // Bake current annotations into the new base so the crop is one flat step.
  const flat = document.createElement('canvas');
  flat.width = base.width;
  flat.height = base.height;
  const flatCtx = flat.getContext('2d');
  flatCtx.drawImage(base, 0, 0);
  for (const s of shapes) drawShape(flatCtx, s);

  const next = document.createElement('canvas');
  next.width = width;
  next.height = height;
  next.getContext('2d').drawImage(flat, x, y, width, height, 0, 0, width, height);

  base = next;
  baseCtx = base.getContext('2d');
  shapes = [];
  selected = null;
  resizeStage();
  render();
  pushHistory();
}

function startTextEditing(point, existing) {
  editingText = existing || {
    type: 'text',
    x: point.x,
    y: point.y,
    text: '',
    stroke: options.stroke,
    fontSize: options.fontSize,
    lineWidth: options.lineWidth
  };
  if (existing) shapes = shapes.filter((s) => s !== existing);
  render();

  textInput.style.display = 'block';
  textInput.style.left = `${editingText.x * scale}px`;
  textInput.style.top = `${editingText.y * scale}px`;
  textInput.style.font = `${editingText.fontSize * scale}px -apple-system, "Segoe UI", system-ui, sans-serif`;
  textInput.style.color = editingText.stroke;
  textInput.style.lineHeight = '1.2';
  textInput.value = editingText.text;
  autoSizeText();
  textInput.focus();
}

function autoSizeText() {
  const lines = textInput.value.split('\n');
  const longest = Math.max(4, ...lines.map((l) => l.length));
  textInput.style.width = `${longest * editingText.fontSize * scale * 0.62 + 16}px`;
  textInput.style.height = `${lines.length * editingText.fontSize * scale * 1.2 + 8}px`;
}

function commitText() {
  if (!editingText) return;
  const shape = editingText;
  editingText = null;
  textInput.style.display = 'none';
  textInput.value = '';
  if (shape.text.trim()) {
    shapes.push(shape);
    pushHistory();
  }
  render();
}

// ------------------------------------------------------------------ pointer

stage.addEventListener('mousedown', (event) => {
  if (event.button !== 0) return;
  if (editingText) { commitText(); return; }
  const point = pointFrom(event);

  if (tool === 'select') {
    selected = hitTest(point);
    moving = selected ? { start: point } : null;
    render();
    return;
  }

  if (tool === 'text') return startTextEditing(point);

  if (tool === 'step') {
    shapes.push({
      type: 'step',
      x: point.x,
      y: point.y,
      radius: Math.max(12, options.fontSize * 0.6),
      number: stepCounter++,
      stroke: options.stroke,
      lineWidth: options.lineWidth
    });
    pushHistory();
    render();
    return;
  }

  drawing = tool === 'pen'
    ? { type: 'pen', points: [point], stroke: options.stroke, lineWidth: options.lineWidth }
    : {
      type: tool,
      x1: point.x, y1: point.y, x2: point.x, y2: point.y,
      stroke: options.stroke,
      fill: options.fill,
      lineWidth: options.lineWidth
    };
});

window.addEventListener('mousemove', (event) => {
  const point = pointFrom(event);

  if (moving && selected) {
    moveShape(selected, point.x - moving.start.x, point.y - moving.start.y);
    moving.start = point;
    moving.moved = true;
    render();
    return;
  }
  if (!drawing) return;

  if (drawing.type === 'pen') drawing.points.push(point);
  else {
    drawing.x2 = point.x;
    drawing.y2 = point.y;
    if (event.shiftKey && (drawing.type === 'rect' || drawing.type === 'ellipse')) {
      const side = Math.max(Math.abs(drawing.x2 - drawing.x1), Math.abs(drawing.y2 - drawing.y1));
      drawing.x2 = drawing.x1 + Math.sign(drawing.x2 - drawing.x1 || 1) * side;
      drawing.y2 = drawing.y1 + Math.sign(drawing.y2 - drawing.y1 || 1) * side;
    }
  }
  render();
});

window.addEventListener('mouseup', () => {
  if (moving) {
    if (moving.moved) pushHistory();
    moving = null;
    return;
  }
  if (!drawing) return;
  const shape = drawing;
  drawing = null;

  if (shape.type === 'crop') return applyCrop(shape);

  const b = shapeBounds(shape);
  const tooSmall = shape.type === 'pen' ? shape.points.length < 2 : b.width < 3 && b.height < 3;
  if (tooSmall) return render();

  shapes.push(shape);
  pushHistory();
  render();
});

textInput.addEventListener('input', () => {
  editingText.text = textInput.value;
  autoSizeText();
});
textInput.addEventListener('blur', commitText);
textInput.addEventListener('keydown', (event) => {
  event.stopPropagation();
  if (event.key === 'Escape') { editingText.text = ''; commitText(); }
  if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) commitText();
});

// -------------------------------------------------------------------- chrome

function buildTools() {
  const box = $('tools');
  TOOLS.forEach((entry, index) => {
    const button = document.createElement('button');
    button.className = 'tool' + (entry.id === tool ? ' active' : '');
    button.textContent = entry.icon;
    button.title = index < 9 ? `${entry.label} (${index + 1})` : entry.label;
    button.dataset.tool = entry.id;
    button.addEventListener('click', () => selectTool(entry.id));
    box.appendChild(button);
  });
}

function selectTool(id) {
  if (editingText) commitText();
  tool = id;
  selected = null;
  document.querySelectorAll('.tool').forEach((b) => b.classList.toggle('active', b.dataset.tool === id));
  stage.classList.toggle('picking', id === 'select');
  render();
}

function buildSwatches() {
  const box = $('swatches');
  SWATCHES.forEach((colour) => {
    const chip = document.createElement('div');
    chip.className = 'swatch' + (colour === options.stroke ? ' active' : '');
    chip.style.background = colour;
    chip.dataset.colour = colour;
    chip.title = colour;
    chip.addEventListener('click', () => setColour(colour));
    box.appendChild(chip);
  });
}

function setColour(colour) {
  options.stroke = colour;
  $('strokeColor').value = colour;
  document.querySelectorAll('.swatch').forEach((s) => s.classList.toggle('active', s.dataset.colour === colour));
  if (selected) { selected.stroke = colour; pushHistory(); render(); }
}

$('strokeColor').addEventListener('input', (e) => setColour(e.target.value));
$('fillMode').addEventListener('change', (e) => {
  options.fill = e.target.value;
  if (selected) { selected.fill = options.fill; pushHistory(); render(); }
});
$('lineWidth').addEventListener('input', (e) => {
  options.lineWidth = Number(e.target.value);
  $('lineWidthValue').textContent = e.target.value;
  if (selected) { selected.lineWidth = options.lineWidth; pushHistory(); render(); }
});
$('fontSize').addEventListener('input', (e) => {
  options.fontSize = Number(e.target.value);
  $('fontSizeValue').textContent = e.target.value;
});

$('undo').addEventListener('click', undo);
$('redo').addEventListener('click', redo);
$('zoom').addEventListener('click', () => { fitToWindow = !fitToWindow; applyScale(); render(); });

// --------------------------------------------------------------------- output

function flatten() {
  const out = document.createElement('canvas');
  out.width = base.width;
  out.height = base.height;
  const outCtx = out.getContext('2d');
  outCtx.drawImage(base, 0, 0);
  for (const shape of shapes) drawShape(outCtx, shape);
  return out.toDataURL('image/png');
}

async function doSave() {
  if (editingText) commitText();
  const saved = await window.screenx.save(flatten(), { ...meta, width: base.width, height: base.height });
  if (saved) {
    dirty = false;
    $('info').textContent = `Saved to ${saved}`;
    setTimeout(() => window.screenx.close(), 400);
  }
}

async function doSaveAs() {
  if (editingText) commitText();
  const saved = await window.screenx.saveAs(flatten(), { ...meta, width: base.width, height: base.height });
  if (saved) {
    dirty = false;
    $('info').textContent = `Saved to ${saved}`;
  }
}

function doCopy() {
  if (editingText) commitText();
  window.screenx.copy(flatten());
  $('info').textContent = 'Copied to clipboard';
  setTimeout(updateButtons, 1500);
}

$('save').addEventListener('click', doSave);
$('saveAs').addEventListener('click', doSaveAs);
$('copy').addEventListener('click', doCopy);

// ------------------------------------------------------------------ keyboard

window.addEventListener('keydown', (event) => {
  if (editingText) return;
  const mod = event.metaKey || event.ctrlKey;

  if (mod && event.key.toLowerCase() === 'z') {
    event.preventDefault();
    return event.shiftKey ? redo() : undo();
  }
  if (mod && event.key.toLowerCase() === 's') {
    event.preventDefault();
    return event.shiftKey ? doSaveAs() : doSave();
  }
  if (mod && event.key.toLowerCase() === 'c') {
    event.preventDefault();
    return doCopy();
  }
  if (event.key === 'Escape') return window.screenx.close();
  if ((event.key === 'Delete' || event.key === 'Backspace') && selected) {
    shapes = shapes.filter((s) => s !== selected);
    selected = null;
    pushHistory();
    return render();
  }
  const index = Number(event.key) - 1;
  if (!mod && index >= 0 && index < TOOLS.length) selectTool(TOOLS[index].id);
});

window.addEventListener('resize', () => { applyScale(); render(); });

// ---------------------------------------------------------------------- init

window.screenx.onLoad((payload) => {
  meta = payload.meta || {};
  const image = new Image();
  image.onload = () => {
    base.width = image.naturalWidth;
    base.height = image.naturalHeight;
    baseCtx.drawImage(image, 0, 0);
    resizeStage();
    render();
    pushHistory();
    dirty = false;
    updateButtons();
  };
  image.src = payload.dataURL;
});

buildTools();
buildSwatches();
selectTool('rect');

'use strict';

const HOTKEY_LABELS = {
  captureFullscreen: 'Capture entire screen',
  captureRegion: 'Capture region',
  captureWindow: 'Capture window',
  recordRegion: 'Record region as GIF',
  recordWindow: 'Record window as GIF',
  stopRecording: 'Stop recording'
};

const TOKEN_HELP = [
  ['%y / %yy', 'year (2026 / 26)'],
  ['%mo / %mon / %mon2', 'month (07 / Jul / July)'],
  ['%d', 'day of month'],
  ['%w / %w2', 'weekday (Fri / Friday)'],
  ['%wy', 'ISO week number'],
  ['%h / %h12', 'hour (24h / 12h)'],
  ['%mi / %s / %ms', 'minute / second / millisecond'],
  ['%pm', 'AM or PM'],
  ['%unix', 'Unix timestamp'],
  ['%i', 'auto-increment number'],
  ['%ra / %rn / %rx', 'random letters / digits / hex'],
  ['%guid', 'random GUID'],
  ['%t', 'window or screen title'],
  ['%width / %height', 'image size'],
  ['%un / %cn', 'user name / computer name'],
  ['%pn', 'app name']
];

let state = null;
let listeningFor = null;

const $ = (id) => document.getElementById(id);

// ------------------------------------------------------------ accelerators

const KEY_ALIASES = {
  ' ': 'Space', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right',
  Escape: 'Esc', Enter: 'Return', '+': 'Plus'
};

function toAccelerator(event) {
  const parts = [];
  if (event.metaKey || event.ctrlKey) {
    // One portable modifier keeps the same settings file usable on both platforms.
    parts.push(event.metaKey && event.ctrlKey ? 'Control' : 'CommandOrControl');
    if (event.metaKey && event.ctrlKey) parts.push('Command');
  }
  if (event.altKey) parts.push('Alt');
  if (event.shiftKey) parts.push('Shift');

  let key = event.key;
  if (['Control', 'Meta', 'Alt', 'Shift'].includes(key)) return null;
  key = KEY_ALIASES[key] || key;
  if (key.length === 1) key = key.toUpperCase();
  // Shifted punctuation comes through as the symbol; the physical key is safer.
  if (/^Digit(\d)$/.test(event.code) && key.length === 1 && !/[0-9]/.test(key)) {
    key = event.code.slice(5);
  }
  parts.push(key);
  return parts.length > 1 ? parts.join('+') : null;
}

// ------------------------------------------------------------------- forms

function renderTokens() {
  const box = $('tokens');
  box.textContent = '';
  for (const [token, meaning] of TOKEN_HELP) {
    const row = document.createElement('div');
    const code = document.createElement('code');
    code.textContent = token;
    row.append(code, document.createTextNode(` — ${meaning}`));
    box.appendChild(row);
  }
}

function renderHotkeys() {
  const table = $('hotkeyTable');
  table.textContent = '';
  for (const [name, label] of Object.entries(HOTKEY_LABELS)) {
    const tr = document.createElement('tr');

    const nameCell = document.createElement('td');
    nameCell.className = 'name';
    nameCell.textContent = label;

    const inputCell = document.createElement('td');
    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'hotkey mono';
    input.readOnly = true;
    input.dataset.hotkey = name;
    input.value = state.hotkeys[name] || '';
    input.placeholder = 'Not set';
    inputCell.appendChild(input);

    const clearCell = document.createElement('td');
    clearCell.className = 'clear';
    const clear = document.createElement('button');
    clear.textContent = '✕';
    clear.title = 'Clear';
    clear.addEventListener('click', () => {
      state.hotkeys[name] = '';
      input.value = '';
      checkDuplicates();
    });
    clearCell.appendChild(clear);

    tr.append(nameCell, inputCell, clearCell);
    table.appendChild(tr);
  }
}

function checkDuplicates() {
  const used = new Map();
  for (const [name, accelerator] of Object.entries(state.hotkeys)) {
    if (!accelerator) continue;
    used.set(accelerator, (used.get(accelerator) || 0) + 1);
  }
  const clashes = [...used.entries()].filter(([, count]) => count > 1).map(([key]) => key);
  const warning = $('hotkeyWarning');
  warning.className = clashes.length ? 'hint warn' : 'hint';
  warning.textContent = clashes.length
    ? `The same combination is assigned more than once: ${clashes.join(', ')}`
    : '';
}

function fill() {
  $('screenshotFolder').value = state.screenshotFolder;
  $('gifFolder').value = state.gifFolder;
  $('afterCapture').value = state.afterCapture;
  $('imageFormat').value = state.imageFormat;
  $('jpegQuality').value = state.jpegQuality;
  $('copyPathAfterSave').checked = state.copyPathAfterSave;
  $('showNotification').checked = state.showNotification;
  $('launchAtLogin').checked = state.launchAtLogin;

  $('screenshotNamePattern').value = state.screenshotNamePattern;
  $('gifNamePattern').value = state.gifNamePattern;
  $('autoIncrementNumber').value = state.autoIncrementNumber;

  $('gifFps').value = state.gif.fps;
  $('gifMaxSeconds').value = state.gif.maxSeconds;
  $('gifMaxWidth').value = state.gif.maxWidth;
  $('gifRepeat').value = String(state.gif.repeat);

  document.querySelectorAll('[data-feature]').forEach((el) => {
    el.checked = state.features[el.dataset.feature] !== false;
  });

  renderHotkeys();
  checkDuplicates();
  refreshPreview();
}

function collect() {
  return {
    screenshotFolder: $('screenshotFolder').value.trim(),
    gifFolder: $('gifFolder').value.trim(),
    afterCapture: $('afterCapture').value,
    imageFormat: $('imageFormat').value,
    jpegQuality: clamp($('jpegQuality').value, 10, 100, 90),
    copyPathAfterSave: $('copyPathAfterSave').checked,
    showNotification: $('showNotification').checked,
    launchAtLogin: $('launchAtLogin').checked,
    screenshotNamePattern: $('screenshotNamePattern').value.trim() || 'ScreenX_%y-%mo-%d_%h-%mi-%s',
    gifNamePattern: $('gifNamePattern').value.trim() || 'ScreenX_%y-%mo-%d_%h-%mi-%s',
    autoIncrementNumber: clamp($('autoIncrementNumber').value, 0, 1e9, 0),
    gif: {
      fps: clamp($('gifFps').value, 1, 30, 15),
      maxSeconds: clamp($('gifMaxSeconds').value, 1, 600, 60),
      maxWidth: clamp($('gifMaxWidth').value, 0, 3840, 0),
      repeat: Number($('gifRepeat').value) === -1 ? -1 : 0
    },
    features: Object.fromEntries(
      [...document.querySelectorAll('[data-feature]')].map((el) => [el.dataset.feature, el.checked])
    ),
    hotkeys: { ...state.hotkeys }
  };
}

function clamp(value, min, max, fallback) {
  const n = Number(value);
  if (!Number.isFinite(n)) return fallback;
  return Math.min(max, Math.max(min, Math.round(n)));
}

let previewTimer = null;
function refreshPreview() {
  clearTimeout(previewTimer);
  previewTimer = setTimeout(async () => {
    const [shot, gif] = await Promise.all([
      window.screenx.preview($('screenshotNamePattern').value, 'image'),
      window.screenx.preview($('gifNamePattern').value, 'gif')
    ]);
    $('screenshotPreview').textContent = `Example: ${shot}`;
    $('gifPreview').textContent = `Example: ${gif}`;
  }, 150);
}

function status(text, warn) {
  const el = $('status');
  el.textContent = text;
  el.className = warn ? 'warn' : '';
  if (text) setTimeout(() => { if (el.textContent === text) el.textContent = ''; }, 4000);
}

// ------------------------------------------------------------------ events

document.querySelectorAll('nav button').forEach((button) => {
  button.addEventListener('click', () => {
    document.querySelectorAll('nav button').forEach((b) => b.classList.toggle('active', b === button));
    document.querySelectorAll('section').forEach((s) => s.classList.toggle('active', s.id === button.dataset.tab));
  });
});

document.querySelectorAll('[data-browse]').forEach((button) => {
  button.addEventListener('click', async () => {
    const input = $(button.dataset.browse);
    const chosen = await window.screenx.pickFolder(input.value);
    if (chosen) input.value = chosen;
  });
});

document.querySelectorAll('[data-open]').forEach((button) => {
  button.addEventListener('click', () => window.screenx.openFolder($(button.dataset.open).value));
});

['screenshotNamePattern', 'gifNamePattern', 'autoIncrementNumber', 'imageFormat']
  .forEach((id) => $(id).addEventListener('input', refreshPreview));

document.addEventListener('focusin', (e) => {
  if (!e.target.dataset || !e.target.dataset.hotkey) return;
  listeningFor = e.target;
  listeningFor.classList.add('listening');
  listeningFor.value = 'Press keys...';
});

document.addEventListener('focusout', (e) => {
  if (e.target !== listeningFor) return;
  listeningFor.classList.remove('listening');
  listeningFor.value = state.hotkeys[listeningFor.dataset.hotkey] || '';
  listeningFor = null;
});

window.addEventListener('keydown', (e) => {
  if (!listeningFor) {
    if ((e.metaKey || e.ctrlKey) && e.key === 's') { e.preventDefault(); save(); }
    if (e.key === 'Escape') window.screenx.close();
    return;
  }
  e.preventDefault();
  const name = listeningFor.dataset.hotkey;
  if (e.key === 'Backspace' || e.key === 'Delete') {
    state.hotkeys[name] = '';
    listeningFor.blur();
    checkDuplicates();
    return;
  }
  if (e.key === 'Escape') return listeningFor.blur();
  const accelerator = toAccelerator(e);
  if (!accelerator) return;
  state.hotkeys[name] = accelerator;
  listeningFor.blur();
  checkDuplicates();
});

async function save() {
  const { settings, conflicts } = await window.screenx.save(collect());
  state = settings;
  fill();
  status(conflicts.length
    ? `Saved, but the system refused: ${conflicts.join(', ')}`
    : 'Settings saved.', conflicts.length > 0);
}

$('save').addEventListener('click', save);
$('close').addEventListener('click', () => window.screenx.close());
$('reset').addEventListener('click', async () => {
  const defaults = await window.screenx.defaults();
  state = { ...defaults, autoIncrementNumber: state.autoIncrementNumber };
  fill();
  status('Defaults restored — press Save to keep them.');
});

(async () => {
  renderTokens();
  state = await window.screenx.get();
  fill();
})();

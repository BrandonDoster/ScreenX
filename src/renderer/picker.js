'use strict';

const grid = document.getElementById('grid');
const filter = document.getElementById('filter');
const title = document.getElementById('title');

let windows = [];
let active = 0;

function visible() {
  const needle = filter.value.trim().toLowerCase();
  return needle ? windows.filter((w) => w.name.toLowerCase().includes(needle)) : windows;
}

function render() {
  const list = visible();
  if (active >= list.length) active = Math.max(0, list.length - 1);
  grid.textContent = '';

  if (!list.length) {
    const empty = document.createElement('div');
    empty.id = 'empty';
    empty.textContent = 'No windows match.';
    grid.appendChild(empty);
    return;
  }

  list.forEach((win, index) => {
    const card = document.createElement('div');
    card.className = 'card' + (index === active ? ' active' : '');

    const shot = document.createElement('div');
    shot.className = 'shot';
    const img = document.createElement('img');
    img.className = 'shot-img';
    img.src = win.thumbnail;
    img.alt = '';
    shot.appendChild(img);

    const meta = document.createElement('div');
    meta.className = 'meta';
    if (win.appIcon) {
      const icon = document.createElement('img');
      icon.src = win.appIcon;
      icon.alt = '';
      meta.appendChild(icon);
    }
    const label = document.createElement('span');
    label.textContent = win.name;
    label.title = win.name;
    meta.appendChild(label);

    card.append(shot, meta);
    card.addEventListener('click', () => window.screenx.select(win.id));
    card.addEventListener('mouseenter', () => { active = index; paintActive(); });
    grid.appendChild(card);
  });
}

function paintActive() {
  [...grid.children].forEach((el, i) => el.classList.toggle('active', i === active));
}

window.screenx.onInit((payload) => {
  windows = payload.windows;
  title.textContent = payload.mode === 'record' ? 'Record which window?' : 'Capture which window?';
  render();
  filter.focus();
});

filter.addEventListener('input', () => { active = 0; render(); });

document.getElementById('cancel').addEventListener('click', () => window.screenx.cancel());

window.addEventListener('keydown', (e) => {
  const list = visible();
  if (e.key === 'Escape') return window.screenx.cancel();
  if (e.key === 'Enter' && list[active]) return window.screenx.select(list[active].id);
  const step = { ArrowRight: 1, ArrowLeft: -1, ArrowDown: 2, ArrowUp: -2 }[e.key];
  if (!step) return;
  e.preventDefault();
  active = Math.min(list.length - 1, Math.max(0, active + step));
  paintActive();
  grid.children[active] && grid.children[active].scrollIntoView({ block: 'nearest' });
});

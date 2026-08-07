'use strict';

const bar = document.getElementById('bar');
const time = document.getElementById('time');
const frames = document.getElementById('frames');
const stop = document.getElementById('stop');
const cancel = document.getElementById('cancel');

function clock(ms) {
  const total = Math.floor(ms / 1000);
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
}

window.screenx.onProgress(({ ms, frames: count }) => {
  time.textContent = clock(ms);
  frames.textContent = `${count} frames`;
});

window.screenx.onEncoding(() => {
  bar.classList.add('encoding');
  frames.textContent = 'encoding GIF...';
  stop.disabled = true;
  cancel.disabled = true;
});

stop.addEventListener('click', () => window.screenx.stop());
cancel.addEventListener('click', () => window.screenx.cancel());

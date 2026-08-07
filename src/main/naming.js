'use strict';

// Filename pattern expansion. Tokens are written as %name, optionally with a
// {n} argument (e.g. %i{4} or %ra{8}). Unknown tokens are left untouched so a
// typo is visible in the produced name instead of silently vanishing.

const MONTHS_SHORT = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const MONTHS_LONG = ['January', 'February', 'March', 'April', 'May', 'June', 'July',
  'August', 'September', 'October', 'November', 'December'];
const DAYS_SHORT = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const DAYS_LONG = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

const ALPHANUM = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
const HEX = '0123456789abcdef';

const pad = (n, width = 2) => String(n).padStart(width, '0');

function randomFrom(alphabet, length) {
  let out = '';
  for (let i = 0; i < length; i++) out += alphabet[Math.floor(Math.random() * alphabet.length)];
  return out;
}

function isoWeek(date) {
  // ISO-8601 week number: week 1 is the week containing the first Thursday.
  const d = new Date(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()));
  d.setUTCDate(d.getUTCDate() + 4 - (d.getUTCDay() || 7));
  const yearStart = new Date(Date.UTC(d.getUTCFullYear(), 0, 1));
  return Math.ceil(((d - yearStart) / 86400000 + 1) / 7);
}

// token -> (arg, context) => string
const TOKENS = {
  y: (_, c) => String(c.now.getFullYear()),
  yy: (_, c) => pad(c.now.getFullYear() % 100),
  mo: (_, c) => pad(c.now.getMonth() + 1),
  mon: (_, c) => MONTHS_SHORT[c.now.getMonth()],
  mon2: (_, c) => MONTHS_LONG[c.now.getMonth()],
  d: (_, c) => pad(c.now.getDate()),
  w: (_, c) => DAYS_SHORT[c.now.getDay()],
  w2: (_, c) => DAYS_LONG[c.now.getDay()],
  wy: (_, c) => pad(isoWeek(c.now)),
  h: (_, c) => pad(c.now.getHours()),
  h12: (_, c) => pad(c.now.getHours() % 12 || 12),
  mi: (_, c) => pad(c.now.getMinutes()),
  s: (_, c) => pad(c.now.getSeconds()),
  ms: (_, c) => pad(c.now.getMilliseconds(), 3),
  pm: (_, c) => (c.now.getHours() < 12 ? 'AM' : 'PM'),
  unix: (_, c) => String(Math.floor(c.now.getTime() / 1000)),

  i: (arg, c) => pad(c.counter, arg || 1),
  ra: (arg) => randomFrom(ALPHANUM, arg || 10),
  rn: (arg) => randomFrom('0123456789', arg || 10),
  rx: (arg) => randomFrom(HEX, arg || 10),
  guid: () => (globalThis.crypto && crypto.randomUUID
    ? crypto.randomUUID()
    : require('crypto').randomUUID()),

  t: (_, c) => c.title || '',
  width: (_, c) => String(c.width || 0),
  height: (_, c) => String(c.height || 0),
  pn: (_, c) => c.appName || 'ScreenX',
  un: (_, c) => c.userName || '',
  cn: (_, c) => c.computerName || ''
};

// Longest first so %mon2 wins over %mon and %mo.
const TOKEN_RE = new RegExp(
  '%(' + Object.keys(TOKENS).sort((a, b) => b.length - a.length).join('|') + ')(?:\\{(\\d{1,3})\\})?',
  'g'
);

/** Characters no mainstream filesystem accepts, plus control chars. */
function sanitizeSegment(value) {
  return String(value)
    .replace(/[\\/:*?"<>|\x00-\x1f]/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

/**
 * Expand a filename pattern.
 * @param {string} pattern
 * @param {object} context {now, counter, title, width, height, appName, userName, computerName}
 * @returns {string} filename without extension, safe for disk
 */
function parseName(pattern, context = {}) {
  const ctx = {
    now: context.now instanceof Date ? context.now : new Date(),
    counter: Number.isFinite(context.counter) ? context.counter : 1,
    ...context
  };

  // Tokens outside TOKENS never match the pattern, so a typo like %foo survives
  // into the filename where it is visible instead of silently disappearing.
  const expanded = String(pattern).replace(TOKEN_RE, (match, token, arg) =>
    sanitizeSegment(TOKENS[token](arg ? parseInt(arg, 10) : 0, ctx))
  );

  // Keep directory separators out but allow the rest through; collapse the
  // gaps a blank token (an empty window title) leaves behind.
  const cleaned = sanitizeSegment(expanded)
    .replace(/[-_ ]{2,}/g, (m) => m[0])
    .replace(/^[-_ .]+|[-_ .]+$/g, '');

  return cleaned || 'capture';
}

module.exports = { parseName, sanitizeSegment, TOKENS };

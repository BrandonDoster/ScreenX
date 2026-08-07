'use strict';

// Smallest thing that fails if the filename pattern logic breaks.
// Run with: npm test

const assert = require('assert');
const { parseName, sanitizeSegment } = require('../src/main/naming');

const now = new Date(2026, 6, 4, 9, 5, 3, 42); // Sat 4 July 2026, 09:05:03.042

assert.strictEqual(
  parseName('ScreenX_%y-%mo-%d_%h-%mi-%s', { now }),
  'ScreenX_2026-07-04_09-05-03'
);

assert.strictEqual(parseName('%yy%mon%mon2%w%w2', { now }), '26JulJulySatSaturday');
assert.strictEqual(parseName('%h12%pm-%ms', { now }), '09AM-042');
assert.strictEqual(parseName('%unix', { now }), String(Math.floor(now.getTime() / 1000)));

// Longest token wins: %mon2 must not be read as %mo followed by "n2".
assert.strictEqual(parseName('%mon2', { now }), 'July');
assert.strictEqual(parseName('%mo', { now }), '07');

// Padding argument.
assert.strictEqual(parseName('%i', { now, counter: 7 }), '7');
assert.strictEqual(parseName('%i{4}', { now, counter: 7 }), '0007');
assert.strictEqual(parseName('shot-%ra{6}', { now }).length, 'shot-'.length + 6);
assert.match(parseName('%rx{8}', { now }), /^[0-9a-f]{8}$/);

// Unknown tokens survive so a typo is visible in the filename.
assert.strictEqual(parseName('%nope', { now }), '%nope');

// Path separators and reserved characters never reach the filesystem.
assert.strictEqual(parseName('%t', { now, title: 'a/b\\c:d*e?f"g<h>i|j' }), 'abcdefghij');
assert.strictEqual(sanitizeSegment('../../etc/passwd'), '....etcpasswd');

// A blank title must not leave a dangling separator behind.
assert.strictEqual(parseName('shot_%t', { now, title: '' }), 'shot');
assert.strictEqual(parseName('%t-%width x %height', { now, title: '', width: 800, height: 600 }), '800 x 600');

// An empty result still produces a usable filename.
assert.strictEqual(parseName('', { now }), 'capture');
assert.strictEqual(parseName('%t', { now, title: '///' }), 'capture');

// ISO week: 1 Jan 2026 is a Thursday, so it belongs to week 1.
assert.strictEqual(parseName('%wy', { now: new Date(2026, 0, 1) }), '01');
assert.strictEqual(parseName('%wy', { now: new Date(2026, 0, 4) }), '01');
assert.strictEqual(parseName('%wy', { now: new Date(2026, 0, 5) }), '02');

console.log('naming: all assertions passed');

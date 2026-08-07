'use strict';

// Hidden window that owns the desktop video stream. Frames are grabbed on a
// timer, cropped/scaled through a canvas and handed to the GIF encoder living
// in the preload (where gifenc can be required).

const video = document.getElementById('source');
const canvas = document.createElement('canvas');
const ctx = canvas.getContext('2d', { willReadFrequently: true });

let stream = null;
let timer = null;
let startedAt = 0;
let frames = 0;
let source = null; // { x, y, width, height } in stream pixels
let limitMs = 0;
let intervalMs = 0;

function teardown() {
  clearInterval(timer);
  timer = null;
  if (stream) stream.getTracks().forEach((track) => track.stop());
  stream = null;
  video.srcObject = null;
}

function grab() {
  const timestamp = performance.now() - startedAt;
  ctx.drawImage(
    video,
    source.x, source.y, source.width, source.height,
    0, 0, canvas.width, canvas.height
  );
  const pixels = ctx.getImageData(0, 0, canvas.width, canvas.height);
  window.screenx.addFrame(pixels.data.buffer, timestamp);
  frames++;
  window.screenx.progress({ ms: timestamp, frames });
  if (limitMs && timestamp >= limitMs) stop();
}

function stop() {
  if (!timer && !stream) return;
  teardown();
  window.screenx.finish(intervalMs);
}

function cancel() {
  teardown();
  window.screenx.abort();
}

async function start(options) {
  try {
    const wanted = options.sourceSize;
    stream = await navigator.mediaDevices.getUserMedia({
      audio: false,
      video: {
        mandatory: {
          chromeMediaSource: 'desktop',
          chromeMediaSourceId: options.sourceId,
          maxFrameRate: Math.max(1, options.fps),
          ...(wanted ? { maxWidth: wanted.width, maxHeight: wanted.height } : { maxWidth: 4096, maxHeight: 4096 })
        }
      }
    });

    video.srcObject = stream;
    await video.play();
    // videoWidth is only meaningful once the first frame has been decoded.
    if (!video.videoWidth) {
      await new Promise((resolve) => video.addEventListener('loadeddata', resolve, { once: true }));
    }

    const streamWidth = video.videoWidth;
    const streamHeight = video.videoHeight;
    if (!streamWidth || !streamHeight) throw new Error('the capture stream produced no video');

    if (options.crop && wanted) {
      // The stream may come back at a different resolution than requested.
      const ratioX = streamWidth / wanted.width;
      const ratioY = streamHeight / wanted.height;
      source = {
        x: Math.max(0, Math.round(options.crop.x * ratioX)),
        y: Math.max(0, Math.round(options.crop.y * ratioY)),
        width: Math.round(options.crop.width * ratioX),
        height: Math.round(options.crop.height * ratioY)
      };
      source.width = Math.min(source.width, streamWidth - source.x);
      source.height = Math.min(source.height, streamHeight - source.y);
    } else {
      source = { x: 0, y: 0, width: streamWidth, height: streamHeight };
    }
    if (source.width < 2 || source.height < 2) throw new Error('the selected area is too small');

    const aspect = source.height / source.width;
    let outWidth = source.width;
    if (options.outputWidth > 0) outWidth = Math.min(outWidth, options.outputWidth);
    if (options.maxWidth > 0) outWidth = Math.min(outWidth, options.maxWidth);
    canvas.width = Math.max(2, Math.round(outWidth));
    canvas.height = Math.max(2, Math.round(outWidth * aspect));

    intervalMs = Math.round(1000 / Math.max(1, options.fps));
    limitMs = Math.max(0, options.maxSeconds) * 1000;
    frames = 0;
    startedAt = performance.now();

    window.screenx.begin(canvas.width, canvas.height, options.repeat);
    grab();
    timer = setInterval(grab, intervalMs);
  } catch (err) {
    teardown();
    window.screenx.abort();
    window.screenx.error(err && err.message ? err.message : err);
  }
}

window.screenx.onStart(start);
window.screenx.onStop(stop);
window.screenx.onCancel(cancel);

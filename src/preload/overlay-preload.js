'use strict';

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('screenx', {
  onInit: (fn) => ipcRenderer.on('overlay:init', (_e, payload) => fn(payload)),
  select: (displayId, rect) => ipcRenderer.send('overlay:select', { displayId, rect }),
  cancel: () => ipcRenderer.send('overlay:cancel')
});

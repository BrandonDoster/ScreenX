'use strict';

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('screenx', {
  onInit: (fn) => ipcRenderer.on('picker:init', (_e, payload) => fn(payload)),
  select: (id) => ipcRenderer.send('picker:select', id),
  cancel: () => ipcRenderer.send('picker:cancel')
});

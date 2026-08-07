'use strict';

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('screenx', {
  onProgress: (fn) => ipcRenderer.on('recordbar:progress', (_e, info) => fn(info)),
  onEncoding: (fn) => ipcRenderer.on('recordbar:encoding', () => fn()),
  stop: () => ipcRenderer.send('recordbar:stop'),
  cancel: () => ipcRenderer.send('recordbar:cancel')
});

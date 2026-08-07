'use strict';

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('screenx', {
  onLoad: (fn) => ipcRenderer.on('editor:load', (_e, payload) => fn(payload)),
  save: (dataURL, meta) => ipcRenderer.invoke('editor:save', { dataURL, meta }),
  saveAs: (dataURL, meta) => ipcRenderer.invoke('editor:saveAs', { dataURL, meta }),
  copy: (dataURL) => ipcRenderer.send('editor:copy', dataURL),
  reveal: (target) => ipcRenderer.send('shell:reveal', target),
  close: () => ipcRenderer.send('window:close')
});

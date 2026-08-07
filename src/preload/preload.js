'use strict';

const { contextBridge, ipcRenderer } = require('electron');

// Shared minimum for windows that only need to close themselves.
contextBridge.exposeInMainWorld('screenx', {
  close: () => ipcRenderer.send('window:close')
});

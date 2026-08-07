'use strict';

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('screenx', {
  get: () => ipcRenderer.invoke('settings:get'),
  save: (patch) => ipcRenderer.invoke('settings:save', patch),
  defaults: () => ipcRenderer.invoke('settings:defaults'),
  pickFolder: (current) => ipcRenderer.invoke('settings:pickFolder', current),
  preview: (pattern, kind) => ipcRenderer.invoke('settings:preview', { pattern, kind }),
  openFolder: (folder) => ipcRenderer.send('settings:openFolder', folder),
  close: () => ipcRenderer.send('window:close')
});

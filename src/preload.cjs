'use strict';
const {contextBridge,ipcRenderer}=require('electron');
const call=(channel,...args)=>ipcRenderer.invoke(channel,...args).then(result=>{if(!result.ok)throw Error(result.error);return result.value;});
const subscribe=(channel,fn)=>{const handler=(_event,value)=>fn(value);ipcRenderer.on(channel,handler);return ()=>ipcRenderer.removeListener(channel,handler);};
contextBridge.exposeInMainWorld('brain',Object.freeze({
 state:()=>call('app:state'),saveNote:n=>call('notes:save',n),deleteNote:id=>call('notes:delete',id),
 saveSettings:value=>call('settings:save',value),setupVault:p=>call('vault:setup',p),unlockVault:p=>call('vault:unlock',p),
 listVault:()=>call('vault:list'),saveAccount:n=>call('vault:save',n),deleteAccount:id=>call('vault:delete',id),
 lockVault:()=>call('vault:lock'),changePassword:(old,next)=>call('vault:change-password',old,next),generatePassword:()=>call('vault:generate'),copyAccount:(id,field)=>call('vault:copy',id,field),
 activity:()=>call('app:activity'),hide:()=>call('app:hide'),cancelHide:()=>call('app:hide-cancelled'),quit:()=>call('app:quit'),
 openDataDirectory:()=>call('app:data-directory'),exportBackup:()=>call('backup:export'),importBackup:()=>call('backup:import'),
 checkUpdate:()=>call('updates:check'),openUpdate:()=>call('updates:open'),openRepository:()=>call('repository:open'),
 onLocked:fn=>subscribe('vault:locked',fn),onShown:fn=>subscribe('window:shown',fn),onPrepareHide:fn=>subscribe('window:prepare-hide',fn),onPrepareQuit:fn=>subscribe('app:prepare-quit',fn),onNotice:fn=>subscribe('app:notice',fn)
}));

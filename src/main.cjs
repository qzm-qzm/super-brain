'use strict';
const {app,BrowserWindow,Tray,Menu,ipcMain,globalShortcut,dialog,clipboard,shell,powerMonitor,nativeImage,screen}=require('electron');
const path=require('node:path');
const fs=require('node:fs/promises');
const crypto=require('node:crypto');
const {pathToFileURL}=require('node:url');
const {LocalStore,atomicWrite,readJson,entries,settings,MAX_BACKUP}=require('./storage.cjs');
const {Vault,envelope}=require('./vault.cjs');

const REPOSITORY='https://github.com/qzm-qzm/super-brain';
const APP_URL=pathToFileURL(path.join(__dirname,'renderer','index.html')).href;
const testMode=process.env.SUPER_BRAIN_TEST==='1';
app.setName('超强大脑');
app.setPath('userData',testMode&&process.env.SUPER_BRAIN_TEST_DATA_DIR?path.resolve(process.env.SUPER_BRAIN_TEST_DATA_DIR):path.join(app.getPath('appData'),'SuperBrain'));
let win,tray,store,vault,quitting=false,activeShortcut='',lastActivity=Date.now(),clipboardTimer,copiedDigest,hidePending=false,updateBusy=false;
function broadcast(channel,value){if(win&&!win.isDestroyed())win.webContents.send(channel,value);}
function digest(value){return crypto.createHash('sha256').update(value).digest('hex');}
function clearOwnClipboard(){clearTimeout(clipboardTimer);if(copiedDigest&&digest(clipboard.readText())===copiedDigest)clipboard.clear();copiedDigest=null;}
function lockVault(reason='manual'){vault?.lock();clearOwnClipboard();broadcast('vault:locked',reason);vault?.flush().catch(error=>broadcast('app:notice','密码库保存失败：'+error.message));}
function revealWindow(){if(!win)return;const display=screen.getDisplayNearestPoint(screen.getCursorScreenPoint()).workArea;const bounds=win.getBounds();if(!screen.getAllDisplays().some(d=>bounds.x+40>d.workArea.x&&bounds.x<d.workArea.x+d.workArea.width&&bounds.y+40>d.workArea.y&&bounds.y<d.workArea.y+d.workArea.height))win.setPosition(Math.round(display.x+(display.width-bounds.width)/2),Math.round(display.y+(display.height-bounds.height)/2));win.show();if(win.isMinimized())win.restore();win.focus();lastActivity=Date.now();broadcast('window:shown');}
function requestHide(){if(hidePending)return;hidePending=true;broadcast('window:prepare-hide');setTimeout(()=>{hidePending=false;},5000).unref();}
function toggleWindow(){if(win.isVisible()&&win.isFocused())requestHide();else revealWindow();}
function registerShortcut(shortcut){if(shortcut===activeShortcut)return;const previous=activeShortcut;if(!globalShortcut.register(shortcut,toggleWindow))throw Error(`快捷键 ${shortcut} 已被占用，请换一个`);if(previous)globalShortcut.unregister(previous);activeShortcut=shortcut;}
function autoStart(value){if(testMode)return;if(value&&process.env.PORTABLE_EXECUTABLE_FILE)throw Error('免安装版暂不支持开机启动，请使用安装版');app.setLoginItemSettings({openAtLogin:value,path:process.execPath,args:['--hidden']});}
async function applySettings(value){const next=settings(value);const old=store.snapshot().settings;if(next.shortcut!==old.shortcut)registerShortcut(next.shortcut);try{if(next.autoStart!==old.autoStart)autoStart(next.autoStart);await store.saveSettings(next);}catch(e){if(activeShortcut!==old.shortcut){globalShortcut.unregister(activeShortcut);activeShortcut='';try{registerShortcut(old.shortcut);}catch{}}if(next.autoStart!==old.autoStart)try{autoStart(old.autoStart);}catch{}throw e;}win.setAlwaysOnTop(next.alwaysOnTop);lastActivity=Date.now();return next;}
function state(){const data=store.snapshot();return {...data,vault:vault.status(),version:app.getVersion(),repository:REPOSITORY,shortcutActive:!!activeShortcut};}
function handle(channel,fn){ipcMain.handle(channel,async(event,...args)=>{
 if(event.sender!==win?.webContents||event.senderFrame!==win.webContents.mainFrame||event.senderFrame.url!==APP_URL)return {ok:false,error:'无法处理此来源的请求'};
 try{return {ok:true,value:await fn(...args)};}catch(e){return {ok:false,error:e.message||'操作未完成，请重试'};}
});}
function setupIPC(){
 handle('app:state',()=>state());
 handle('notes:save',note=>store.saveNote(note));
 handle('notes:delete',id=>{if(typeof id!=='string')throw Error('记录编号不正确');return store.deleteNote(id);});
 handle('settings:save',value=>applySettings(value));
 handle('vault:setup',async password=>{lastActivity=Date.now();return vault.setup(password);});
 handle('vault:unlock',async password=>{lastActivity=Date.now();return vault.unlock(password);});
 handle('vault:list',()=>vault.list());
 handle('vault:save',value=>{lastActivity=Date.now();return vault.save(value);});
 handle('vault:delete',id=>{if(typeof id!=='string')throw Error('记录编号不正确');lastActivity=Date.now();return vault.delete(id);});
 handle('vault:lock',()=>lockVault());
 handle('vault:change-password',(oldPassword,newPassword)=>vault.changePassword(oldPassword,newPassword));
 handle('vault:generate',()=>{const chars='ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%&*-_';return Array.from({length:24},()=>chars[crypto.randomInt(chars.length)]).join('');});
 handle('vault:copy',(id,field)=>{if(!['username','password'].includes(field))throw Error('不支持复制此字段');const item=vault.list().find(x=>x.id===id);if(!item)throw Error('账号记录不存在');const value=item[field];clearTimeout(clipboardTimer);clipboard.writeText(value);copiedDigest=digest(value);clipboardTimer=setTimeout(clearOwnClipboard,30000);clipboardTimer.unref();lastActivity=Date.now();return true;});
 handle('app:activity',()=>{lastActivity=Date.now();});
 handle('app:hide',async()=>{await store.queue;await vault.flush();hidePending=false;lockVault('hidden');win.hide();});
 handle('app:hide-cancelled',()=>{hidePending=false;});
 handle('app:quit',async()=>{await store.queue;await vault.flush();quitting=true;app.quit();});
 handle('app:data-directory',()=>shell.openPath(app.getPath('userData')));
 handle('backup:export',async()=>{await store.queue;await vault.flush();const choice=await dialog.showSaveDialog(win,{title:'导出备份（密码保持加密）',defaultPath:path.join(app.getPath('documents'),`SuperBrain-backup-${new Date().toISOString().slice(0,10)}.json`),filters:[{name:'超强大脑备份',extensions:['json']}]});if(choice.canceled)return {cancelled:true};await atomicWrite(choice.filePath,{format:'super-brain.backup',version:1,createdAt:new Date().toISOString(),data:store.snapshot(),vault:vault.backup()},MAX_BACKUP);return {cancelled:false};});
 handle('backup:import',async()=>{
  const choice=await dialog.showOpenDialog(win,{title:'选择超强大脑备份',properties:['openFile'],filters:[{name:'超强大脑备份',extensions:['json']}]});if(choice.canceled)return {cancelled:true};
  const backup=await readJson(choice.filePaths[0],MAX_BACKUP);if(backup?.format!=='super-brain.backup'||backup.version!==1||backup.data?.version!==1)throw Error('这不是受支持的超强大脑备份');
  const clean={notes:entries(backup.data.notes),settings:settings(backup.data.settings)};const record=backup.vault?envelope(backup.vault):null;
  const answer=await dialog.showMessageBox(win,{type:'question',title:'恢复备份',message:'用备份替换当前备忘录和密码库？',detail:'当前资料会先备份到数据目录。恢复后，密码库需要用该备份原来的主密码解锁。',buttons:['取消','恢复备份'],defaultId:0,cancelId:0});if(answer.response!==1)return {cancelled:true};
  await store.queue;await vault.flush();const old=store.snapshot(),oldVault=vault.backup();
  await atomicWrite(path.join(app.getPath('userData'),`before-restore-${Date.now()}.json`),{format:'super-brain.backup',version:1,createdAt:new Date().toISOString(),data:old,vault:oldVault},MAX_BACKUP);
  // Imported data cannot silently modify OS startup or take a new global shortcut.
  clean.settings.autoStart=old.settings.autoStart;clean.settings.shortcut=old.settings.shortcut;
  lockVault('restore');
  try{await vault.restore(record);await store.restore(clean);win.setAlwaysOnTop(clean.settings.alwaysOnTop);}catch(e){await vault.restore(oldVault);await store.restore(old);throw e;}
  return {cancelled:false,state:state()};
 });
 handle('updates:check',async()=>{if(updateBusy)throw Error('正在检查更新');updateBusy=true;try{const response=await fetch('https://api.github.com/repos/qzm-qzm/super-brain/releases/latest',{headers:{Accept:'application/vnd.github+json','User-Agent':'SuperBrain/'+app.getVersion()},signal:AbortSignal.timeout(12000)});if(response.status===404)return {available:false,message:'暂时没有已发布的新版本'};if(!response.ok)throw Error('暂时无法检查更新，请稍后重试');const release=await response.json();const latest=String(release.tag_name||'').replace(/^v/,'');if(!/^\d+\.\d+\.\d+$/.test(latest))throw Error('版本信息格式不正确');const a=latest.split('.').map(Number),b=app.getVersion().split('.').map(Number);const newer=a.some((v,i)=>v>b[i]&&a.slice(0,i).every((n,j)=>n===b[j]));return {available:newer,version:latest,message:newer?`发现新版本 ${latest}`:'当前已是最新版本'};}catch(e){if(e.name==='TimeoutError'||e.name==='TypeError')throw Error('网络连接失败，请稍后重试');throw e;}finally{updateBusy=false;}});
 handle('updates:open',()=>shell.openExternal(REPOSITORY+'/releases/latest'));
 handle('repository:open',()=>shell.openExternal(REPOSITORY));
}
function createWindow(){
 win=new BrowserWindow({width:480,height:700,minWidth:380,minHeight:540,frame:false,show:false,resizable:true,backgroundColor:'#F6F8FB',title:'超强大脑',icon:path.join(__dirname,'../assets/icon.png'),webPreferences:{preload:path.join(__dirname,'preload.cjs'),contextIsolation:true,nodeIntegration:false,sandbox:true,devTools:!app.isPackaged,spellcheck:false,backgroundThrottling:false}});
 win.setAlwaysOnTop(store.snapshot().settings.alwaysOnTop);
 win.webContents.on('did-start-navigation',(_event,_url,_inPlace,isMainFrame)=>{if(isMainFrame)lockVault('renderer-reload');});
 win.webContents.on('render-process-gone',()=>lockVault('renderer-gone'));
 win.webContents.setWindowOpenHandler(()=>({action:'deny'}));
 win.webContents.on('will-navigate',(event,url)=>{if(url!==APP_URL)event.preventDefault();});
 win.webContents.session.setPermissionRequestHandler((_webContents,_permission,callback)=>callback(false));
 win.webContents.session.setPermissionCheckHandler(()=>false);
 win.on('close',event=>{if(!quitting){event.preventDefault();requestHide();}});
 win.on('minimize',event=>{event.preventDefault();requestHide();});
 win.once('ready-to-show',()=>{if(!testMode&&!process.argv.includes('--hidden'))revealWindow();});
 win.loadFile(path.join(__dirname,'renderer','index.html'));
 const image=nativeImage.createFromPath(path.join(__dirname,'../assets/icon.png')).resize({width:20,height:20});
 tray=new Tray(image);tray.setToolTip('超强大脑');tray.on('click',toggleWindow);
 tray.setContextMenu(Menu.buildFromTemplate([{label:'打开超强大脑',click:revealWindow},{label:'锁定密码库',click:()=>lockVault()},{type:'separator'},{label:'退出',click:()=>{revealWindow();broadcast('app:prepare-quit');}}]));
}
if(!app.requestSingleInstanceLock()){app.quit();}else{
 app.on('second-instance',()=>revealWindow());
 app.whenReady().then(async()=>{
  store=new LocalStore(app.getPath('userData'));vault=new Vault(app.getPath('userData'));await store.init();await vault.init();Menu.setApplicationMenu(null);setupIPC();createWindow();
  try{registerShortcut(store.snapshot().settings.shortcut);}catch(e){win.webContents.once('did-finish-load',()=>broadcast('app:notice',e.message));}
  powerMonitor.on('lock-screen',()=>lockVault('screen-lock'));powerMonitor.on('suspend',()=>lockVault('suspend'));
  setInterval(()=>{if(vault.status().unlocked&&Date.now()-lastActivity>store.snapshot().settings.autoLockMinutes*60000)lockVault('idle');},1000).unref();
 }).catch(error=>{dialog.showErrorBox('超强大脑启动失败',error.message);app.quit();});
 app.on('before-quit',()=>{quitting=true;lockVault('quit');});
 app.on('will-quit',()=>globalShortcut.unregisterAll());
 app.on('window-all-closed',()=>{});
}

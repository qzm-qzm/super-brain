'use strict';
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');

const MAX_FILE = 16 * 1024 * 1024;
const MAX_BACKUP = 2 * MAX_FILE + 1024 * 1024;
const DEFAULT_SETTINGS = Object.freeze({
  background:'#F6F8FB', accent:'#48648E', image:'', imageTransparency:90,
  shortcut:'Alt+Q', autoStart:false, autoLockMinutes:5, alwaysOnTop:false
});
function plain(value) { return value && typeof value==='object' && !Array.isArray(value); }
function text(value, limit, label) {
  if (typeof value!=='string' || value.length>limit) throw Error(`${label}格式不正确或内容过长`);
  return value;
}
function settings(value) {
  if(!plain(value))throw Error('设置格式不正确');
  const v={...DEFAULT_SETTINGS};
  for(const k of ['background','accent']) if(k in value){if(!/^#[0-9a-f]{6}$/i.test(value[k]))throw Error('颜色格式不正确');v[k]=value[k].toUpperCase();}
  if('image' in value){const image=text(value.image,2900000,'背景图片');if(image&&!/^data:image\/(png|jpeg|webp);base64,[a-z0-9+/=]+$/i.test(image))throw Error('背景图片格式不正确');v.image=image;}
  if('imageTransparency' in value){if(!Number.isInteger(value.imageTransparency)||value.imageTransparency<0||value.imageTransparency>100)throw Error('透明度应为 0–100');v.imageTransparency=value.imageTransparency;}
  if('shortcut' in value){const shortcut=text(value.shortcut,60,'快捷键');if(!/^(?:(?:CommandOrControl|Control|Ctrl|Alt|Shift|Super)\+){0,3}(?:[A-Z0-9]|F(?:[1-9]|1[0-9]|2[0-4])|Space)$/i.test(shortcut))throw Error('快捷键格式不正确，例如 Alt+Q 或 F8');if(!shortcut.includes('+')&&!/^F(?:[1-9]|1[0-9]|2[0-4])$/i.test(shortcut))throw Error('单键快捷键请使用 F1–F24');v.shortcut=shortcut;}
  for(const k of ['autoStart','alwaysOnTop'])if(k in value){if(typeof value[k]!=='boolean')throw Error('设置值不正确');v[k]=value[k];}
  if('autoLockMinutes' in value){if(![1,5,10,15,30].includes(value.autoLockMinutes))throw Error('自动锁定时间不正确');v.autoLockMinutes=value.autoLockMinutes;}
  return v;
}
function entry(value, secret=false) {
  if(!plain(value))throw Error('记录格式不正确');
  const result={
    id:text(value.id,80,'记录编号'),title:text(value.title,120,'标题'),body:text(value.body,100000,'正文'),
    pinned:!!value.pinned,createdAt:text(value.createdAt,40,'创建时间'),updatedAt:text(value.updatedAt,40,'修改时间')
  };
  if(!result.id||!Number.isFinite(Date.parse(result.createdAt))||!Number.isFinite(Date.parse(result.updatedAt)))throw Error('记录日期或编号不正确');
  if(secret)for(const k of ['username','password','url'])result[k]=text(value[k],k==='password'?4096:2048,'账号信息');
  return result;
}
function entries(value,secret=false) {
  if(!Array.isArray(value)||value.length>5000)throw Error('记录列表格式不正确，最多 5000 条');
  const list=value.map(v=>entry(v,secret));
  if(new Set(list.map(v=>v.id)).size!==list.length)throw Error('存在重复记录编号');
  return list;
}
async function readJson(filename,maxBytes=MAX_FILE) {
  let handle;
  try { handle=await fs.open(filename,'r'); const stat=await handle.stat();if(stat.size>maxBytes)throw Error('文件过大，无法读取'); return JSON.parse(await handle.readFile('utf8')); }
  catch(e){if(e.code==='ENOENT')return null;throw Error(`无法读取 ${path.basename(filename)}，文件未被覆盖，请从备份恢复。`);}
  finally{await handle?.close();}
}
async function atomicWrite(filename,value,maxBytes=MAX_FILE) {
  await fs.mkdir(path.dirname(filename),{recursive:true});
  const serialized=JSON.stringify(value,null,2);
  if(Buffer.byteLength(serialized)>maxBytes)throw Error('数据过大，保存失败');
  const temporary=filename+'.'+crypto.randomUUID()+'.tmp';
  let handle;
  try {
    handle=await fs.open(temporary,'wx',0o600);await handle.writeFile(serialized,'utf8');await handle.sync();await handle.close();handle=null;
    try{await fs.copyFile(filename,filename+'.bak');}catch(e){if(e.code!=='ENOENT')throw e;}
    await fs.rename(temporary,filename);
  }finally{await handle?.close();await fs.rm(temporary,{force:true}).catch(()=>{});}
}
class LocalStore {
  constructor(directory){this.directory=directory;this.filename=path.join(directory,'notes.json');this.data=null;this.queue=Promise.resolve();}
  async init(){await fs.mkdir(this.directory,{recursive:true});const saved=await readJson(this.filename);if(saved===null){this.data={version:1,notes:[],settings:{...DEFAULT_SETTINGS}};await atomicWrite(this.filename,this.data);}else{if(saved.version!==1)throw Error('备忘录数据版本不受支持');this.data={version:1,notes:entries(saved.notes),settings:settings(saved.settings)};}}
  snapshot(){return structuredClone(this.data);}
  mutate(fn){const job=this.queue.then(async()=>{const next=this.snapshot();const result=await fn(next);await atomicWrite(this.filename,next);this.data=next;return result;});this.queue=job.catch(()=>{});return job;}
  saveNote(value){const note=entry(value);return this.mutate(data=>{const index=data.notes.findIndex(n=>n.id===note.id);if(index<0){if(data.notes.length>=5000)throw Error('最多保存 5000 条备忘录');data.notes.unshift(note);}else data.notes[index]=note;return note;});}
  deleteNote(id){return this.mutate(data=>{data.notes=data.notes.filter(n=>n.id!==id);});}
  saveSettings(value){const clean=settings(value);return this.mutate(data=>{data.settings=clean;return clean;});}
  restore(value){const clean={version:1,notes:entries(value.notes),settings:settings(value.settings)};return this.mutate(data=>{Object.assign(data,clean);});}
}
module.exports={LocalStore,atomicWrite,readJson,settings,entry,entries,DEFAULT_SETTINGS,MAX_FILE,MAX_BACKUP};

'use strict';
const crypto=require('node:crypto');
const path=require('node:path');
const {promisify}=require('node:util');
const {atomicWrite,readJson,entries,entry}=require('./storage.cjs');
const scrypt=promisify(crypto.scrypt);
const KDF=Object.freeze({name:'scrypt',N:131072,r:8,p:1});
const AAD=Buffer.from('super-brain:vault:1');
function decode(value,length,label){if(typeof value!=='string'||!value.length||value.length>24000000||!/^[A-Za-z0-9+/]+={0,2}$/.test(value))throw Error(`密码库${label}格式不正确`);const result=Buffer.from(value,'base64');if((length&&result.length!==length)||result.toString('base64')!==value)throw Error(`密码库${label}格式不正确`);return result;}
function envelope(value){
 if(!value||value.format!=='super-brain.vault'||value.version!==1||value.kdf?.name!=='scrypt'||value.kdf.N!==KDF.N||value.kdf.r!==KDF.r||value.kdf.p!==KDF.p||value.cipher?.name!=='aes-256-gcm')throw Error('密码库文件格式或版本不受支持');
 decode(value.kdf.salt,32,'盐值');decode(value.cipher.iv,12,'随机数');decode(value.cipher.tag,16,'认证码');decode(value.cipher.data,0,'内容');
 return {format:'super-brain.vault',version:1,kdf:{...KDF,salt:value.kdf.salt},cipher:{name:'aes-256-gcm',iv:value.cipher.iv,tag:value.cipher.tag,data:value.cipher.data}};
}
function passphrase(password,isNew=false){if(typeof password!=='string'||password.length>1024||(!password.length))throw Error('请输入主密码');if(isNew&&password.length<12)throw Error('主密码至少需要 12 个字符，可使用较长的中文短句');return password;}
async function derive(password,salt){return scrypt(passphrase(password),salt,32,{N:KDF.N,r:KDF.r,p:KDF.p,maxmem:256*1024*1024});}
function encrypt(list,key,salt){const iv=crypto.randomBytes(12);const cipher=crypto.createCipheriv('aes-256-gcm',key,iv);cipher.setAAD(AAD);const plain=Buffer.from(JSON.stringify(entries(list,true)));try{const data=Buffer.concat([cipher.update(plain),cipher.final()]);return {format:'super-brain.vault',version:1,kdf:{...KDF,salt:salt.toString('base64')},cipher:{name:'aes-256-gcm',iv:iv.toString('base64'),tag:cipher.getAuthTag().toString('base64'),data:data.toString('base64')}};}finally{plain.fill(0);}}
function decrypt(value,key){let plain;try{const decipher=crypto.createDecipheriv('aes-256-gcm',key,Buffer.from(value.cipher.iv,'base64'));decipher.setAAD(AAD);decipher.setAuthTag(Buffer.from(value.cipher.tag,'base64'));plain=Buffer.concat([decipher.update(Buffer.from(value.cipher.data,'base64')),decipher.final()]);return entries(JSON.parse(plain.toString('utf8')),true);}catch{throw Error('主密码不正确，或密码库文件已损坏');}finally{plain?.fill(0);}}
class Vault {
 constructor(directory){this.filename=path.join(directory,'vault.enc.json');this.record=null;this.persistedRecord=null;this.key=null;this.items=[];this.epoch=0;this.queue=Promise.resolve();this.busy=false;}
 async init(){const value=await readJson(this.filename);this.record=value?envelope(value):null;this.persistedRecord=this.record;}
 status(){return {exists:!!this.record,unlocked:!!this.key};}
 lock(){this.epoch++;this.key?.fill(0);this.key=null;this.items=[];}
 list(){if(!this.key)throw Error('密码库已锁定，请先解锁');return structuredClone(this.items);}
 serialize(fn){const task=this.queue.then(fn);this.queue=task.catch(()=>{});return task;}
 exclusive(fn){if(this.busy)return Promise.reject(Error('密码库正在处理，请稍后重试'));this.busy=true;return this.serialize(fn).finally(()=>{this.busy=false;});}
 setup(password){passphrase(password,true);const epoch=this.epoch;return this.exclusive(async()=>{if(this.record)throw Error('密码库已经创建');const salt=crypto.randomBytes(32);const key=await derive(password,salt);try{if(epoch!==this.epoch)throw Error('解锁已取消，请重新打开密码库');const record=encrypt([],key,salt);await atomicWrite(this.filename,record);this.record=record;this.persistedRecord=record;if(epoch!==this.epoch)throw Error('密码库已创建并锁定');this.key=Buffer.from(key);this.items=[];return this.list();}finally{key.fill(0);}});}
 unlock(password){passphrase(password);const epoch=this.epoch;return this.exclusive(async()=>{if(!this.record)throw Error('请先创建密码库');const key=await derive(password,Buffer.from(this.record.kdf.salt,'base64'));try{const list=decrypt(this.record,key);if(epoch!==this.epoch)throw Error('解锁已取消，请重新输入主密码');this.key?.fill(0);this.key=Buffer.from(key);this.items=list;return this.list();}finally{key.fill(0);}});}
 update(fn){if(!this.key)return Promise.reject(Error('密码库已锁定，请先解锁'));if(this.busy)return Promise.reject(Error('密码库正在处理，请稍后重试'));const list=this.list();const result=fn(list);const record=encrypt(list,this.key,Buffer.from(this.record.kdf.salt,'base64'));this.items=list;this.record=record;return this.serialize(async()=>{await atomicWrite(this.filename,record);this.persistedRecord=record;return result;});}
 async flush(){await this.queue;if(this.record!==this.persistedRecord)await this.serialize(async()=>{const latest=this.record;if(latest&&latest!==this.persistedRecord){await atomicWrite(this.filename,latest);this.persistedRecord=latest;}});}
 save(value){const item=entry(value,true);return this.update(list=>{const i=list.findIndex(n=>n.id===item.id);if(i<0){if(list.length>=5000)throw Error('最多保存 5000 条账号记录');list.unshift(item);}else list[i]=item;return item;});}
 delete(id){return this.update(list=>{const i=list.findIndex(n=>n.id===id);if(i>=0)list.splice(i,1);});}
 changePassword(oldPassword,newPassword){passphrase(oldPassword);passphrase(newPassword,true);const epoch=this.epoch;return this.exclusive(async()=>{if(!this.record||!this.key)throw Error('请先解锁密码库');const oldKey=await derive(oldPassword,Buffer.from(this.record.kdf.salt,'base64'));let key;try{const list=decrypt(this.record,oldKey);if(epoch!==this.epoch)throw Error('密码库已锁定');const salt=crypto.randomBytes(32);key=await derive(newPassword,salt);if(epoch!==this.epoch)throw Error('密码库已锁定');const record=encrypt(list,key,salt);await atomicWrite(this.filename,record);this.record=record;this.persistedRecord=record;if(epoch===this.epoch){this.key?.fill(0);this.key=Buffer.from(key);this.items=list;}}finally{oldKey.fill(0);key?.fill(0);}});}
 backup(){return this.persistedRecord?structuredClone(this.persistedRecord):null;}
 restore(value){const record=value?envelope(value):null;if(this.busy)return Promise.reject(Error('密码库正在处理，请稍后重试'));this.lock();return this.exclusive(async()=>{if(record)await atomicWrite(this.filename,record);else{const fs=require('node:fs/promises');try{await fs.copyFile(this.filename,this.filename+'.bak');await fs.unlink(this.filename);}catch(e){if(e.code!=='ENOENT')throw e;}}this.record=record;this.persistedRecord=record;});}
}
module.exports={Vault,envelope,encrypt,decrypt,derive,KDF};

const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const {Vault,envelope}=require('../src/vault.cjs');
const PASSWORD='TEST ONLY master phrase 2026';
const account={id:'demo-id',title:'测试账号',username:'demo@example.com',password:'TEST-SECRET-never-plaintext',url:'https://example.com',body:'private test note',pinned:false,createdAt:'2026-09-22T00:00:00.000Z',updatedAt:'2026-09-22T00:00:00.000Z'};
async function temporary(t){const root=path.resolve(__dirname,'../test-results');await fs.mkdir(root,{recursive:true});const dir=await fs.mkdtemp(path.join(root,'vault-'));t.after(async()=>{assert.equal(path.dirname(dir),root);await fs.rm(dir,{recursive:true,force:true});});return dir;}
test('encryption survives restart; disk and backup contain no plaintext passwords',async t=>{const dir=await temporary(t),vault=new Vault(dir);await vault.init();await vault.setup(PASSWORD);await vault.save(account);const disk=await fs.readFile(vault.filename,'utf8');for(const secret of [PASSWORD,account.password,account.username,account.body])assert.ok(!disk.includes(secret));const old=await fs.readFile(vault.filename+'.bak','utf8');assert.ok(!old.includes(PASSWORD));vault.lock();assert.throws(()=>vault.list(),/锁定/);const restarted=new Vault(dir);await restarted.init();assert.equal(restarted.status().unlocked,false);await assert.rejects(restarted.unlock('wrong-password'),/不正确/);await restarted.unlock(PASSWORD);assert.equal(restarted.list()[0].password,account.password);restarted.lock();});
test('tampering and unreasonable KDF parameters are rejected',async t=>{const dir=await temporary(t),vault=new Vault(dir);await vault.init();await vault.setup(PASSWORD);await vault.save(account);const tampered=vault.backup();const bytes=Buffer.from(tampered.cipher.data,'base64');bytes[0]^=1;tampered.cipher.data=bytes.toString('base64');await vault.restore(tampered);await assert.rejects(vault.unlock(PASSWORD),/损坏/);const bad=vault.backup();bad.kdf.N=2147483648;assert.throws(()=>envelope(bad),/不受支持/);});
test('changing master password rejects old password and preserves data',async t=>{const dir=await temporary(t),vault=new Vault(dir);await vault.init();await vault.setup(PASSWORD);await vault.save(account);await vault.changePassword(PASSWORD,'TEST ONLY replacement phrase');vault.lock();await assert.rejects(vault.unlock(PASSWORD),/不正确/);await vault.unlock('TEST ONLY replacement phrase');assert.equal(vault.list()[0].password,account.password);vault.lock();});
test('locking while scrypt is running cannot reopen vault after lock',async t=>{const dir=await temporary(t),vault=new Vault(dir);await vault.init();await vault.setup(PASSWORD);vault.lock();const pending=vault.unlock(PASSWORD);vault.lock();await assert.rejects(pending,/取消/);assert.equal(vault.status().unlocked,false);});
test('concurrent vault edits serialize without losing unrelated accounts',async t=>{const dir=await temporary(t),vault=new Vault(dir);await vault.init();await vault.setup(PASSWORD);await Promise.all(Array.from({length:8},(_,i)=>vault.save({...account,id:'account-'+i})));vault.lock();await vault.unlock(PASSWORD);assert.equal(vault.list().length,8);vault.lock();});
test('short master passwords are rejected before creating a file',async t=>{const dir=await temporary(t),vault=new Vault(dir);await vault.init();assert.throws(()=>vault.setup('short'),/12/);assert.equal(vault.status().exists,false);});

test('an edit accepted immediately before lock persists; edits after lock are rejected',async t=>{
 const dir=await temporary(t),vault=new Vault(dir);await vault.init();await vault.setup(PASSWORD);
 let release;const barrier=new Promise(resolve=>{release=resolve;});vault.serialize(()=>barrier);
 const accepted=vault.save({...account,password:'accepted immediately before lock'});
 try{
  vault.lock();assert.equal(vault.status().unlocked,false);assert.throws(()=>vault.list(),/锁定/);
  await assert.rejects(vault.save({...account,password:'must never be saved'}),/锁定/);
  await assert.rejects(vault.delete(account.id),/锁定/);
 }finally{release();}
 await accepted;await vault.flush();
 const restarted=new Vault(dir);await restarted.init();await restarted.unlock(PASSWORD);
 assert.equal(restarted.list().length,1);assert.equal(restarted.list()[0].password,'accepted immediately before lock');restarted.lock();
});

test('earlier queued writes cannot replace newer accepted snapshots',async t=>{
 const dir=await temporary(t),vault=new Vault(dir);await vault.init();await vault.setup(PASSWORD);
 let releaseFirst,releaseSecond;
 const firstBarrier=new Promise(resolve=>{releaseFirst=resolve;});
 const secondBarrier=new Promise(resolve=>{releaseSecond=resolve;});
 vault.serialize(()=>firstBarrier);
 const first=vault.save({...account,id:'first'});
 vault.serialize(()=>secondBarrier);
 const second=vault.save({...account,id:'second'});
 let third;
 try{
  assert.deepEqual(vault.list().map(item=>item.id).sort(),['first','second']);
  releaseFirst();await first;
  assert.deepEqual(vault.list().map(item=>item.id).sort(),['first','second']);
  third=vault.save({...account,id:'third'});
 }finally{releaseFirst();releaseSecond();}
 await Promise.all([first,second,third]);vault.lock();
 const restarted=new Vault(dir);await restarted.init();await restarted.unlock(PASSWORD);
 assert.deepEqual(restarted.list().map(item=>item.id).sort(),['first','second','third']);restarted.lock();
});

test('password rotation rejects new writes while queued and resumes with the new key',async t=>{
 const dir=await temporary(t),vault=new Vault(dir);await vault.init();await vault.setup(PASSWORD);await vault.save(account);
 let release;const barrier=new Promise(resolve=>{release=resolve;});vault.serialize(()=>barrier);
 const replacement='TEST ONLY rotated master phrase';
 const rotation=vault.changePassword(PASSWORD,replacement);
 try{
  await assert.rejects(vault.save({...account,password:'blocked during rotation'}),/正在处理/);
  await assert.rejects(vault.delete(account.id),/正在处理/);
 }finally{release();}
 await rotation;assert.equal(vault.list()[0].password,account.password);
 await vault.save({...account,id:'after-rotation'});vault.lock();
 const restarted=new Vault(dir);await restarted.init();await assert.rejects(restarted.unlock(PASSWORD),/不正确/);await restarted.unlock(replacement);
 assert.deepEqual(restarted.list().map(item=>item.id).sort(),['after-rotation',account.id].sort());restarted.lock();
});

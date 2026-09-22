const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const {LocalStore,settings,entries,atomicWrite,readJson,MAX_FILE,MAX_BACKUP}=require('../src/storage.cjs');
async function temporary(t){const root=path.resolve(__dirname,'../test-results');await fs.mkdir(root,{recursive:true});const dir=await fs.mkdtemp(path.join(root,'storage-'));t.after(async()=>{assert.equal(path.dirname(dir),root);await fs.rm(dir,{recursive:true,force:true});});return dir;}
const note=(id,title='测试备忘录')=>({id,title,body:'普通内容',pinned:false,createdAt:'2026-09-22T00:00:00.000Z',updatedAt:'2026-09-22T00:00:00.000Z'});
test('notes persist across restarts and concurrent writes retain all records',async t=>{const dir=await temporary(t);const store=new LocalStore(dir);await store.init();await Promise.all(Array.from({length:12},(_,i)=>store.saveNote(note('id-'+i))));const reopened=new LocalStore(dir);await reopened.init();assert.equal(reopened.snapshot().notes.length,12);assert.equal(JSON.parse(await fs.readFile(path.join(dir,'notes.json.bak'),'utf8')).notes.length,11);});
test('corrupt data is not replaced with an empty database',async t=>{const dir=await temporary(t);await fs.writeFile(path.join(dir,'notes.json'),'broken JSON');await assert.rejects(new LocalStore(dir).init(),/未被覆盖/);assert.equal(await fs.readFile(path.join(dir,'notes.json'),'utf8'),'broken JSON');});
test('settings validate images, shortcuts, bounded transparency and lock timeout',()=>{for(const bad of [{shortcut:'Q'},{image:'https://evil.invalid/a.png'},{imageTransparency:101},{imageTransparency:NaN},{autoLockMinutes:0}])assert.throws(()=>settings(bad));assert.equal(settings({shortcut:'F8',imageTransparency:0}).imageTransparency,0);assert.throws(()=>entries([note('same'),note('same')]),/重复/);});
test('rejected write does not poison subsequent saves',async t=>{const dir=await temporary(t);const store=new LocalStore(dir);await store.init();await assert.rejects(store.mutate(()=>{throw Error('simulated failure')}));await store.saveNote(note('later'));assert.equal(store.snapshot().notes.length,1);});
test('snapshot does not let renderer callers mutate persisted state',async t=>{const dir=await temporary(t);const store=new LocalStore(dir);await store.init();const data=store.snapshot();data.settings.shortcut='F8';assert.equal(store.snapshot().settings.shortcut,'Alt+Q');});

test('combined backup larger than an individual store roundtrips with the backup limit',async t=>{
 const {randomBytes}=require('node:crypto');const {encrypt,decrypt,envelope}=require('../src/vault.cjs');
 const dir=await temporary(t),filename=path.join(dir,'combined-backup.json');
 const body='x'.repeat(100000);
 const data={version:1,settings:settings({}),notes:Array.from({length:90},(_,i)=>({...note('note-'+i),body}))};
 const accounts=Array.from({length:70},(_,i)=>({...note('account-'+i),body,username:'test-user',password:'TEST ONLY secret',url:''}));
 const key=randomBytes(32);t.after(()=>key.fill(0));const vault=encrypt(accounts,key,randomBytes(32));
 const backup={format:'super-brain.backup',version:1,createdAt:'2026-09-22T00:00:00.000Z',data,vault};
 const bytes=value=>Buffer.byteLength(JSON.stringify(value,null,2));
 assert.ok(bytes(data)<MAX_FILE);assert.ok(bytes(vault)<MAX_FILE);
 assert.ok(bytes(backup)>MAX_FILE);assert.ok(bytes(backup)<MAX_BACKUP);
 await assert.rejects(atomicWrite(filename,backup),/数据过大/);
 await atomicWrite(filename,backup,MAX_BACKUP);
 await assert.rejects(readJson(filename),/无法读取/);
 const restored=await readJson(filename,MAX_BACKUP);
 assert.equal(restored.format,'super-brain.backup');assert.deepEqual(entries(restored.data.notes),data.notes);
 assert.deepEqual(decrypt(envelope(restored.vault),key),accounts);
 const store=new LocalStore(path.join(dir,'restored'));await store.init();await store.restore(restored.data);
 const reopened=new LocalStore(store.directory);await reopened.init();assert.deepEqual(reopened.snapshot(),data);
});

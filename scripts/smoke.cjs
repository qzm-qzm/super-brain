'use strict';

// Real Electron + renderer + IPC + filesystem integration. Uses isolated data only.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { _electron: electron } = require('playwright');

const root = path.resolve(__dirname, '..');
const masterPassword = 'Smoke test master password 2026!';
const secret = 'OnlyInEncryptedVault-4F6a-2026!';
const accountTitle = 'Smoke private account';
const noteTitle = 'Integration note <img src=x onerror="window.injected=true">';
const image = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=';
let app, page, runDirectory, dataDirectory;
const pageErrors = [];
const checks = [];

async function check(label, fn) {
  await fn();
  checks.push(label);
  console.log('PASS ' + label);
}

async function start(entry = root) {
  const env = { ...process.env, SUPER_BRAIN_TEST: '1', SUPER_BRAIN_TEST_DATA_DIR: dataDirectory };
  delete env.ELECTRON_RUN_AS_NODE;
  app = await electron.launch({ args: [entry], env, timeout: 30000 });
  page = await app.firstWindow({ timeout: 20000 });
  page.setDefaultTimeout(12000);
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.waitForSelector('.wordmark');
  await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].show());
}

async function stop() {
  const running = app;
  app = null;
  if (running) await running.close();
}

async function saved() {
  await page.waitForFunction(() => document.querySelector('.save-status')?.textContent.includes('已保存到本机'));
}

async function unlock(password) {
  await page.locator('#master-password').fill(password);
  await page.locator('#unlock-form button[type="submit"]').click();
}

async function show() {
  await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].show());
}

async function nativeHidden() {
  for (let i = 0; i < 100; i++) {
    if (await app.evaluate(({ BrowserWindow }) => !BrowserWindow.getAllWindows()[0].isVisible())) return;
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  throw new Error('Window did not hide within 5 seconds');
}

async function main() {
  const results = path.join(root, 'test-results');
  await fs.mkdir(results, { recursive: true });
  runDirectory = await fs.mkdtemp(path.join(results, 'desktop-smoke-'));
  dataDirectory = path.join(runDirectory, 'user-data');
  console.log('Isolated test artifacts: ' + runDirectory);
  await start();

  await check('empty startup and isolated native data directory', async () => {
    const snapshot = await page.evaluate(() => window.brain.state());
    assert.deepEqual(snapshot.notes, []);
    assert.equal(snapshot.vault.exists, false);
    assert.equal(snapshot.vault.unlocked, false);
    assert.equal(await app.evaluate(({ app }) => app.getPath('userData')), dataDirectory);
    assert.equal(await page.locator('.wordmark').textContent(), '超强大脑');
    // Keep desktop/user Alt+Q conflicts from blocking unrelated settings checks.
    await page.evaluate(settings => window.brain.saveSettings({ ...settings, shortcut: 'Ctrl+Alt+Shift+F24' }), snapshot.settings);
    await page.reload();
    await page.waitForSelector('.wordmark');
  });

  await check('sandboxed renderer exposes narrow bridge without Node', async () => {
    const isolation = await page.evaluate(() => ({
      require: typeof require, process: typeof process, ipc: typeof window.ipcRenderer,
      bridgeInvoke: typeof window.brain.invoke, injected: window.injected || false,
      frozen: Object.isFrozen(window.brain)
    }));
    assert.deepEqual(isolation, { require: 'undefined', process: 'undefined', ipc: 'undefined', bridgeInvoke: 'undefined', injected: false, frozen: true });
    const preferences = await app.evaluate(({ BrowserWindow }) => {
      const p = BrowserWindow.getAllWindows()[0].webContents.getLastWebPreferences();
      return { contextIsolation: p.contextIsolation, nodeIntegration: p.nodeIntegration, sandbox: p.sandbox };
    });
    assert.deepEqual(preferences, { contextIsolation: true, nodeIntegration: false, sandbox: true });
  });

  await check('create/edit note, escape HTML, and save to disk', async () => {
    await page.locator('[data-action="new"]').first().click();
    await page.locator('#edit-title').fill(noteTitle);
    await page.locator('#edit-body').fill('第一行：普通备忘录\nSecond line: saved locally.');
    await saved();
    await page.locator('[data-action="back"]').click();
    assert.equal(await page.locator('.item-title').textContent(), noteTitle);
    assert.equal(await page.locator('.item img').count(), 0);
    assert.equal(await page.evaluate(() => Boolean(window.injected)), false);
    await page.locator('[data-open]').first().click();
    await page.locator('#edit-body').fill('Edited note; pending changes must survive native close.');
    await app.evaluate(({ BrowserWindow }) => BrowserWindow.getAllWindows()[0].close());
    await nativeHidden();
    const disk = JSON.parse(await fs.readFile(path.join(dataDirectory, 'notes.json'), 'utf8'));
    assert.equal(disk.notes[0].body, 'Edited note; pending changes must survive native close.');
    await show();
    await page.locator('[data-action="back"]').click();
  });

  await check('create password vault through real setup form', async () => {
    await page.locator('#vault-tab').click();
    await page.locator('#master-password').fill(masterPassword);
    await page.locator('#confirm-password').fill(masterPassword);
    await page.locator('#master-acknowledge').check();
    await page.locator('#unlock-form button[type="submit"]').click();
    await page.waitForSelector('.vault-strip');
    assert.deepEqual((await page.evaluate(() => window.brain.state())).vault, { exists: true, unlocked: true });
  });

  await check('save account without writing plaintext secrets to native JSON', async () => {
    await page.locator('[data-action="new"]').first().click();
    await page.locator('#edit-title').fill(accountTitle);
    await page.locator('#edit-username').fill('smoke-private@example.invalid');
    await page.locator('#edit-password').fill(secret);
    await page.locator('#edit-url').fill('https://example.invalid');
    await page.locator('#edit-body').fill('Private account metadata');
    await saved();
    assert.equal(await page.locator('#edit-password').getAttribute('type'), 'password');
    await page.locator('[data-action="reveal"]').click();
    assert.equal(await page.locator('#edit-password').getAttribute('type'), 'text');
    for (const filename of await fs.readdir(dataDirectory)) {
      if (!/\.json(?:\.bak)?$/.test(filename)) continue;
      const text = await fs.readFile(path.join(dataDirectory, filename), 'utf8');
      for (const forbidden of [secret, masterPassword, accountTitle, 'smoke-private@example.invalid']) {
        assert.equal(text.includes(forbidden), false, filename + ' contains plaintext ' + forbidden);
      }
    }
    const disk = JSON.parse(await fs.readFile(path.join(dataDirectory, 'vault.enc.json'), 'utf8'));
    assert.equal(disk.format, 'super-brain.vault');
    assert.equal(disk.cipher.name, 'aes-256-gcm');
    assert.equal(disk.kdf.name, 'scrypt');
  });

  await check('hide locks vault, clears DOM and callback has no Electron event', async () => {
    await page.evaluate(() => {
      window.smokeLockArgs = null;
      window.brain.onLocked((...args) => { window.smokeLockArgs = args; });
    });
    await page.locator('#edit-body').fill('Last edit before immediate hide');
    await page.locator('[data-action="hide"]').click();
    await nativeHidden();
    await page.waitForFunction(() => window.smokeLockArgs !== null && !document.querySelector('#edit-password'));
    const result = await page.evaluate(async () => ({
      vault: (await window.brain.state()).vault,
      html: document.documentElement.outerHTML,
      args: window.smokeLockArgs,
      listError: await window.brain.listVault().then(() => null, e => e.message)
    }));
    assert.equal(result.vault.unlocked, false);
    assert.equal(result.html.includes(secret), false);
    assert.equal(result.html.includes(accountTitle), false);
    assert.deepEqual(result.args, ['hidden']);
    assert.match(result.listError, /锁定/);
    await show();
  });

  await check('wrong master password fails; correct password restores account', async () => {
    await unlock('This password is definitely wrong!');
    await page.waitForFunction(() => document.getElementById('unlock-error')?.textContent.includes('主密码不正确'));
    assert.equal((await page.evaluate(() => window.brain.state())).vault.unlocked, false);
    assert.equal(await page.locator('#master-password').inputValue(), '');
    await unlock(masterPassword);
    await page.waitForSelector('.vault-strip');
    await page.locator('[data-open]').first().click();
    assert.equal(await page.locator('#edit-password').inputValue(), secret);
    assert.equal(await page.locator('#edit-body').inputValue(), 'Last edit before immediate hide');
    await page.locator('[data-action="back"]').click();
    await page.locator('#notes-tab').click();
  });

  await check('screen lock saves pending account edit and clears secret DOM', async () => {
    await page.locator('#vault-tab').click();
    await page.locator('[data-open]').first().click();
    await page.locator('#edit-body').fill('Last edit immediately before screen lock');
    await app.evaluate(({ powerMonitor }) => powerMonitor.emit('lock-screen'));
    await page.waitForFunction(() => !document.getElementById('edit-password'));
    assert.equal((await page.evaluate(() => window.brain.state())).vault.unlocked, false);
    assert.equal((await page.content()).includes(secret), false);
    await unlock(masterPassword);
    await page.waitForSelector('.vault-strip');
    await page.locator('[data-open]').first().click();
    assert.equal(await page.locator('#edit-body').inputValue(), 'Last edit immediately before screen lock');
    await page.locator('[data-action="back"]').click();
    await page.locator('#notes-tab').click();
  });

  await check('renderer reload locks vault and saved accounts remain available after unlock', async () => {
    await page.locator('#vault-tab').click();
    await page.locator('[data-open]').first().click();
    assert.equal(await page.locator('#edit-password').inputValue(), secret);
    assert.equal((await page.evaluate(() => window.brain.state())).vault.unlocked, true);
    await page.reload();
    await page.waitForSelector('.wordmark');
    assert.deepEqual((await page.evaluate(() => window.brain.state())).vault, { exists: true, unlocked: false });
    assert.equal((await page.content()).includes(secret), false);
    assert.equal(await app.evaluate(({ Menu }) => Menu.getApplicationMenu() === null), true);
    await page.locator('#vault-tab').click();
    await unlock(masterPassword);
    await page.waitForSelector('.vault-strip');
    await page.locator('[data-open]').first().click();
    assert.equal(await page.locator('#edit-password').inputValue(), secret);
    assert.equal(await page.locator('#edit-body').inputValue(), 'Last edit immediately before screen lock');
    await page.locator('[data-action="back"]').click();
    await page.locator('#notes-tab').click();
  });

  await check('custom theme + background image transparency save through native bridge', async () => {
    await page.locator('[data-appearance-open]').click();
    await page.locator('[data-preset="graphite"]').click();
    await page.locator('#background-hex').fill('#243040');
    await page.locator('#wallpaper-file').setInputFiles({ name: 'smoke.png', mimeType: 'image/png', buffer: Buffer.from(image, 'base64') });
    await page.waitForFunction(() => !document.getElementById('wallpaper-transparency').disabled);
    await page.locator('#wallpaper-transparency').evaluate(element => { element.value = '37'; element.dispatchEvent(new Event('input', { bubbles: true })); });
    await page.locator('#appearance-close').click();
    await page.waitForFunction(() => !document.getElementById('appearance-dialog').open);
    const prefs = (await page.evaluate(() => window.brain.state())).settings;
    assert.equal(prefs.background, '#243040');
    assert.equal(prefs.imageTransparency, 37);
    assert.equal(prefs.image, 'data:image/png;base64,' + image);
    const disk = JSON.parse(await fs.readFile(path.join(dataDirectory, 'notes.json'), 'utf8'));
    assert.equal(disk.settings.imageTransparency, 37);
    await page.screenshot({ path: path.join(runDirectory, 'desktop-notes.png') });
  });

  await check('IPC rejects malformed settings without changing saved preferences', async () => {
    const result = await page.evaluate(async () => {
      const original = (await window.brain.state()).settings;
      const error = await window.brain.saveSettings({ ...original, imageTransparency: 101 }).then(() => null, e => e.message);
      return { error, after: (await window.brain.state()).settings.imageTransparency };
    });
    assert.match(result.error, /透明度/);
    assert.equal(result.after, 37);
  });

  await check('restart preserves notes/theme and starts password vault locked', async () => {
    await stop();
    await start();
    const snapshot = await page.evaluate(() => window.brain.state());
    assert.equal(snapshot.notes.length, 1);
    assert.equal(snapshot.notes[0].title, noteTitle);
    assert.equal(snapshot.notes[0].body, 'Edited note; pending changes must survive native close.');
    assert.equal(snapshot.settings.background, '#243040');
    assert.equal(snapshot.settings.imageTransparency, 37);
    assert.equal(snapshot.settings.image, 'data:image/png;base64,' + image);
    assert.deepEqual(snapshot.vault, { exists: true, unlocked: false });
    await page.locator('#vault-tab').click();
    await unlock(masterPassword);
    await page.waitForSelector('.vault-strip');
    await page.locator('[data-open]').first().click();
    assert.equal(await page.locator('#edit-password').inputValue(), secret);
    assert.equal(await page.locator('#edit-body').inputValue(), 'Last edit immediately before screen lock');
    await page.locator('[data-action="hide"]').click();
    await nativeHidden();
  });

  await check('occupied startup shortcut does not block appearance saves or closing dialog', async () => {
    await stop();
    const bootstrap = path.join(runDirectory, 'occupied-shortcut.cjs');
    await fs.writeFile(bootstrap, "'use strict';\nrequire('electron').globalShortcut.register = () => false;\nrequire(" + JSON.stringify(path.join(root, 'src', 'main.cjs')) + ");\n");
    await start(bootstrap);
    assert.equal((await page.evaluate(() => window.brain.state())).shortcutActive, false);
    await page.locator('[data-appearance-open]').click();
    await page.locator('[data-preset="blue"]').click();
    await page.locator('#appearance-close').click();
    await page.waitForFunction(() => !document.getElementById('appearance-dialog').open);
    assert.equal((await page.evaluate(() => window.brain.state())).settings.background, '#EAF2FA');
  });

  await check('no uncaught renderer errors', async () => assert.deepEqual(pageErrors, []));
  await stop();
  await fs.writeFile(path.join(runDirectory, 'result.json'), JSON.stringify({ passed: checks, pageErrors }, null, 2));
  console.log(`Desktop smoke passed: ${checks.length} checks.`);
}

main().catch(async error => {
  console.error('FAIL ' + error.stack);
  if (page && !page.isClosed() && runDirectory) {
    await page.screenshot({ path: path.join(runDirectory, 'failure.png') }).catch(() => {});
    await fs.writeFile(path.join(runDirectory, 'failure.html'), await page.content()).catch(() => {});
  }
  if (runDirectory) await fs.writeFile(path.join(runDirectory, 'result.json'), JSON.stringify({ passed: checks, failure: error.stack, pageErrors }, null, 2)).catch(() => {});
  await stop().catch(() => {});
  process.exitCode = 1;
});

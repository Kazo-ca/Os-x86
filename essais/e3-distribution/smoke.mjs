// Essai E3 — vérification rapide (code jetable) : page dotnet, measureUserAgentSpecificMemory, info.
import { chromium } from 'playwright';
import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { startServer } from './server.mjs';
const { server, port } = await startServer({ port: 8099 + 0, coi: true });
const base = `http://127.0.0.1:${port}`;
const dir = await mkdtemp(path.join(os.tmpdir(), 'e3-smoke-'));
const ctx = await chromium.launchPersistentContext(dir, { channel: 'chrome', headless: true });
const page = ctx.pages()[0];
page.on('console', (m) => console.log('[console]', m.type(), m.text().slice(0, 300)));
page.on('pageerror', (e) => console.log('[pageerror]', e.message));
await page.goto(`${base}/dl.html`);
console.log(JSON.stringify(await page.evaluate(() => window.__run({ mode: 'info' }))));
console.log(JSON.stringify(await page.evaluate(async () => { try { return await performance.measureUserAgentSpecificMemory(); } catch (e) { return 'ERR ' + e.message; } })));
await page.goto(`${base}/dotnet/interp/?size=16&passes=1`);
try {
  await page.waitForFunction(() => window.__done === true, null, { timeout: 120000, polling: 500 });
  console.log(JSON.stringify(await page.evaluate(() => ({ r: window.__results, e: window.__error }))));
} catch (e) { console.log('dotnet timeout', e.message.slice(0, 200)); console.log(await page.content().then((c) => c.slice(0, 1500))); }
await ctx.close(); await rm(dir, { recursive: true, force: true }); server.close(); process.exit(0);

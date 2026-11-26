#!/usr/bin/env node
// Capture screenshots of a page at desktop/mobile sizes in light/dark mode.
//
// Uses an installed Chrome / Edge / Chromium in headless mode through the
// DevTools protocol, so it needs no npm packages (Node.js 22+ for the global
// WebSocket). Mobile sizes are emulated properly (headless Chrome cannot make
// a window narrower than 500px on its own). The script only reads the page and
// writes PNG files into --out-dir.
//
//   node capture.mjs page.html                       # desktop + mobile, light
//   node capture.mjs http://localhost:5173 --theme both --full
//   node capture.mjs page.html --viewports mobile --query accent=teal

import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const VIEWPORTS = {
  desktop: { width: 1440, height: 900, scale: 1, mobile: false },
  laptop: { width: 1280, height: 800, scale: 1, mobile: false },
  tablet: { width: 834, height: 1112, scale: 1, mobile: true },
  mobile: { width: 390, height: 844, scale: 2, mobile: true },
};
const MAX_FULL_HEIGHT = 8000;

function usage(code = 0) {
  console.log(`Usage: node capture.mjs <file-or-url>... [options]

Options:
  --viewports <list>   desktop,laptop,tablet,mobile (default: desktop,mobile)
  --theme <mode>       light | dark | both (default: light)
  --full               Capture the whole page height (capped at ${MAX_FULL_HEIGHT}px)
  --query <string>     Query string appended to file targets, e.g. accent=teal
  --wait <ms>          Extra wait after load (default: 400)
  --out-dir <dir>      Output directory (default: .ux-shots)
  --browser <path>     Browser executable (or set UX_BROWSER)
  -h, --help           Show this help`);
  process.exit(code);
}

function parseArgs(argv) {
  const opts = { targets: [], viewports: ['desktop', 'mobile'], themes: ['light'], full: false,
    query: '', wait: 400, outDir: '.ux-shots', browser: process.env.UX_BROWSER || '' };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    const next = () => {
      if (i + 1 >= argv.length) { console.error(`Missing value for ${arg}`); process.exit(2); }
      i += 1;
      return argv[i];
    };
    if (arg === '-h' || arg === '--help') usage(0);
    else if (arg === '--viewports') opts.viewports = next().split(',').map((v) => v.trim()).filter(Boolean);
    else if (arg === '--theme') {
      const theme = next();
      if (!['light', 'dark', 'both'].includes(theme)) { console.error('--theme must be light, dark or both'); process.exit(2); }
      opts.themes = theme === 'both' ? ['light', 'dark'] : [theme];
    } else if (arg === '--full') opts.full = true;
    else if (arg === '--query') opts.query = next().replace(/^\?/, '');
    else if (arg === '--wait') opts.wait = Number(next());
    else if (arg === '--out-dir') opts.outDir = next();
    else if (arg === '--browser') opts.browser = next();
    else if (arg.startsWith('--')) { console.error(`Unknown option: ${arg}`); process.exit(2); }
    else opts.targets.push(arg);
  }
  if (opts.targets.length === 0) usage(2);
  for (const name of opts.viewports) {
    if (!VIEWPORTS[name]) { console.error(`Unknown viewport: ${name}`); process.exit(2); }
  }
  return opts;
}

function findBrowser(explicit) {
  if (explicit) return explicit;
  const candidates = [];
  if (process.platform === 'win32') {
    const roots = [process.env['PROGRAMFILES'], process.env['PROGRAMFILES(X86)'], process.env.LOCALAPPDATA].filter(Boolean);
    for (const root of roots) {
      candidates.push(path.join(root, 'Google/Chrome/Application/chrome.exe'));
      candidates.push(path.join(root, 'Microsoft/Edge/Application/msedge.exe'));
      candidates.push(path.join(root, 'Chromium/Application/chrome.exe'));
    }
  } else if (process.platform === 'darwin') {
    candidates.push('/Applications/Google Chrome.app/Contents/MacOS/Google Chrome');
    candidates.push('/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge');
    candidates.push('/Applications/Chromium.app/Contents/MacOS/Chromium');
  }
  const pathDirs = (process.env.PATH || '').split(path.delimiter);
  for (const name of ['google-chrome', 'google-chrome-stable', 'chromium', 'chromium-browser', 'microsoft-edge', 'msedge', 'chrome']) {
    for (const dir of pathDirs) candidates.push(path.join(dir, process.platform === 'win32' ? `${name}.exe` : name));
  }
  const found = candidates.find((candidate) => candidate && existsSync(candidate));
  if (!found) throw new Error('Chrome / Edge / Chromium was not found. Pass --browser <path> or set UX_BROWSER.');
  return found;
}

function toUrl(target, query) {
  if (/^[a-z]+:\/\//i.test(target)) return target;
  const resolved = path.resolve(target);
  if (!existsSync(resolved)) throw new Error(`File not found: ${target}`);
  const url = pathToFileURL(resolved).href;
  return query ? `${url}?${query}` : url;
}

async function waitForFile(file, timeoutMs) {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (existsSync(file)) {
      const text = await readFile(file, 'utf8');
      if (text.includes('\n')) return text;
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error('Browser did not open a DevTools port in time.');
}

class Cdp {
  constructor(url) {
    this.nextId = 1;
    this.pending = new Map();
    this.listeners = new Map();
    this.socket = new WebSocket(url);
    this.ready = new Promise((resolve, reject) => {
      this.socket.addEventListener('open', resolve, { once: true });
      this.socket.addEventListener('error', () => reject(new Error('DevTools connection failed')), { once: true });
    });
    this.socket.addEventListener('message', (event) => {
      const message = JSON.parse(event.data);
      if (message.id && this.pending.has(message.id)) {
        const { resolve, reject } = this.pending.get(message.id);
        this.pending.delete(message.id);
        if (message.error) reject(new Error(message.error.message));
        else resolve(message.result);
      } else if (message.method && this.listeners.has(message.method)) {
        for (const listener of this.listeners.get(message.method)) listener(message.params);
        this.listeners.delete(message.method);
      }
    });
  }

  send(method, params = {}) {
    const id = this.nextId++;
    this.socket.send(JSON.stringify({ id, method, params }));
    return new Promise((resolve, reject) => this.pending.set(id, { resolve, reject }));
  }

  once(method, timeoutMs = 30000) {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`Timed out waiting for ${method}`)), timeoutMs);
      const list = this.listeners.get(method) || [];
      list.push((params) => { clearTimeout(timer); resolve(params); });
      this.listeners.set(method, list);
    });
  }

  close() { this.socket.close(); }
}

function slug(target) {
  const base = /^[a-z]+:\/\//i.test(target)
    ? new URL(target).host + new URL(target).pathname
    : path.basename(target, path.extname(target));
  return base.replace(/[^a-z0-9._-]+/gi, '-').replace(/^-+|-+$/g, '') || 'page';
}

async function capture(cdp, url, viewport, theme, opts) {
  await cdp.send('Emulation.setDeviceMetricsOverride', {
    width: viewport.width, height: viewport.height, deviceScaleFactor: viewport.scale, mobile: viewport.mobile,
  });
  await cdp.send('Emulation.setTouchEmulationEnabled', { enabled: viewport.mobile });
  await cdp.send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: theme }] });
  const loaded = cdp.once('Page.loadEventFired');
  await cdp.send('Page.navigate', { url });
  await loaded;
  await cdp.send('Runtime.evaluate', {
    expression: `document.fonts.ready.then(() => new Promise((r) => setTimeout(r, ${Math.max(0, opts.wait)})))`,
    awaitPromise: true,
  });
  let height = viewport.height;
  if (opts.full) {
    const result = await cdp.send('Runtime.evaluate', {
      expression: 'Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0)',
      returnByValue: true,
    });
    height = Math.min(Math.max(viewport.height, Number(result.result.value) || viewport.height), MAX_FULL_HEIGHT);
  }
  const shot = await cdp.send('Page.captureScreenshot', {
    format: 'png',
    captureBeyondViewport: opts.full,
    clip: { x: 0, y: 0, width: viewport.width, height, scale: 1 },
  });
  return Buffer.from(shot.data, 'base64');
}

async function main() {
  const opts = parseArgs(process.argv.slice(2));
  const browserPath = findBrowser(opts.browser);
  const userDataDir = await mkdtemp(path.join(os.tmpdir(), 'ux-capture-'));
  const browser = spawn(browserPath, [
    '--headless=new', '--remote-debugging-port=0', `--user-data-dir=${userDataDir}`,
    '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--hide-scrollbars',
    '--disable-extensions', 'about:blank',
  ], { stdio: 'ignore' });

  let cdp;
  try {
    const portFile = await waitForFile(path.join(userDataDir, 'DevToolsActivePort'), 15000);
    const port = portFile.split('\n')[0].trim();
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
    const page = pages.find((entry) => entry.type === 'page');
    if (!page) throw new Error('No page target found in the browser.');
    cdp = new Cdp(page.webSocketDebuggerUrl);
    await cdp.ready;
    await cdp.send('Page.enable');

    await mkdir(opts.outDir, { recursive: true });
    const written = [];
    for (const target of opts.targets) {
      const url = toUrl(target, opts.query);
      for (const name of opts.viewports) {
        for (const theme of opts.themes) {
          const png = await capture(cdp, url, VIEWPORTS[name], theme, opts);
          const suffix = opts.query ? `-${opts.query.replace(/[^a-z0-9]+/gi, '-')}` : '';
          const file = path.join(opts.outDir, `${slug(target)}${suffix}-${name}-${theme}${opts.full ? '-full' : ''}.png`);
          await writeFile(file, png);
          written.push(file);
          console.log(`saved ${file}`);
        }
      }
    }
    console.log(`${written.length} screenshot(s) written to ${path.resolve(opts.outDir)}`);
  } finally {
    try { if (cdp) { await Promise.race([cdp.send('Browser.close'), new Promise((r) => setTimeout(r, 1500))]); cdp.close(); } } catch { /* already closed */ }
    browser.kill();
    await new Promise((resolve) => setTimeout(resolve, 300));
    await rm(userDataDir, { recursive: true, force: true, maxRetries: 3 }).catch(() => {});
  }
}

main().catch((error) => {
  console.error(`capture failed: ${error.message}`);
  process.exit(1);
});

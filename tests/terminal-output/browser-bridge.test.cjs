'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { test } = require('node:test');
let chromium;
try { ({ chromium } = require('playwright')); } catch { /* Optional browser runtime. */ }

async function fixture() {
  const browser = await chromium.launch({
    headless: true,
    ...(process.platform === 'win32' ? { channel: 'msedge' } : {})
  });
  const page = await browser.newPage({ viewport: { width: 900, height: 600 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    const state = window.__suttyTest = { messages: [], hostMessage: null, terminal: null };
    window.chrome = window.chrome || {};
    window.chrome.webview = {
      postMessage(message) { state.messages.push(message); },
      addEventListener(name, callback) { if (name === 'message') state.hostMessage = callback; }
    };
    let constructor;
    Object.defineProperty(window, 'Terminal', {
      configurable: true,
      get() { return constructor; },
      set(value) {
        constructor = new Proxy(value, {
          construct(target, args) {
            const terminal = Reflect.construct(target, args);
            state.terminal = terminal;
            return terminal;
          }
        });
      }
    });
  });
  await page.goto(pathToFileURL(path.resolve(
    __dirname, '../../src/sutty.UI/Assets/Terminal/index.html')).href);
  await page.waitForFunction(() => window.__suttyTest.messages.some(message => message.type === 'ready'));
  await page.waitForFunction(() => window.__suttyTest.messages.some(message => message.type === 'resize'));
  // Size the actual browser viewport to the native capture's 80x24 cells, so a
  // later production fit cannot silently reflow it back to another column count.
  const viewport = await page.evaluate(() => {
    const terminal = window.__suttyTest.terminal;
    const cell = terminal._core._renderService.dimensions.css.cell;
    return {
      width: Math.ceil(innerWidth + (80 - terminal.cols) * cell.width),
      height: Math.ceil(innerHeight + (24 - terminal.rows) * cell.height)
    };
  });
  await page.setViewportSize(viewport);
  await page.waitForFunction(() => window.__suttyTest.terminal.cols === 80 &&
    window.__suttyTest.terminal.rows === 24);
  let id = 0;
  const send = message => page.evaluate(message =>
    window.__suttyTest.hostMessage({ data: { version: 1, ...message } }), message);
  const write = async data => {
    const writeId = ++id;
    await send({ type: 'write', id: writeId, data: Buffer.from(data).toString('base64') });
    await page.waitForFunction(id => window.__suttyTest.messages.some(
      message => message.type === 'writeComplete' && message.id === id), writeId);
  };
  const input = data => page.evaluate(data => window.__suttyTest.terminal.input(data, true), data);
  const copy = async () => {
    const copyId = ++id;
    await send({ type: 'copyLatestOutput', id: copyId });
    await page.waitForFunction(id => window.__suttyTest.messages.some(
      message => message.type === 'outputCopyEnd' && message.id === id), copyId);
    return page.evaluate(id => window.__suttyTest.messages.filter(
      message => message.type === 'outputCopyChunk' && message.id === id)
      .map(message => message.text).join(''), copyId);
  };
  const close = async () => {
    const bridgeErrors = await page.evaluate(() => window.__suttyTest.messages
      .filter(message => message.type === 'error').map(message => message.text));
    await browser.close();
    assert.deepEqual(errors, [], 'browser script errors');
    assert.deepEqual(bridgeErrors, [], 'production bridge errors');
  };
  return { page, send, write, input, copy, close };
}

test('production browser bridge copies current command stdout and stderr', { skip: !chromium }, async () => {
  const f = await fixture();
  try {
    await f.write('\x1b]133;A\x07PS C:\\> \x1b]133;B\x07');
    await f.input('command\r');
    await f.write('command\r\n  stdout\r\n\x1b[31mstderr\x1b[0m\r\n');
    await f.write('\x1b]133;A\x07PS C:\\> \x1b]133;B\x07');
    assert.equal(await f.copy(), '  stdout\r\nstderr\r\n');
  } finally { await f.close(); }
});

test('nested SSH with a compact custom prompt copies only the current response', { skip: !chromium }, async () => {
  const f = await fixture();
  try {
    await f.write('\x1b]133;A\x07PS C:\\> \x1b]133;B\x07');
    await f.input('ssh server\r');
    await f.write('ssh server\r\nWelcome\r\nprod ❯ ');
    await f.input('command\r');
    await f.write('command\r\nstdout\r\n\x1b[31mstderr\x1b[0m\r\nprod ❯ ');
    assert.equal(await f.copy(), 'stdout\r\nstderr\r\n');
  } finally { await f.close(); }
});

test('unknown response does not send an empty clipboard replacement', { skip: !chromium }, async () => {
  const f = await fixture();
  try {
    await f.write('Welcome\r\nserver$ ');
    await f.send({ type: 'copyLatestOutput', id: 200 });
    const messages = await f.page.evaluate(() => window.__suttyTest.messages.filter(message => message.id === 200));
    assert.deepEqual(messages.map(message => message.type), ['outputCopyUnavailable']);
    await f.write('\x1b]133;A\x07PS C:\\> \x1b]133;B\x07');
    await f.input('$null = 1\r');
    await f.write('$null = 1\r\n\x1b]133;A\x07PS C:\\> \x1b]133;B\x07');
    assert.equal(await f.copy(), '', 'a confirmed empty response still clears prior output');
  } finally { await f.close(); }
});

test('selected cursor shape survives shell DECSCUSR and terminal reset', { skip: !chromium }, async () => {
  const f = await fixture();
  try {
    const effectiveStyle = () => f.page.evaluate(() => {
      const terminal = window.__suttyTest.terminal;
      return terminal._core.coreService.decPrivateModes.cursorStyle ?? terminal.options.cursorStyle;
    });
    assert.equal(await effectiveStyle(), 'bar');
    await f.write('\x1b[2 q');
    assert.equal(await effectiveStyle(), 'bar');
    await f.send({ type: 'options', cursorStyle: 'underline' });
    await f.write('\x1b[6 q');
    assert.equal(await effectiveStyle(), 'underline');
    await f.send({ type: 'reset' });
    await f.write('\x1b[2 q');
    assert.equal(await effectiveStyle(), 'underline');
  } finally { await f.close(); }
});

for (const shell of ['PowerShell', 'CommandPrompt']) {
  test(`production browser bridge replays actual ${shell} ConPTY traffic`,
    { skip: !chromium || !process.env.SUTTY_COPY_REPLAY_DIRECTORY }, async () => {
      const f = await fixture();
      try {
        await f.send({ type: 'options', outputShell: shell === 'CommandPrompt' ? 'cmd' : null });
        const records = JSON.parse(fs.readFileSync(path.join(
          process.env.SUTTY_COPY_REPLAY_DIRECTORY, shell + '.json'), 'utf8'));
        for (const record of records) {
          if (record.Type === 'input') await f.input(record.Data);
          else if (record.Type === 'snapshot') assert.equal(await f.copy(), record.Data);
          else await f.write(Buffer.from(record.Data, 'base64'));
        }
      } finally { await f.close(); }
    });
}

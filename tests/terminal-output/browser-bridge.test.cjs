'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { test } = require('node:test');
let chromium;
try { ({ chromium } = require('playwright')); } catch { /* Optional browser runtime. */ }

function contrastRatio(first, second) {
  const luminance = color => {
    const channels = color.startsWith('#')
      ? [1, 3, 5].map(offset => parseInt(color.slice(offset, offset + 2), 16))
      : color.match(/\d+/g).slice(0, 3).map(Number);
    const linear = channels.map(value => {
      const channel = value / 255;
      return channel <= 0.04045 ? channel / 12.92 : Math.pow((channel + 0.055) / 1.055, 2.4);
    });
    return linear[0] * 0.2126 + linear[1] * 0.7152 + linear[2] * 0.0722;
  };
  const a = luminance(first);
  const b = luminance(second);
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}

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

test('terminal and host sizes agree at split-pane and oversized viewport limits', { skip: !chromium }, async () => {
  const f = await fixture();
  try {
    for (const viewport of [{ width: 150, height: 55 }, { width: 5000, height: 4000 }]) {
      await f.page.evaluate(() => { window.__suttyTest.messages.length = 0; });
      await f.page.setViewportSize(viewport);
      await f.page.waitForFunction(() => window.__suttyTest.messages.some(message => message.type === 'resize'));
      const size = await f.page.evaluate(() => {
        const state = window.__suttyTest;
        const message = state.messages.filter(message => message.type === 'resize').at(-1);
        return { columns: state.terminal.cols, rows: state.terminal.rows, message };
      });
      assert.ok(size.columns >= 20 && size.columns <= 500);
      assert.ok(size.rows >= 5 && size.rows <= 200);
      assert.equal(size.message.columns, size.columns);
      assert.equal(size.message.rows, size.rows);
      const count = await f.page.evaluate(() => window.__suttyTest.messages.length);
      await f.page.waitForTimeout(200);
      assert.equal(await f.page.evaluate(() => window.__suttyTest.messages.length), count,
        'clamped fitting must settle without recurring bridge messages');
    }
  } finally { await f.close(); }
});

test('terminal search fits narrow panes and retains focus when the window regains focus', { skip: !chromium }, async () => {
  const f = await fixture();
  try {
    await f.page.setViewportSize({ width: 190, height: 210 });
    await f.send({ type: 'findNext', text: 'result' });
    const bounds = await f.page.evaluate(() => {
      const search = document.getElementById('search').getBoundingClientRect();
      const input = document.getElementById('search-input').getBoundingClientRect();
      return { left: search.left, right: search.right, width: innerWidth, inputWidth: input.width };
    });
    assert.ok(bounds.left >= 0 && bounds.right <= bounds.width);
    assert.ok(bounds.inputWidth > 0, 'search input stays usable beside all three actions');
    await f.page.evaluate(() => window.dispatchEvent(new Event('focus')));
    assert.equal(await f.page.evaluate(() => document.activeElement.id), 'search-input');
    await f.page.locator('#search-close').click();
    await f.page.evaluate(() => window.dispatchEvent(new Event('focus')));
    assert.ok(await f.page.evaluate(() => document.activeElement.classList.contains('xterm-helper-textarea')));
  } finally { await f.close(); }
});

test('terminal search follows named themes without changing output, selection, or scrollback', { skip: !chromium }, async () => {
  const f = await fixture();
  try {
    await f.write(Array.from({ length: 45 }, (_, index) => `result ${index}\r\n`).join(''));
    await f.send({ type: 'findNext', text: 'result' });
    await f.send({ type: 'findNext', text: 'result' });
    await f.page.evaluate(() => window.__suttyTest.terminal.scrollToLine(10));
    const capture = () => f.page.evaluate(() => {
      const terminal = window.__suttyTest.terminal;
      return {
        selection: terminal.getSelectionPosition(),
        viewport: terminal.buffer.active.viewportY,
        lines: Array.from({ length: terminal.buffer.active.length }, (_, index) =>
          terminal.buffer.active.getLine(index).translateToString(true))
      };
    });
    const before = await capture();
    const themes = [
      { id: 'dracula', background: '#282a36', foreground: '#f8f8f2', cursor: '#bd93f9', scheme: 'dark' },
      { id: 'github-light', background: '#ffffff', foreground: '#24292f', cursor: '#0969da', scheme: 'light' },
      { id: 'nord', background: '#2e3440', foreground: '#d8dee9', cursor: '#88c0d0', scheme: 'dark' }
    ];
    for (const theme of themes) {
      const { id, scheme, ...palette } = theme;
      await f.send({ type: 'options', theme: palette });
      await f.page.waitForTimeout(100);
      assert.deepEqual(await capture(), before, id + ' preserves terminal state');
      const colors = await f.page.evaluate(() => {
        const style = getComputedStyle(document.documentElement);
        const searchStyle = getComputedStyle(document.getElementById('search'));
        const inputStyle = getComputedStyle(document.getElementById('search-input'));
        return {
          background: style.getPropertyValue('--terminal-background').trim(),
          foreground: style.getPropertyValue('--foreground').trim(),
          accent: style.getPropertyValue('--accent').trim(),
          surface: style.getPropertyValue('--surface').trim(),
          muted: style.getPropertyValue('--muted').trim(),
          scheme: style.colorScheme,
          searchBackground: searchStyle.backgroundColor,
          inputForeground: inputStyle.color,
          placeholderForeground: getComputedStyle(document.getElementById('search-input'), '::placeholder').color,
          matchBorders: Array.from(document.querySelectorAll('.xterm-find-result-decoration'), element =>
            getComputedStyle(element).outlineColor),
          messages: window.__suttyTest.messages.filter(message => message.type === 'input')
        };
      });
      assert.equal(colors.background, theme.background);
      assert.equal(colors.foreground, theme.foreground);
      assert.equal(colors.accent, theme.cursor);
      assert.equal(colors.scheme, scheme);
      assert.notEqual(colors.surface, '#101b2d', 'search no longer keeps the startup dark-blue surface');
      const rgb = hex => `rgb(${[1, 3, 5].map(offset => parseInt(hex.slice(offset, offset + 2), 16)).join(', ')})`;
      assert.equal(colors.searchBackground, rgb(colors.surface));
      assert.equal(colors.inputForeground, rgb(theme.foreground));
      assert.equal(colors.placeholderForeground, rgb(colors.muted));
      assert.ok(colors.matchBorders.length > 0, 'real matching output receives search decorations');
      assert.ok(colors.matchBorders.every(color =>
        color === rgb(theme.cursor) || color === rgb(theme.foreground)), 'all match borders use the active theme');
      assert.deepEqual(colors.messages, [], 'changing theme never generates PTY input');
      if (process.env.SUTTY_THEME_SCREENSHOT_DIRECTORY) {
        fs.mkdirSync(process.env.SUTTY_THEME_SCREENSHOT_DIRECTORY, { recursive: true });
        await f.page.screenshot({ path: path.join(process.env.SUTTY_THEME_SCREENSHOT_DIRECTORY, id + '.png') });
      }
    }
    await f.send({ type: 'options', cursorStyle: 'block' });
    assert.equal(await f.page.evaluate(() =>
      getComputedStyle(document.documentElement).getPropertyValue('--terminal-background').trim()), '#2e3440',
    'a partial options update does not revert the selected palette');
  } finally { await f.close(); }
});

test('terminal search in light palettes remains readable and fits compact viewports', { skip: !chromium }, async () => {
  const f = await fixture();
  try {
    await f.page.setViewportSize({ width: 270, height: 210 });
    await f.send({ type: 'options', theme: {
      background: '#ffffff', foreground: '#24292f', cursor: '#ffff00'
    } });
    await f.write('result\r\n');
    await f.send({ type: 'findNext', text: 'result' });
    const styles = await f.page.evaluate(() => {
      const rootStyle = getComputedStyle(document.documentElement);
      const search = document.getElementById('search').getBoundingClientRect();
      const input = getComputedStyle(document.getElementById('search-input'));
      const button = getComputedStyle(document.getElementById('search-next'));
      return {
        accent: rootStyle.getPropertyValue('--accent').trim(),
        scheme: rootStyle.colorScheme,
        search: { left: search.left, right: search.right },
        width: innerWidth,
        inputBackground: input.backgroundColor,
        inputForeground: input.color,
        buttonForeground: button.color
      };
    });
    assert.equal(styles.scheme, 'light');
    assert.notEqual(styles.accent, '#ffff00', 'low-contrast cursor colors cannot hide search focus outlines');
    assert.ok(contrastRatio(styles.accent, '#ffffff') >= 3);
    assert.equal(styles.inputBackground, 'rgb(255, 255, 255)');
    assert.equal(styles.inputForeground, 'rgb(36, 41, 47)');
    assert.notEqual(styles.buttonForeground, 'rgb(133, 148, 168)', 'actions use the active light palette');
    assert.ok(styles.search.left >= 0 && styles.search.right <= styles.width);
    if (process.env.SUTTY_THEME_SCREENSHOT_DIRECTORY) {
      fs.mkdirSync(process.env.SUTTY_THEME_SCREENSHOT_DIRECTORY, { recursive: true });
      await f.page.screenshot({ path: path.join(process.env.SUTTY_THEME_SCREENSHOT_DIRECTORY, 'light-compact-low-contrast-cursor.png') });
    }
  } finally { await f.close(); }
});

test('search UI contrast is readable for Min Dark and Solarized Light without altering terminal palettes', { skip: !chromium }, async () => {
  const f = await fixture();
  try {
    await f.write('result\r\n');
    await f.send({ type: 'findNext', text: 'result' });
    for (const palette of [
      { background: '#1f1f1f', foreground: '#888888', cursor: '#79b8ff', red: '#cd3131' },
      { background: '#fdf6e3', foreground: '#657b83', cursor: '#268bd2', red: '#dc322f' }
    ]) {
      await f.send({ type: 'options', theme: palette });
      const styles = await f.page.evaluate(() => {
        const input = document.getElementById('search-input');
        const inputStyle = getComputedStyle(input);
        const rootStyle = getComputedStyle(document.documentElement);
        return {
          foreground: inputStyle.color,
          muted: getComputedStyle(input, '::placeholder').color,
          buttonForeground: getComputedStyle(document.getElementById('search-next')).color,
          background: inputStyle.backgroundColor,
          surface: getComputedStyle(document.getElementById('search')).backgroundColor,
          accent: rootStyle.getPropertyValue('--accent').trim(),
          terminal: window.__suttyTest.terminal.options.theme
        };
      });
      for (const background of [styles.background, styles.surface]) {
        assert.ok(contrastRatio(styles.foreground, background) >= 4.5, 'search text is readable on both surfaces');
        assert.ok(contrastRatio(styles.muted, background) >= 4.5, 'placeholder is readable on both surfaces');
        assert.ok(contrastRatio(styles.buttonForeground, background) >= 4.5, 'search actions are readable on both surfaces');
        assert.ok(contrastRatio(styles.accent, background) >= 3, 'focus border is visible on both surfaces');
      }
      assert.equal(styles.terminal.foreground, palette.foreground);
      assert.equal(styles.terminal.red, palette.red);
      assert.equal(styles.terminal.cursor, palette.cursor);
    }
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

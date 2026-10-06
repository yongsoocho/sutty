'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

const input = path.resolve(process.argv[2] || 'artifacts/theme-preview/palettes.json');
const output = path.resolve(process.argv[3] || 'artifacts/theme-preview');
const palettes = JSON.parse(fs.readFileSync(input, 'utf8'));
assert.ok(Array.isArray(palettes) && palettes.length >= 50, 'exported catalog coverage');
assert.equal(new Set(palettes.map(theme => theme.id)).size, palettes.length, 'unique theme IDs');
fs.mkdirSync(output, { recursive: true });

function escape(value) {
  return String(value).replace(/[&<>"']/g, character => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  })[character]);
}

function tile(theme) {
  const colors = { ...theme.colors, RailTop: theme.railTop, RailBottom: theme.railBottom };
  for (const color of Object.values(colors)) assert.match(color, /^#[0-9A-F]{6}$/i, theme.id);
  const variables = Object.entries(colors).map(([key, value]) => `--${key}:${value}`).join(';');
  const ansi = theme.ansiColors.map((color, index) => {
    assert.match(color, /^#[0-9A-F]{6}$/i, `${theme.id} ANSI ${index}`);
    return `<i style="background:${color}" title="ANSI ${index}: ${color}"></i>`;
  }).join('');
  return `<section class="theme" data-theme="${escape(theme.id)}" style="${variables}">
    <header><strong>${escape(theme.name)}</strong><span>${escape(theme.id)}</span></header>
    <div class="shell">
      <nav><b class="brand">sutty</b><a class="selected">Sessions</a><a>Files</a><a>Commands</a><a>Settings</a></nav>
      <div class="workspace">
        <div class="tab">prod-web <span>SSH / 22</span></div>
        <div class="content">
          <div class="connection"><b>app01.example.com</b><span>Production web server</span><small>Last used 2 minutes ago</small></div>
          <input placeholder="Search connections" aria-label="Search connections">
          <div class="buttons"><button>Connect</button><button class="hover">Connect</button><a>Details</a></div>
          <div class="controls"><span class="check">&#10003;</span><span class="toggle"><i></i></span><span>Keep alive</span></div>
          <div class="states"><span class="green">Running</span><span class="amber">Queued</span><span class="red">Failed</span><span class="idle">Offline</span></div>
          <div class="terminal"><div>admin@app01:~$ ls -l</div><div>drwxr-xr-x  web  app.log</div><div class="ansi">${ansi}</div></div>
        </div>
      </div>
    </div>
  </section>`;
}

const html = `<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Sutty Theme Role Gallery</title><style>
*{box-sizing:border-box}body{margin:0;padding:16px;background:#dce1e4;color:#1c2530;font:13px "Segoe UI",sans-serif;letter-spacing:0}
.gallery{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px}.theme{min-width:0;overflow:hidden;border:1px solid #a8b0b6;border-radius:4px}
header{padding:9px 12px;background:#f5f6f7;display:flex;flex-wrap:wrap;gap:8px;align-items:center;min-height:39px}
header strong{font-size:14px}header span{color:#57616a;font-size:11px;overflow-wrap:anywhere}.shell{display:flex;height:350px;background:var(--AppBg);color:var(--TextPrimary)}
nav{width:96px;flex:none;padding:10px 6px;background:var(--RailTop);border-right:1px solid var(--ShellBorder)}
.brand{display:block;margin:0 8px 17px;font-size:18px}nav a{display:block;padding:9px 6px;margin-bottom:4px;color:var(--TextFaint);border-radius:4px;white-space:nowrap}
nav .selected{color:var(--AccentTeal);background:var(--SelectionBg)}nav .selected:hover{background:var(--SelectionBgHover)}
.workspace{flex:1;min-width:0;background:var(--PanelBg)}.tab{height:34px;padding:8px 12px;background:var(--ActiveTabBg);border-bottom:1px solid var(--Divider)}
.tab span{float:right;color:var(--TextMuted);font-size:11px}.content{padding:12px}.connection{padding:9px 10px;margin-bottom:8px;background:var(--CardBg);border:1px solid var(--CardBorder);border-radius:4px}
.connection b,.connection span,.connection small{display:block;overflow-wrap:anywhere}.connection span{margin-top:3px;color:var(--TextMuted);font-size:12px}.connection small{margin-top:3px;color:var(--TextFaint);font-size:11px}
input{display:block;width:100%;height:30px;margin-bottom:8px;padding:5px 8px;border:1px solid var(--InputBorder);border-radius:3px;color:var(--TextPrimary);background:var(--InputBg);font:inherit}
input::placeholder{color:var(--TextPlaceholder);opacity:1}.buttons{display:flex;align-items:center;gap:8px;margin-bottom:9px}.buttons a{margin-left:auto;color:var(--AccentBlue);font-size:12px}
button{min-width:76px;height:29px;border:0;border-radius:4px;background:linear-gradient(90deg,var(--GradientStart),var(--GradientEnd));color:var(--AccentForeground);font:600 12px "Segoe UI",sans-serif}
button.hover{background:linear-gradient(90deg,var(--GradientHoverStart),var(--GradientHoverEnd))}.controls{display:flex;align-items:center;gap:8px;height:20px;margin-bottom:8px;color:var(--TextMuted);font-size:11px}
.check{display:inline-grid;place-items:center;width:17px;height:17px;border-radius:2px;background:var(--ControlAccentFill);color:var(--AccentForeground);font-size:13px;font-weight:700}
.toggle{position:relative;width:34px;height:18px;border-radius:9px;background:var(--ControlAccentFillHover)}.toggle i{position:absolute;right:3px;top:3px;width:12px;height:12px;border-radius:50%;background:var(--AccentForeground)}
.states{display:flex;gap:6px;margin-bottom:9px;white-space:nowrap;font-size:11px}.states span{padding:3px 5px;border-radius:4px}
.green{color:var(--StatusGreen);background:var(--StatusGreenBg)}.amber{color:var(--StatusAmber);background:var(--StatusAmberBg)}.red{color:var(--StatusRed);background:var(--StatusRedBg)}.idle{color:var(--StatusIdle);background:var(--StatusIdleBg)}
.terminal{padding:9px 10px;background:var(--TerminalBg);color:var(--TerminalFg);border-top:1px solid var(--OutputGuide);font:12px/1.6 "Cascadia Mono",Consolas,monospace;white-space:nowrap}
.ansi{display:grid;grid-template-columns:repeat(16,minmax(0,1fr));gap:2px;margin-top:7px;height:9px}.ansi i{display:block;min-width:0}
@media(max-width:900px){.gallery{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:600px){body{padding:8px}.gallery{grid-template-columns:minmax(0,1fr)}nav{width:83px}.content{padding:10px}.states{gap:4px}.buttons{gap:6px}.buttons a{font-size:11px}.connection b{font-size:12px}.terminal{font-size:11px;padding:9px 6px}}
</style><main class="gallery">${palettes.map(tile).join('')}</main></html>`;
const htmlPath = path.join(output, 'gallery.html');
fs.writeFileSync(htmlPath, html);

async function validate(page, expectedCount) {
  const result = await page.evaluate(() => ({
    themes: document.querySelectorAll('.theme:not([hidden])').length,
    overflow: [...document.querySelectorAll('.theme:not([hidden]),.theme:not([hidden]) *')]
      .filter(element => element.scrollWidth > element.clientWidth + 1)
      .map(element => ({ theme: element.closest('[data-theme]').dataset.theme, className: element.className }))
  }));
  assert.equal(result.themes, expectedCount, 'rendered theme count');
  assert.deepEqual(result.overflow, [], 'palette preview text does not overflow');
  return result;
}

async function screenshot(page, fileName) {
  const image = await page.screenshot({ path: path.join(output, fileName), fullPage: true });
  const variation = await page.evaluate(async base64 => {
    const image = new Image();
    image.src = `data:image/png;base64,${base64}`;
    await image.decode();
    const canvas = document.createElement('canvas');
    canvas.width = 64;
    canvas.height = 64;
    const context = canvas.getContext('2d');
    context.drawImage(image, 0, 0, 64, 64);
    const pixels = context.getImageData(0, 0, 64, 64).data;
    const colors = new Set();
    for (let index = 0; index < pixels.length; index += 4)
      colors.add(`${pixels[index]},${pixels[index + 1]},${pixels[index + 2]}`);
    return colors.size;
  }, image.toString('base64'));
  assert.ok(variation > 20, `${fileName} has nonblank palette pixels`);
}

(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.platform === 'win32' ? { channel: 'msedge' } : {}) });
  const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1 });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const report = { source: input, kind: 'Role palette browser preview, not native WinUI acceptance', count: palettes.length };
  try {
    await page.goto(pathToFileURL(htmlPath).href);
    report.desktop = await validate(page, palettes.length);
    await screenshot(page, 'contact-sheet.png');
    for (let index = 0; index < palettes.length; index += 9) {
      const ids = palettes.slice(index, index + 9).map(theme => theme.id);
      await page.evaluate(ids => {
        for (const theme of document.querySelectorAll('.theme')) theme.hidden = !ids.includes(theme.dataset.theme);
      }, ids);
      await validate(page, ids.length);
      await screenshot(page, `catalog-page-${String(index / 9 + 1).padStart(2, '0')}.png`);
    }
    const priority = ['DeepField', 'DeepFieldLight', 'RosePine', 'RosePineDawn', 'MinDark', 'MinLight', 'Vesper', 'Nord', 'Monokai', 'MonokaiDimmed', 'AtomOneLight', 'VSCodeQuietLight'];
    await page.evaluate(ids => {
      for (const theme of document.querySelectorAll('.theme')) theme.hidden = !ids.includes(theme.dataset.theme);
    }, priority);
    report.priority = await validate(page, priority.length);
    await screenshot(page, 'priority-palettes.png');
    await page.setViewportSize({ width: 390, height: 844 });
    for (const id of ['DeepField', 'DeepFieldLight']) {
      await page.evaluate(id => {
        for (const theme of document.querySelectorAll('.theme')) theme.hidden = theme.dataset.theme !== id;
      }, id);
      report[id] = await validate(page, 1);
      await screenshot(page, `${id}-narrow.png`);
    }
    assert.deepEqual(errors, [], 'gallery JavaScript errors');
    fs.writeFileSync(path.join(output, 'gallery-report.json'), JSON.stringify(report, null, 2));
    console.log(`Verified ${palettes.length} exported palettes; screenshots and report: ${output}`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });

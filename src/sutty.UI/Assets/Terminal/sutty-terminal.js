(function () {
  'use strict';

  const protocolVersion = 1;
  const bridge = window.chrome && window.chrome.webview;
  const terminalRoot = document.getElementById('terminal-root');
  const terminalElement = document.getElementById('terminal');
  const searchElement = document.getElementById('search');
  const searchInput = document.getElementById('search-input');
  const searchPrevious = document.getElementById('search-previous');
  const searchNext = document.getElementById('search-next');
  const searchClose = document.getElementById('search-close');

  if (!bridge || !terminalElement || !window.Terminal || !window.FitAddon || !window.SearchAddon) {
    return;
  }

  const terminal = new window.Terminal({
    // The pinned SearchAddon uses xterm's decoration API for matching results.
    allowProposedApi: true,
    allowTransparency: false,
    convertEol: false,
    cursorBlink: true,
    cursorStyle: 'bar',
    drawBoldTextInBrightColors: true,
    fontFamily: 'Cascadia Mono, Consolas, monospace',
    fontSize: 13,
    minimumContrastRatio: 1,
    rightClickSelectsWord: true,
    screenReaderMode: false,
    scrollback: 5000,
    smoothScrollDuration: 0,
    tabStopWidth: 8
  });
  const fitAddon = new window.FitAddon.FitAddon();
  const searchAddon = new window.SearchAddon.SearchAddon();
  terminal.loadAddon(fitAddon);
  terminal.loadAddon(searchAddon);
  terminal.open(terminalElement);
  const outputCapture = new window.SuttyOutputCapture(terminal);

  // The cursor selected in Settings is authoritative. DECSCUSR otherwise leaves
  // a shell-owned shape override in xterm even after its options are updated.
  terminal.parser.registerCsiHandler({ intermediates: ' ', final: 'q' }, () => true);

  let fitTimer = 0;
  let lastColumns = 0;
  let lastRows = 0;
  let lastPixelWidth = 0;
  let lastPixelHeight = 0;
  let searchPalette = {
    matchBackground: '#192127',
    matchBorder: '#2bc7b5',
    matchOverviewRuler: '#2bc7b5',
    activeMatchBackground: '#102f2e',
    activeMatchBorder: '#9fb0c0',
    activeMatchColorOverviewRuler: '#9fb0c0'
  };

  function post(message) {
    bridge.postMessage(Object.assign({ version: protocolVersion }, message));
  }

  function reportError(error) {
    const message = error instanceof Error ? error.message : String(error);
    post({ type: 'error', text: message.slice(0, 2048) });
  }

  function decodeBase64(value) {
    const binary = atob(value);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index += 1) {
      bytes[index] = binary.charCodeAt(index);
    }
    return bytes;
  }

  function fitAndReport() {
    window.clearTimeout(fitTimer);
    fitTimer = 0;
    if (!terminalRoot.isConnected || terminalRoot.clientWidth < 40 || terminalRoot.clientHeight < 30) {
      return;
    }

    try {
      const proposed = fitAddon.proposeDimensions();
      if (!proposed || !Number.isFinite(proposed.cols) || !Number.isFinite(proposed.rows)) {
        return;
      }
      // Match the PTY contract even when a split pane is very small or very large.
      const columns = Math.max(20, Math.min(500, proposed.cols));
      const rows = Math.max(5, Math.min(200, proposed.rows));
      if (columns !== terminal.cols || rows !== terminal.rows) {
        terminal.resize(columns, rows);
      }
      const pixelWidth = Math.max(0, Math.floor(terminalRoot.clientWidth));
      const pixelHeight = Math.max(0, Math.floor(terminalRoot.clientHeight));
      if (terminal.cols === lastColumns && terminal.rows === lastRows &&
          pixelWidth === lastPixelWidth && pixelHeight === lastPixelHeight) {
        return;
      }

      lastColumns = terminal.cols;
      lastRows = terminal.rows;
      lastPixelWidth = pixelWidth;
      lastPixelHeight = pixelHeight;
      post({
        type: 'resize',
        columns: terminal.cols,
        rows: terminal.rows,
        pixelWidth: pixelWidth,
        pixelHeight: pixelHeight
      });
    } catch (error) {
      reportError(error);
    }
  }

  function scheduleFit() {
    if (fitTimer !== 0) {
      window.clearTimeout(fitTimer);
    }
    fitTimer = window.setTimeout(fitAndReport, 60);
  }

  function searchOptions() {
    return {
      caseSensitive: false,
      incremental: true,
      decorations: searchPalette
    };
  }

  function validColor(value, fallback) {
    return typeof value === 'string' && /^#[0-9a-f]{6}$/i.test(value) ? value : fallback;
  }

  function mixColor(first, second, amount) {
    let result = '#';
    for (let offset = 1; offset < 7; offset += 2) {
      const a = parseInt(first.slice(offset, offset + 2), 16);
      const b = parseInt(second.slice(offset, offset + 2), 16);
      result += Math.round(a + (b - a) * amount).toString(16).padStart(2, '0');
    }
    return result;
  }

  function luminance(color) {
    const channels = [1, 3, 5].map(offset => {
      const channel = parseInt(color.slice(offset, offset + 2), 16) / 255;
      return channel <= 0.04045 ? channel / 12.92 : Math.pow((channel + 0.055) / 1.055, 2.4);
    });
    return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
  }

  function contrast(first, second) {
    const a = luminance(first);
    const b = luminance(second);
    return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
  }

  function readableColor(color, backgrounds, minimum) {
    const minimumContrast = candidate => Math.min(...backgrounds.map(background => contrast(candidate, background)));
    if (minimumContrast(color) >= minimum) {
      return color;
    }
    const target = minimumContrast('#ffffff') >= minimumContrast('#000000') ? '#ffffff' : '#000000';
    for (let step = 1; step <= 100; step += 1) {
      const candidate = mixColor(color, target, step / 100);
      if (minimumContrast(candidate) >= minimum) {
        return candidate;
      }
    }
    return target;
  }

  function applyTheme(theme) {
    const background = validColor(theme.background, '#0a0d10');
    const terminalForeground = validColor(theme.foreground, '#9fb0c0');
    const surface = mixColor(background, terminalForeground, 0.055);
    const surfaces = [background, surface];
    const foreground = readableColor(terminalForeground, surfaces, 4.5);
    const cursor = validColor(theme.cursor, terminalForeground);
    const selection = validColor(theme.selectionBackground, mixColor(background, cursor, 0.30));
    const backgroundLuminance = luminance(background);
    const accent = readableColor(cursor, surfaces, 3);
    const rootStyle = document.documentElement.style;
    rootStyle.colorScheme = backgroundLuminance > 0.5 ? 'light' : 'dark';
    rootStyle.setProperty('--terminal-background', background);
    rootStyle.setProperty('--foreground', foreground);
    rootStyle.setProperty('--selection', selection);
    rootStyle.setProperty('--surface', surface);
    rootStyle.setProperty('--border', mixColor(background, terminalForeground, 0.24));
    rootStyle.setProperty('--muted', readableColor(mixColor(background, terminalForeground, 0.80), surfaces, 4.5));
    rootStyle.setProperty('--accent', accent);
    searchPalette = {
      matchBackground: mixColor(background, terminalForeground, 0.10),
      matchBorder: accent,
      matchOverviewRuler: accent,
      activeMatchBackground: mixColor(background, accent, 0.18),
      activeMatchBorder: foreground,
      activeMatchColorOverviewRuler: foreground
    };

    if (!searchElement.hidden && searchInput.value) {
      // Rebuild decoration colors without advancing the selected result or scrolling.
      const selected = terminal.getSelectionPosition();
      const viewport = terminal.buffer.active.viewportY;
      const restoreSelection = () => {
        if (selected) {
          terminal.select(selected.start.x, selected.start.y,
            (selected.end.y - selected.start.y) * terminal.cols + selected.end.x - selected.start.x);
        }
      };
      searchAddon.clearDecorations();
      restoreSelection();
      searchAddon.findNext(searchInput.value, searchOptions());
      restoreSelection();
      terminal.scrollToLine(viewport);
    }
  }

  function findNext() {
    const value = searchInput.value;
    if (value) {
      searchAddon.findNext(value, searchOptions());
    }
  }

  function findPrevious() {
    const value = searchInput.value;
    if (value) {
      searchAddon.findPrevious(value, searchOptions());
    }
  }

  function showSearch() {
    searchElement.hidden = false;
    searchInput.focus();
    searchInput.select();
  }

  function hideSearch() {
    searchAddon.clearDecorations();
    searchElement.hidden = true;
    terminal.focus();
  }

  searchInput.addEventListener('input', findNext);
  searchInput.addEventListener('keydown', function (event) {
    if (event.key === 'Escape') {
      event.preventDefault();
      hideSearch();
    } else if (event.key === 'Enter') {
      event.preventDefault();
      if (event.shiftKey) {
        findPrevious();
      } else {
        findNext();
      }
    }
  });
  searchPrevious.addEventListener('click', findPrevious);
  searchNext.addEventListener('click', findNext);
  searchClose.addEventListener('click', hideSearch);

  function handleAppShortcut(event) {
    if (event.altKey && !event.ctrlKey && !event.shiftKey) {
      const navigationMatch = /^Digit([1-8])$/.exec(event.code);
      if (navigationMatch) {
        event.preventDefault();
        event.stopPropagation();
        post({ type: 'appShortcut', action: 'navigate', number: Number(navigationMatch[1]) });
        return true;
      }
    }

    if (event.ctrlKey && !event.altKey && !event.shiftKey) {
      const tabMatch = /^Digit([1-9])$/.exec(event.code);
      let action = null;
      let number = 0;
      if (tabMatch) {
        action = 'selectTab';
        number = Number(tabMatch[1]);
      } else if (event.code === 'KeyT') {
        action = 'newTab';
      } else if (event.code === 'Comma') {
        action = 'settings';
      }

      if (action) {
        event.preventDefault();
        event.stopPropagation();
        post({ type: 'appShortcut', action: action, number: number });
        return true;
      }
    }

    return false;
  }

  // Capture before xterm's hidden textarea or the search input sees the key so the
  // application shortcut has identical behaviour for every focus target in WebView2.
  document.addEventListener('keydown', handleAppShortcut, true);

  terminal.attachCustomKeyEventHandler(function (event) {
    if (event.type !== 'keydown') {
      return true;
    }

    const key = event.key.toLowerCase();
    if (event.ctrlKey && !event.altKey && key === 'f') {
      event.preventDefault();
      showSearch();
      return false;
    }

    if ((event.ctrlKey && event.key === 'Insert') ||
        (event.ctrlKey && event.shiftKey && key === 'c' && terminal.hasSelection())) {
      event.preventDefault();
      post({ type: 'copy', text: terminal.getSelection().slice(0, 4 * 1024 * 1024) });
      return false;
    }

    if ((event.shiftKey && event.key === 'Insert') ||
        (event.ctrlKey && event.shiftKey && key === 'v')) {
      event.preventDefault();
      post({ type: 'pasteRequest' });
      return false;
    }

    return true;
  });

  terminal.onData(function (data) {
    if (typeof data === 'string' && data.length <= 4 * 1024 * 1024) {
      outputCapture.input(data);
      post({ type: 'input', data: data });
    }
  });

  terminal.onResize(scheduleFit);
  terminal.onTitleChange(function (title) {
    post({ type: 'title', text: String(title || '').slice(0, 256) });
  });

  function applyOptions(message) {
    outputCapture.shell = message.outputShell;
    const theme = message.theme || terminal.options.theme || {};
    terminal.options.fontFamily = typeof message.fontFamily === 'string'
      ? message.fontFamily.slice(0, 256)
      : terminal.options.fontFamily;
    terminal.options.fontSize = Number.isInteger(message.fontSize)
      ? Math.max(8, Math.min(32, message.fontSize))
      : terminal.options.fontSize;
    terminal.options.cursorStyle = ['block', 'bar', 'underline'].includes(message.cursorStyle)
      ? message.cursorStyle
      : 'bar';
    terminal.options.cursorBlink = message.cursorBlink !== false;
    terminal.options.scrollback = Number.isInteger(message.scrollback)
      ? Math.max(100, Math.min(50000, message.scrollback))
      : 5000;
    terminal.options.screenReaderMode = message.screenReaderMode === true;
    terminal.options.theme = theme;

    applyTheme(theme);

    const korean = message.language === 'ko';
    searchInput.placeholder = korean ? '터미널 출력 검색' : 'Search terminal output';
    searchElement.setAttribute('aria-label', korean ? '터미널 출력 검색' : 'Search terminal output');
    searchPrevious.setAttribute('aria-label', korean ? '이전 일치 항목' : 'Previous match');
    searchNext.setAttribute('aria-label', korean ? '다음 일치 항목' : 'Next match');
    searchClose.setAttribute('aria-label', korean ? '검색 닫기' : 'Close search');
    scheduleFit();
  }

  bridge.addEventListener('message', function (event) {
    const message = event.data;
    if (!message || message.version !== protocolVersion || typeof message.type !== 'string') {
      return;
    }

    try {
      switch (message.type) {
        case 'write': {
          if (typeof message.data !== 'string' || !Number.isSafeInteger(message.id)) {
            return;
          }
          const bytes = decodeBase64(message.data);
          terminal.write(bytes, function () {
            outputCapture.parsed();
            post({ type: 'writeComplete', id: message.id });
          });
          break;
        }
        case 'reset':
          outputCapture.reset();
          terminal.reset();
          terminal.clear();
          if (typeof message.text === 'string' && message.text.length > 0) {
            terminal.write(message.text.slice(0, 4096));
          }
          break;
        case 'copyLatestOutput': {
          const text = outputCapture.snapshot();
          if (!outputCapture.hasSnapshot()) {
            post({ type: 'outputCopyUnavailable', id: message.id });
            break;
          }
          // Preserve the complete output, including an empty result. Split bridge
          // messages instead of silently truncating at the selection-copy limit.
          post({ type: 'outputCopyStart', id: message.id });
          for (const chunk of window.SuttyOutputCapture.copyChunks(text)) {
            post({ type: 'outputCopyChunk', id: message.id,
              text: chunk });
          }
          post({ type: 'outputCopyEnd', id: message.id });
          break;
        }
        case 'options':
          applyOptions(message);
          break;
        case 'paste':
          if (typeof message.text === 'string' && message.text.length <= 4 * 1024 * 1024) {
            terminal.paste(message.text);
          }
          break;
        case 'focus':
          terminal.focus();
          break;
        case 'findNext':
          showSearch();
          if (typeof message.text === 'string') {
            searchInput.value = message.text.slice(0, 1024);
          }
          findNext();
          break;
        case 'findPrevious':
          showSearch();
          if (typeof message.text === 'string') {
            searchInput.value = message.text.slice(0, 1024);
          }
          findPrevious();
          break;
        default:
          break;
      }
    } catch (error) {
      reportError(error);
    }
  });

  const resizeObserver = new ResizeObserver(scheduleFit);
  resizeObserver.observe(terminalRoot);
  window.addEventListener('focus', function () {
    if (searchElement.hidden) terminal.focus();
    else searchInput.focus();
  });
  applyOptions({});
  scheduleFit();
  post({ type: 'ready', text: 'xterm.js 6.0.0' });
}());

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
    allowProposedApi: false,
    allowTransparency: false,
    convertEol: false,
    cursorBlink: true,
    cursorStyle: 'underline',
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

  let fitTimer = 0;
  let lastColumns = 0;
  let lastRows = 0;
  let lastPixelWidth = 0;
  let lastPixelHeight = 0;

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
      fitAddon.fit();
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
      decorations: {
        matchBackground: '#4a5568',
        matchBorder: '#6ee7d8',
        matchOverviewRuler: '#6ee7d8',
        activeMatchBackground: '#256f78',
        activeMatchBorder: '#ffffff',
        activeMatchColorOverviewRuler: '#ffffff'
      }
    };
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
      post({ type: 'input', data: data });
    }
  });

  terminal.onResize(scheduleFit);
  terminal.onTitleChange(function (title) {
    post({ type: 'title', text: String(title || '').slice(0, 256) });
  });

  function applyOptions(message) {
    const theme = message.theme || {};
    terminal.options.fontFamily = typeof message.fontFamily === 'string'
      ? message.fontFamily.slice(0, 256)
      : terminal.options.fontFamily;
    terminal.options.fontSize = Number.isInteger(message.fontSize)
      ? Math.max(8, Math.min(32, message.fontSize))
      : terminal.options.fontSize;
    terminal.options.cursorStyle = ['block', 'bar', 'underline'].includes(message.cursorStyle)
      ? message.cursorStyle
      : 'underline';
    terminal.options.cursorBlink = message.cursorBlink !== false;
    terminal.options.scrollback = Number.isInteger(message.scrollback)
      ? Math.max(100, Math.min(50000, message.scrollback))
      : 5000;
    terminal.options.screenReaderMode = message.screenReaderMode === true;
    terminal.options.theme = theme;

    const background = typeof theme.background === 'string' ? theme.background : '#08111f';
    const foreground = typeof theme.foreground === 'string' ? theme.foreground : '#d7e2f0';
    const selection = typeof theme.selectionBackground === 'string' ? theme.selectionBackground : '#315878';
    document.documentElement.style.setProperty('--terminal-background', background);
    document.documentElement.style.setProperty('--foreground', foreground);
    document.documentElement.style.setProperty('--selection', selection);

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
            post({ type: 'writeComplete', id: message.id });
          });
          break;
        }
        case 'reset':
          terminal.reset();
          terminal.clear();
          if (typeof message.text === 'string' && message.text.length > 0) {
            terminal.write(message.text.slice(0, 4096));
          }
          break;
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
  window.addEventListener('focus', function () { terminal.focus(); });
  applyOptions({});
  scheduleFit();
  post({ type: 'ready', text: 'xterm.js 6.0.0' });
}());

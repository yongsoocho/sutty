(function (root, factory) {
  'use strict';
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.SuttyOutputCapture = factory();
  }
}(typeof window === 'object' ? window : globalThis, function () {
  'use strict';

  // Capture the rendered command, never an arbitrary transport packet. OSC 133/633
  // gives authoritative prompt/execution boundaries. Unintegrated remote shells
  // use the prompt visible before typing as a conservative fallback.
  class OutputCapture {
    static *copyChunks(text, size = 256 * 1024) {
      size = Math.max(2, size);
      for (let offset = 0; offset < text.length;) {
        let end = Math.min(text.length, offset + size);
        const last = text.charCodeAt(end - 1);
        const next = text.charCodeAt(end);
        // A lone UTF-16 surrogate is invalid in System.Text.Json. Keep the pair
        // within one bridge message, including an emoji exactly on the boundary.
        if (last >= 0xd800 && last <= 0xdbff && next >= 0xdc00 && next <= 0xdfff) end -= 1;
        yield text.slice(offset, end);
        offset = end;
      }
    }

    constructor(terminal) {
      this.terminal = terminal;
      this.reset();
      terminal.parser.registerOscHandler(133, data => this.shellMarker(data));
      terminal.parser.registerOscHandler(633, data => this.shellMarker(data));
      terminal.onLineFeed(() => this.lineFeed());
      terminal.onScroll(() => this.archiveScrolledLines());
    }

    reset() {
      this.disposeMarkers();
      this.latest = '';
      this.parts = [];
      this.active = false;
      this.awaitingEcho = false;
      this.integrated = false;
      this.remoteFallback = false;
      this.atPrompt = false;
      this.prompt = '';
      this.commandPrompt = '';
      this.hasInput = false;
    }

    disposeMarkers() {
      if (this.start) this.start.dispose();
      if (this.submitted) this.submitted.dispose();
      this.start = null;
      this.submitted = null;
    }

    position() {
      const buffer = this.terminal.buffer.active;
      return { line: buffer.baseY + buffer.cursorY, column: buffer.cursorX };
    }

    // Unlike translateToString(true), this preserves spaces explicitly written by
    // the application. Unwritten terminal padding has empty getChars(), not ' '.
    lineText(lineIndex, startColumn, endColumn) {
      const line = this.terminal.buffer.normal.getLine(lineIndex);
      if (!line) return '';
      const limit = Math.min(endColumn, line.length);
      let lastWritten = startColumn;
      for (let column = startColumn; column < limit; column += 1) {
        const cell = line.getCell(column);
        if (cell && cell.getChars() !== '') lastWritten = column + cell.getWidth();
      }
      return line.translateToString(false, startColumn, Math.min(lastWritten, limit));
    }

    range(startLine, startColumn, endLine, endColumn) {
      const buffer = this.terminal.buffer.normal;
      let result = '';
      for (let row = Math.max(0, startLine); row <= endLine; row += 1) {
        const line = buffer.getLine(row);
        if (!line) break;
        if (row > startLine && !line.isWrapped) result += '\r\n';
        result += this.lineText(row, row === startLine ? startColumn : 0,
          row === endLine ? endColumn : line.length);
      }
      return result;
    }

    setStart(column) {
      if (this.start) this.start.dispose();
      this.start = this.terminal.registerMarker(0);
      this.startColumn = column;
    }

    shellMarker(data) {
      const kind = data.split(';', 1)[0];
      if (!['A', 'B', 'C', 'D'].includes(kind)) return false;
      this.integrated = true;
      this.remoteFallback = false;
      if (kind === 'A' || kind === 'D') {
        if (this.active) this.finish(this.position(), true);
        if (kind === 'A') {
          this.atPrompt = false;
          this.hasInput = false;
        }
      } else if (kind === 'B') {
        this.atPrompt = true;
        const position = this.position();
        this.prompt = this.lineText(position.line, 0, position.column);
        if (this.submitted) this.submitted.dispose();
        this.submitted = this.terminal.registerMarker(0);
      } else if (kind === 'C') {
        // Explicit execution markers also handle multiline commands and shells
        // whose input echo is disabled.
        this.disposeMarkers();
        this.parts = [];
        this.active = true;
        this.awaitingEcho = false;
        this.atPrompt = false;
        this.setStart(this.position().column);
      }
      return true;
    }

    input(data) {
      if (this.terminal.buffer.active.type !== 'normal') return;
      // Terminal device replies are input too, but are not command submission.
      if (!data || (!data.includes('\r') && !data.includes('\n'))) {
        if (!this.active && !this.hasInput && data && !data.startsWith('\x1b')) {
          const position = this.position();
          this.prompt = this.lineText(position.line, 0, position.column);
          this.hasInput = true;
        }
        return;
      }
      if (data.startsWith('\x1b[200~')) return; // Bracketed paste is edited first.
      if (this.active) {
        const position = this.position();
        const line = this.lineText(position.line, 0, position.column);
        if (!/^(?:>> |More\? )/.test(line)) return; // Input to a running program.
      }
      this.disposeMarkers();
      this.parts = [];
      this.active = true;
      this.awaitingEcho = true;
      this.atPrompt = false;
      this.commandPrompt = this.prompt;
      this.submitted = this.terminal.registerMarker(0);
    }

    lineFeed() {
      if (!this.active && this.atPrompt && this.integrated) {
        // Native command launchers can write directly to the PTY. Its echoed
        // command terminator still provides the same output start boundary.
        this.active = true;
        this.awaitingEcho = true;
        this.parts = [];
        this.atPrompt = false;
      }
      if (!this.active || this.terminal.buffer.active.type !== 'normal') return;
      if (this.awaitingEcho) {
        const position = this.position();
        const firstOutputLine = this.outputStartLine();
        if (firstOutputLine !== null && firstOutputLine < position.line) {
          const previous = position.line - 1;
          this.parts.push(this.range(firstOutputLine, 0, previous,
            this.terminal.buffer.normal.getLine(previous).length));
          this.parts.push('\r\n');
        }
        this.awaitingEcho = false;
      } else if (this.start && !this.start.isDisposed) {
        const position = this.position();
        // onLineFeed is a hard newline; xterm's automatic soft wrap does not fire
        // it. Archive completed lines here so scrollback eviction loses no output.
        const previous = position.line - 1;
        if (previous >= this.start.line) {
          this.parts.push(this.range(this.start.line, this.startColumn,
            previous, this.terminal.buffer.normal.getLine(previous).length));
          this.parts.push('\r\n');
        }
      }
      this.setStart(0);
    }

    outputStartLine() {
      if (!this.submitted || this.submitted.isDisposed) return null;
      let line = this.submitted.line + 1;
      // A pasted/long command may wrap before its final echo newline. Those rows
      // still belong to input, while the first unwrapped row starts output.
      while (this.terminal.buffer.normal.getLine(line)?.isWrapped) line += 1;
      return line;
    }

    archiveScrolledLines() {
      const buffer = this.terminal.buffer.normal;
      if (!this.active || this.awaitingEcho || !this.start || this.start.isDisposed ||
          this.terminal.buffer.active.type !== 'normal' || this.start.line >= buffer.baseY) return;
      const last = buffer.baseY - 1;
      this.parts.push(this.range(this.start.line, this.startColumn, last,
        buffer.getLine(last).length));
      if (!buffer.getLine(buffer.baseY).isWrapped) this.parts.push('\r\n');
      this.start.dispose();
      this.start = this.terminal.registerMarker(-buffer.cursorY);
      this.startColumn = 0;
    }

    pendingText(end) {
      if (this.start && !this.start.isDisposed) {
        return this.range(this.start.line, this.startColumn, end.line, end.column);
      }
      // ConPTY can encode a line transition with cursor addressing instead of LF.
      if (this.awaitingEcho && this.submitted && !this.submitted.isDisposed &&
          end.line > this.submitted.line) {
        return this.range(this.outputStartLine(), 0, end.line, end.column);
      }
      return '';
    }

    finish(end, shellBoundary = false) {
      this.latest = this.parts.join('') + this.pendingText(end);
      // cmd.exe prints one separator before PROMPT, even for `cd .`. Remove only
      // that shell-owned separator; preserve every blank line from the command.
      if (shellBoundary && this.shell === 'cmd' && this.latest.endsWith('\r\n')) {
        this.latest = this.latest.slice(0, -2);
      }
      this.parts = [];
      this.disposeMarkers();
      this.active = false;
      this.awaitingEcho = false;
      this.hasInput = false;
    }

    parsed() {
      if (this.terminal.buffer.active.type !== 'normal') return;
      const position = this.position();
      const text = this.lineText(position.line, 0, position.column);
      const conventional = /^(?:PS (?:[^\r\n]*?)> ?|[A-Za-z]:\\[^\r\n]*>|[^\r\n]*[@:][^\r\n]*[$#%] |[$#%] )$/.test(text);
      const learned = this.commandPrompt && text === this.commandPrompt;
      // An integrated parent may launch a markerless nested SSH shell. Its first
      // recognizable prompt ends the launch output; subsequent submissions then
      // use the learned remote prompt until the parent's OSC markers return.
      const nestedPrompt = this.integrated && conventional && text !== this.prompt &&
        !text.startsWith('PS ') && !/^[A-Za-z]:\\/.test(text);
      if (this.active && ((!this.integrated && (conventional || learned)) || nestedPrompt ||
          (this.remoteFallback && (conventional || learned)))) {
        this.finish({ line: position.line, column: 0 });
        this.remoteFallback = this.integrated;
        this.atPrompt = true;
        this.prompt = text;
      } else if (!this.active && !this.hasInput && conventional) {
        this.prompt = text;
        this.atPrompt = true;
      }
    }

    snapshot() {
      this.parsed();
      if (this.terminal.buffer.active.type !== 'normal') return this.latest;
      return this.active ? this.parts.join('') + this.pendingText(this.position()) : this.latest;
    }
  }

  return OutputCapture;
}));

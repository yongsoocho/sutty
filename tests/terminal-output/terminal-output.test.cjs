'use strict';

const assert = require('node:assert/strict');
const { test } = require('node:test');
const { Terminal } = require('../../src/sutty.UI/Assets/Terminal/xterm-6.0.0.js');
const OutputCapture = require('../../src/sutty.UI/Assets/Terminal/sutty-output-capture.js');
const marker = value => `\x1b]133;${value}\x07`;

function fixture(columns = 40, scrollback = 100) {
  const terminal = new Terminal({ cols: columns, rows: 5, scrollback });
  const capture = new OutputCapture(terminal);
  const write = data => new Promise(resolve => terminal.write(data, () => {
    capture.parsed();
    resolve();
  }));
  const prompt = async (text = 'PS C:\\> ') => {
    await write(marker('A') + text + marker('B'));
  };
  const command = async (text = 'command') => {
    capture.input(text);
    await write(text);
    capture.input('\r');
    await write('\r\n');
  };
  return { terminal, capture, write, prompt, command };
}

test('combined output preserves indentation, explicit trailing spaces and final blank lines', async () => {
  const f = fixture();
  await f.prompt();
  await f.command();
  await f.write('  stdout  \r\n\x1b[31mstderr\x1b[0m\r\n\r\n');
  await f.prompt();
  assert.equal(f.capture.snapshot(), '  stdout  \r\nstderr\r\n\r\n');
  f.terminal.dispose();
});

test('split UTF-8 and shell markers do not become packet-sized output', async () => {
  const f = fixture();
  await f.prompt();
  await f.command();
  const wire = Buffer.from('한글 😀\r\nsecond\r\n' + marker('A') + 'PS C:\\> ' + marker('B'));
  for (let index = 0; index < wire.length; index += 1) {
    await f.write(new Uint8Array(wire.subarray(index, index + 1)));
  }
  assert.equal(f.capture.snapshot(), '한글 😀\r\nsecond\r\n');
  f.terminal.dispose();
});

test('a newer empty command clears old output, excluding both echo and prompt', async () => {
  const f = fixture();
  await f.prompt('C:\\>');
  await f.command('echo old');
  await f.write('old\r\n');
  await f.prompt('C:\\>');
  assert.equal(f.capture.snapshot(), 'old\r\n');
  await f.command('cd .');
  await f.prompt('C:\\>');
  assert.equal(f.capture.snapshot(), '');
  f.terminal.dispose();
});

test('soft wraps concatenate while hard newlines and whitespace-only lines survive', async () => {
  const f = fixture(20);
  await f.prompt();
  await f.command();
  const output = '0123456789'.repeat(6) + '  \r\n   \r\n';
  await f.write(output);
  await f.prompt();
  assert.equal(f.capture.snapshot(), output);
  f.terminal.dispose();
});

test('output before scrollback eviction remains part of the last command', async () => {
  const f = fixture(20, 3);
  await f.prompt();
  await f.command();
  const output = Array.from({ length: 80 }, (_, index) => `line ${index}\r\n`).join('');
  await f.write(output);
  await f.prompt();
  assert.equal(f.capture.snapshot(), output);
  f.terminal.dispose();
});

test('one long soft-wrapped output survives scrollback eviction without added newlines', async () => {
  const f = fixture(20, 3);
  await f.prompt();
  await f.command();
  const output = '0123456789'.repeat(150) + '\r\n';
  await f.write(output);
  await f.prompt();
  assert.equal(f.capture.snapshot(), output);
  f.terminal.dispose();
});

test('ConPTY cursor addressing preserves leading blank output and excludes wrapped command echo', async () => {
  const f = fixture(20);
  f.terminal.resize(20, 12);
  f.capture.shell = 'cmd';
  await f.prompt('C:\\>');
  f.capture.input('echo(&echo(  stdout  &echo(  stderr  &echo(\r');
  await f.write('echo(&echo(  stdout  &echo(  stderr  &echo(\x1b[5;1H  stdout  \r\n  stderr  \x1b[9;1H');
  await f.prompt('C:\\>');
  assert.equal(f.capture.snapshot(), '\r\n  stdout  \r\n  stderr  \r\n\r\n');
  f.terminal.dispose();
});

test('CMD removes exactly its own separator and retains command blank lines', async () => {
  const f = fixture();
  f.capture.shell = 'cmd';
  await f.prompt('C:\\>');
  await f.command('cd .');
  await f.write('\r\n');
  await f.prompt('C:\\>');
  assert.equal(f.capture.snapshot(), '');
  await f.command('echo(&echo(');
  await f.write('\r\n\r\n\r\n');
  await f.prompt('C:\\>');
  assert.equal(f.capture.snapshot(), '\r\n\r\n');
  f.terminal.dispose();
});

test('copy during execution returns all output so far and handles carriage-return repaint', async () => {
  const f = fixture();
  await f.prompt();
  await f.command();
  await f.write('first\r\n0%\r100%');
  assert.equal(f.capture.snapshot(), 'first\r\n100%');
  await f.write('\r\n');
  await f.prompt();
  assert.equal(f.capture.snapshot(), 'first\r\n100%\r\n');
  f.terminal.dispose();
});

test('OSC C/D boundaries support no input echo and unterminated output', async () => {
  const f = fixture();
  await f.prompt();
  await f.write('\r\n' + marker('C') + '  no newline  ' + marker('D;0'));
  await f.prompt();
  assert.equal(f.capture.snapshot(), '  no newline  ');
  f.terminal.dispose();
});

test('typing the next command does not replace the previous output', async () => {
  const f = fixture();
  await f.prompt();
  await f.command();
  await f.write('result\r\n');
  await f.prompt();
  f.capture.input('next');
  await f.write('next');
  assert.equal(f.capture.snapshot(), 'result\r\n');
  f.terminal.dispose();
});

test('ordinary remote prompts delimit output without modifying the remote shell', async () => {
  const f = fixture();
  await f.write('user@server:~$ ');
  await f.command('ls');
  await f.write('a.txt\r\n  b.txt  \r\nuser@server:~$ ');
  assert.equal(f.capture.snapshot(), 'a.txt\r\n  b.txt  \r\n');
  f.terminal.dispose();
});

test('markerless SSH nested inside integrated CMD captures the latest remote command', async () => {
  const f = fixture();
  f.capture.shell = 'cmd';
  await f.prompt('C:\\>');
  await f.command('ssh server');
  await f.write('Welcome\r\nuser@server:~$ ');
  assert.equal(f.capture.snapshot(), 'Welcome\r\n');
  await f.command('echo hello');
  await f.write('hello\r\nuser@server:~$ ');
  assert.equal(f.capture.snapshot(), 'hello\r\n');
  await f.command('true');
  await f.write('user@server:~$ ');
  assert.equal(f.capture.snapshot(), '');
  await f.command('exit');
  await f.write('logout\r\n\r\n');
  await f.prompt('C:\\>');
  await f.command('cd .');
  await f.write('\r\n');
  await f.prompt('C:\\>');
  assert.equal(f.capture.snapshot(), '');
  f.terminal.dispose();
});

test('native command input is tracked through prompt and echo boundaries', async () => {
  const f = fixture();
  await f.prompt();
  await f.write('echo result\r\nresult\r\n');
  await f.prompt();
  assert.equal(f.capture.snapshot(), 'result\r\n');
  f.terminal.dispose();
});

test('clipboard bridge chunks never split surrogate pairs or lose large output', () => {
  const output = 'x'.repeat(256 * 1024 - 1) + '😀한글' + 'y'.repeat(5 * 1024 * 1024) + '\r\n\r\n';
  const chunks = [...OutputCapture.copyChunks(output)];
  assert.equal(chunks.join(''), output);
  assert.ok(chunks.length > 16);
  for (const chunk of chunks) {
    assert.ok(chunk.length <= 256 * 1024);
    assert.equal(chunk.isWellFormed(), true);
    assert.equal(JSON.parse(JSON.stringify(chunk)), chunk);
  }
});

test('terminal reset clears all captures and integration state', async () => {
  const f = fixture();
  await f.prompt();
  await f.command();
  await f.write('secret\r\n');
  await f.prompt();
  f.capture.reset();
  f.terminal.reset();
  assert.equal(f.capture.snapshot(), '');
  assert.equal(f.capture.hasSnapshot(), false);
  f.terminal.dispose();
});

test('an unknown response differs from a confirmed empty command response', async () => {
  const f = fixture();
  await f.prompt();
  assert.equal(f.capture.hasSnapshot(), false);
  await f.command('no-output');
  await f.prompt();
  assert.equal(f.capture.snapshot(), '');
  assert.equal(f.capture.hasSnapshot(), true);
  f.terminal.dispose();
});

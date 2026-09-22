'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { test } = require('node:test');
const { Terminal } = require('../../src/sutty.UI/Assets/Terminal/xterm-6.0.0.js');
const OutputCapture = require('../../src/sutty.UI/Assets/Terminal/sutty-output-capture.js');

// Generate with dotnet run --project tests/sutty.Terminal.SelfTest --
// --capture-copy-replay <directory>, then set SUTTY_COPY_REPLAY_DIRECTORY.
for (const shell of ['PowerShell', 'CommandPrompt']) {
  test(`actual ${shell} ConPTY output is copied without echo, prompts or whitespace loss`,
    { skip: !process.env.SUTTY_COPY_REPLAY_DIRECTORY }, async () => {
      const records = JSON.parse(fs.readFileSync(path.join(
        process.env.SUTTY_COPY_REPLAY_DIRECTORY, shell + '.json'), 'utf8'));
      const terminal = new Terminal({ cols: 80, rows: 24, scrollback: 5000 });
      const capture = new OutputCapture(terminal);
      capture.shell = shell === 'CommandPrompt' ? 'cmd' : undefined;
      for (const record of records) {
        if (record.Type === 'input') capture.input(record.Data);
        else if (record.Type === 'snapshot') assert.equal(capture.snapshot(), record.Data);
        else await new Promise(resolve => terminal.write(new Uint8Array(
          Buffer.from(record.Data, 'base64')), () => { capture.parsed(); resolve(); }));
      }
      terminal.dispose();
    });
}

# Terminal output clipboard tests

Run the packaged xterm.js capture tests with Node.js:

```powershell
node --test tests/terminal-output/terminal-output.test.cjs
```

On Windows, capture real PowerShell and CMD ConPTY traffic and replay it through
the same renderer used in the app:

```powershell
$replayDirectory = Join-Path $env:TEMP 'sutty-copy-replay'
dotnet run --project tests/sutty.Terminal.SelfTest -- --capture-copy-replay $replayDirectory
$env:SUTTY_COPY_REPLAY_DIRECTORY = $replayDirectory
node --test tests/terminal-output/*.test.cjs
```

The fixtures are generated locally and contain the shell's displayed working
directory. PowerShell history saving is disabled only inside the test child.
Native replay verifies combined stdout/stderr, command echo and prompt exclusion,
empty results, rendered whitespace, and the shell's handling of unterminated lines.
The renderer tests also cover split UTF-8/OSC sequences, clipboard message Unicode
boundaries, long output beyond scrollback, soft wrapping, and nested SSH prompts.

Clipboard text reflects the terminal's rendered output. ConPTY can encode blank
cells as erase operations, and shells can add a newline before their prompts;
the original stdout byte stream is not available from the rendered PTY screen.

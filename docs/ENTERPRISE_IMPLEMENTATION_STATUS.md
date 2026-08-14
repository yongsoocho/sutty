# Sutty Windows Enterprise implementation status

This document maps the current repository to the Windows Enterprise Product Plan. It is intentionally evidence-based: **Implemented** means code and focused verification exist; it does not imply a GA release.

## Product boundary

- Windows 11 desktop application, local-first, with no account or cloud control plane.
- SSH, interactive Terminal, structured REPL commands, SFTP Files, reusable Commands, and selected-session Multi operations.
- English and Korean first-party UI.
- Fresh production storage contains no sample hosts, commands, credentials, or connection history.

## Implemented foundation

| Area | Current evidence |
| --- | --- |
| Runtime and architecture | .NET 10; x64 and ARM64 solution platforms; UI, Core, Setting, and Command projects have explicit responsibilities. |
| Real sessions | Production session creation uses the SSH.NET-backed session only. SSH, Terminal, and SFTP states are independent. |
| Host identity | Unknown keys fail closed, one-time and persisted trust are explicit, and changed keys are blocked. |
| Interactive terminal | Persistent SSH PTY and local ConPTY use package-local xterm.js 6.0.0 in a hardened WebView2. ANSI/VT color/style, alternate screen, mouse/input modes, IME/Unicode cells, search, clipboard shortcuts, measured server-side resize, and bounded acknowledged output delivery are integrated. |
| Structured commands | Standard output, standard error, exit status/signal, timing, and cancellation are preserved for REPL and Multi execution. |
| SFTP baseline | Session-bound remote tree with lazy loading, single-file upload/download, safe staging, cancellation, queue limits, rename, delete, and directory creation. |
| Saved Hosts | Explicit SQLite profiles support create/update, delete, search, tags, group, environment, favorites, last-connected time, and opaque credential references. |
| Credential vault | Opt-in AES-256-GCM records use a random master key protected for the current Windows user. Plaintext secrets are excluded from settings, SQLite, history, and crash messages. |
| Connection history | Every completed attempt appends success, failure, or cancellation, bounded diagnostic code, and duration. Duplicate attempts remain separate rows. Retention and frequent-host count are settings. |
<<<<<<< HEAD
| Desktop state | Theme, language, terminal mode, terminal palette/cursor/scrollback/accessibility/profile-loading options, window sizes, and right-panel width persist locally. Resize persistence is debounced. |
=======
| Desktop state | Theme, language, terminal mode, window sizes, and right-panel width persist locally. Resize persistence is debounced. |
>>>>>>> e47dd3e633b929266266b8bb37b277af3130f013
| Route foundation | Direct, HTTP CONNECT, SOCKS4, and SOCKS5 routes are resolved before client creation and shared by SSH/SFTP. Enterprise mode rejects Direct without fallback; one credential-free correlation context is created per session. |
| Terminal productivity | REPL JSON/YAML and severity highlighting, bounded command suggestions, Right/Tab acceptance, tab/navigation/settings shortcuts, and Insert-style copy/paste are implemented. |

## Verification added for this milestone

- Credential round-trip, reload, no-plaintext-at-rest, and authenticated-tamper rejection.
- Empty local database, legacy saved-host migration, profile create/update/favorite/delete, append-only duplicate history, outcome records, and frequent-host aggregation.
- Legacy settings compatibility, value normalization, atomic persistence, panel-width persistence, and corrupt-file fallback.
- Existing terminal parser/input and safe SFTP path/transfer checks remain part of the solution.
<<<<<<< HEAD
- Package-local renderer checks cover restrictive CSP, absence of remote asset URLs and `innerHTML`, input/output/resize bridge primitives, and the reviewed xterm.js SHA-256.
- Route-policy rejection, credential-free audit context, structured-text classification, danger/warning classification, and command-suggestion ordering have focused self-tests.
- SSH.NET is upgraded to 2026.0.0 and lock files are refreshed. x64 Debug and ARM64 Release locked builds are warning-free.

## Remaining release gates

1. Run and record the full shell/TUI/Unicode/input/security/latency/soak matrix for the new package-local renderer; integration alone is not GA evidence.
=======
- Route-policy rejection, credential-free audit context, structured-text classification, danger/warning classification, and command-suggestion ordering have focused self-tests.
- x64 Debug is warning-free. x64 and ARM64 Release builds complete; the only remaining trim diagnostics originate in Windows SDK runtime assemblies rather than Sutty code.

## Remaining release gates

1. Replace the transitional native terminal renderer with the approved hardened, package-local renderer and pass the full shell/TUI/Unicode/input/security matrix.
>>>>>>> e47dd3e633b929266266b8bb37b277af3130f013
2. Complete OTP and multi-prompt keyboard-interactive authentication, Windows SSH agent support, jump hosts, managed gateway profiles, audited route adapters, proxy-DNS verification, reconnect policy, and algorithm-policy UX.
3. Add recursive directory transfer, complete collision policy, verification, retry/resume, restart recovery, symlink policy, and large-file/deep-path evidence.
4. Add local, remote, and dynamic port forwarding with bind-risk warnings and lifecycle tests.
5. Finish streaming command output, typed named parameters, durable Multi details/export, timeouts, and redacted audit events.
6. Complete enterprise policy, managed host catalogs, encrypted import/export, support bundles, accessibility, signed packaging, update, clean-install/upgrade, performance, and soak evidence.

The authoritative requirement-by-requirement status remains in [Requirements Traceability](REQUIREMENTS.md).

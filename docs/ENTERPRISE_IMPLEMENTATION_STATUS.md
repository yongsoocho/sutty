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
| SSH authentication | Password, private keys including PPK v2/v3, Windows SSH Agent, and repeated multi-prompt keyboard-interactive OTP/MFA flows are wired through SSH.NET and the secure UI prompt. |
| Host identity | Unknown keys fail closed, one-time and persisted trust are explicit, and changed keys are blocked. |
| Interactive terminal | Persistent SSH PTY and local ConPTY use package-local xterm.js 6.0.0 in a hardened WebView2. ANSI/VT color/style, alternate screen, mouse/input modes, IME/Unicode cells, search, clipboard shortcuts, measured server-side resize, and bounded acknowledged output delivery are integrated. |
| Structured commands | Standard output, standard error, exit status/signal, timing, and cancellation are preserved for REPL and Multi execution. |
| SFTP baseline | Session-bound lazy tree plus bounded recursive enumeration, recursive file/folder upload and download, symlink non-traversal, deterministic partial files, resume checkpoints, retry, SHA-256 verification, safe promotion, cancellation, queue limits, rename, delete, and directory creation. |
| Multi SFTP | Explicitly checked SFTP sessions support 1→N upload and N→1 download. Server results are isolated, name collisions use deterministic server folders, successful targets remain complete, and retry addresses only failed/incomplete targets. |
| Saved Hosts | Explicit SQLite profiles support create/update, delete, search, tags, group, environment, favorites, last-connected time, and opaque credential references. |
| Credential vault | Opt-in AES-256-GCM records use a random master key protected for the current Windows user. Plaintext secrets are excluded from settings, SQLite, history, and crash messages. |
| Connection history | Every completed attempt appends success, failure, or cancellation, bounded diagnostic code, and duration. Duplicate attempts remain separate rows. Retention and frequent-host count are settings. |
| Desktop state | Theme, language, terminal mode, terminal palette/cursor/scrollback/accessibility/profile-loading options, window sizes, and right-panel width persist locally. Resize persistence is debounced. |
| Route foundation | Direct, HTTP CONNECT, SOCKS4, SOCKS5, SSH jump, and ProxyCommand routes are resolved before client creation and shared by SSH/SFTP. Saved hosts restore credential-free route/tunnel definitions while route secrets remain in the Windows-user vault. Enterprise mode rejects Direct without fallback; one credential-free correlation context is created per session. |
| Forwarding | Local, remote, and dynamic forwarding rules start after target authentication, report runtime failures, and stop before the owning SSH session is disposed. |
| Terminal productivity | REPL JSON/YAML and severity highlighting, bounded command suggestions, Right/Tab acceptance, tab/navigation/settings shortcuts, and Insert-style copy/paste are implemented. |

## Verification added for this milestone

- Credential round-trip, reload, no-plaintext-at-rest, and authenticated-tamper rejection.
- Empty local database, legacy saved-host migration, profile create/update/favorite/delete, append-only duplicate history, outcome records, and frequent-host aggregation.
- Legacy settings compatibility, value normalization, atomic persistence, panel-width persistence, and corrupt-file fallback.
- Existing terminal parser/input and safe SFTP path checks remain part of the solution; transfer checks now cover recursive/empty-directory copy, resume offsets, checksums, non-secret checkpoint persistence, per-target failure isolation, and failed-only retry.
- Package-local renderer checks cover restrictive CSP, absence of remote asset URLs and `innerHTML`, input/output/resize bridge primitives, and the reviewed xterm.js SHA-256.
- Route-policy rejection, credential-free audit context, structured-text classification, danger/warning classification, and command-suggestion ordering have focused self-tests.
- SSH.NET is upgraded to the security-fixed 2026.0.0 release and lock files are refreshed. The Windows Agent adapter loads against this runtime; a live Agent service/key and live server matrix are still required.
- A credential-free atomic transfer queue survives process restart, converts abandoned running work to interrupted state, preserves completed targets, and exposes explicit restore/resume actions in Files and Multi.
- A credentialed live-server harness now covers smoke, disconnect/resume fault injection, configurable 100 GB/100,000-file scale, and 16-session soak modes. These modes are release gates and have not been run without an approved server.
- A manual signed-MSIX workflow validates the production PFX, signs and verifies x64 output, and emits an App Installer descriptor that supports controlled update and rollback. A production certificate and deployment endpoint are still required.

## Remaining release gates

1. Run and record the full shell/TUI/Unicode/input/security/latency/soak matrix for the new package-local renderer; integration alone is not GA evidence.
2. Run the live Windows Agent, repeated OTP/MFA, PPK v2/v3, SSH jump, and ProxyCommand compatibility matrix; add managed gateway profiles, audited route adapters, proxy-DNS verification, full reconnect policy, and algorithm-policy UX.
3. Complete permission policy, pause, recursive delete, and large-file/deep-path/live multi-host SFTP evidence; restart queue discovery and deterministic N→1 collision isolation now have an Alpha baseline.
4. Add forwarding bind-risk warnings, a post-connect tunnel manager, and local/remote/dynamic lifecycle and failure integration tests.
5. Finish streaming command output, typed named parameters, durable Multi details/export, timeouts, and redacted audit events.
6. Complete enterprise policy, managed host catalogs, encrypted import/export, support bundles, accessibility, clean-install/upgrade/rollback, performance, and soak evidence. Signed packaging/update automation exists but has not produced a production-signed acceptance artifact.

The authoritative requirement-by-requirement status remains in [Requirements Traceability](REQUIREMENTS.md).

# Sutty Alpha implementation status

This document maps the current repository to Sutty's local-first Windows SSH/SFTP product scope. It is intentionally evidence-based: **Implemented** means code and focused verification exist; it does not imply a GA release.

## Product boundary

- Windows 11 desktop application, local-first, with no account or cloud control plane.
- Five primary workspaces: Local, Terminal, REPL, Files, and Multi. Saved Hosts and Commands are common capabilities used across those workspaces.
- The current Alpha is centered on per-user local use. Credential-free file/Git sharing packs for small teams, including export, import preview, conflict handling, schema versioning, and local credential binding, are planned and are not part of the implemented foundation below.
- English and Korean first-party UI.
- Fresh production storage contains no sample hosts, commands, credentials, or connection history.

## Implemented foundation

| Area | Current evidence |
| --- | --- |
| Runtime and architecture | .NET 10; x64 and ARM64 solution platforms; UI, Core, Setting, Command, and the pinned Windows SSH Agent compatibility project have explicit responsibilities. |
| Real sessions | Production session creation uses the SSH.NET-backed session only. SSH, Terminal, and SFTP states are independent. Unexpected primary-transport errors retire only the current client generation, clear live handshake data, run best-effort owned-resource cleanup, and publish `Failed` without racing explicit disconnect. |
| SSH authentication | Password, private keys including PPK v2/v3, Windows SSH Agent, and repeated multi-prompt keyboard-interactive OTP/MFA flows are wired through SSH.NET and the secure UI prompt. |
| Host identity | Unknown keys fail closed, one-time and persisted trust are explicit, and changed keys are blocked. |
| Connection information | The primary SSH handshake is captured as an in-memory, credential-free snapshot and exposed in an accessible read-only/copy flyout: server/client identification, KEX, verified host-key algorithm and SHA-256 fingerprint, plus both cipher/MAC/compression directions. Connection alone issues no banner or home-directory discovery command. |
| Interactive terminal | Persistent SSH PTY and local ConPTY use package-local xterm.js 6.0.0 in a hardened WebView2. ANSI/VT color/style, alternate screen, mouse/input modes, IME/Unicode cells, search, clipboard shortcuts, measured server-side resize, and bounded acknowledged output delivery are integrated. |
| Structured commands | Standard output, standard error, exit status/signal, timing, and cancellation are preserved for REPL and Multi execution. |
| SFTP baseline | Session-bound lazy tree plus bounded recursive enumeration and filename search, recursive file/folder upload and download, symlink non-traversal, deterministic partial files, checkpoint resume, durable pause/resume, retry, selectable final-size or SHA-256 verification, five durable conflict policies, safe promotion, cross-directory move without overwrite, preview-confirmed recursive deletion, octal permission changes, cancellation, queue limits, rename, delete, and directory creation. |
| Multi SFTP | Explicitly checked SFTP sessions support 1→N upload and N→1 download. A preflight dialog reviews the targets, source, destination, and conflict policy; server results are isolated, name collisions use deterministic server folders, successful targets remain complete, and retry addresses only failed/incomplete targets with the original policy. |
| Saved Hosts | Explicit SQLite profiles support create/update, delete, search, tags, group, environment, favorites, last-connected time, opaque credential references, and credential-free OpenSSH, Windows saved-session, legacy INI, and SFTP Site Manager XML import. |
| Saved Host launcher | `sutty.UI.exe --host <id or exact name>` resolves an existing Saved Host and enters the same secure connection flow. It rejects credential arguments and does not replace the normal window's Workspace snapshot. |
| Credential vault | Opt-in AES-256-GCM records use a random master key protected for the current Windows user. Plaintext secrets are excluded from settings, SQLite, history, and crash messages. |
| Connection history | Every completed attempt appends success, failure, or cancellation, bounded diagnostic code, and duration. Duplicate attempts remain separate rows. Retention and frequent-host count are settings. |
| Desktop state | Theme, language, terminal mode, terminal palette/cursor/scrollback/accessibility/profile-loading options, window sizes, and right-panel width persist locally. A separate atomic workspace snapshot remembers local tabs and Saved Host ids only, asks before SSH reconnection by default, and never replays commands. Resize and workspace writes are debounced. |
| Route foundation | Direct, HTTP CONNECT, SOCKS4, SOCKS5, SSH jump, and ProxyCommand routes are resolved before client creation and shared by SSH/SFTP. Saved hosts restore credential-free route/tunnel definitions while route secrets remain in the Windows-user vault. Strict route policy rejects Direct without fallback; one credential-free correlation context is created per session. |
| Forwarding | Local, remote, and dynamic forwarding rules start after target authentication, report runtime failures, and stop before the owning SSH session is disposed. |
| Terminal productivity | REPL JSON/YAML and severity highlighting, bounded command suggestions, Right/Tab acceptance, tab/navigation/settings shortcuts, and Insert-style copy/paste are implemented. |

## Verification added for this milestone

- Credential round-trip, reload, no-plaintext-at-rest, and authenticated-tamper rejection.
- Empty local database, legacy saved-host migration, profile create/update/favorite/delete, append-only duplicate history, outcome records, and frequent-host aggregation.
- Legacy settings compatibility, value normalization, atomic persistence, panel-width persistence, corrupt-file fallback, and credential-free 16-tab workspace snapshot normalization/clearing.
- Existing terminal parser/input and safe SFTP path checks remain part of the solution; transfer checks now cover recursive/empty-directory copy, bounded filename search, cross-directory file/folder move and self-descendant rejection, resume offsets, checksums, conflict-policy behavior and durable policy persistence, safe recursive-delete previews, non-secret checkpoint persistence, per-target failure isolation, and failed-only retry.
- Package-local renderer checks cover restrictive CSP, absence of remote asset URLs and `innerHTML`, input/output/resize bridge primitives, and the reviewed xterm.js SHA-256.
- Route-policy rejection, credential-free diagnostic correlation context, structured-text classification, danger/warning classification, and command-suggestion ordering have focused self-tests.
- SSH.NET negotiated-property and transport-error event availability, the immutable connection-information field allowlist, normalization, and absence on new/failed connections have focused automated checks. Static source review confirms removal of the automatic remote commands. Live fingerprint, reconnect, unexpected-drop cleanup, no-exec, and indirect-route evidence remains pending.
- The user-facing strict-route setting replaces organization-scale terminology. Saved profiles read the legacy boolean into `DisableDirect`, subsequent saves emit only the current field, and unknown/retired/corrupt route metadata becomes an explicit non-connectable `Unsupported` or `Corrupt` state with a stable recovery error code and Host-editor guidance.
- A product-scope check gates CI, Alpha archives, and signed-package workflows. It keeps primary product documentation vendor-neutral, allows technical format names only in migration/import documentation, rejects replacement overclaims, organization-scale positioning, placeholder labels, and superseded binary plan names, and has fixture-based self-tests.
- `CONTRIBUTING.md`, the bilingual Development Playbook, PR template, and feature/bug forms define Core → Test → UI → live-validation order, vertical slices, lifecycle and secret rules, SFTP integrity, Multi safety, and Definition of Done.
- SSH.NET is upgraded to the security-fixed 2026.0.0 release and lock files are refreshed. The official Windows Agent adapter source is pinned in-tree, compiled directly against SSH.NET 2026, and can be made a required self-test gate; a live Agent service/key and live server matrix are still required.
- A credential-free atomic transfer queue survives process restart, converts abandoned running work to interrupted state, preserves completed targets, and exposes explicit restore/resume actions in Files and Multi.
- A credentialed live-server harness now covers smoke, disconnect/resume fault injection, configurable 100 GB/100,000-file scale, and 16-session soak modes. These modes are release gates and have not been run without an approved server.
- A manual signed-MSIX workflow validates the production PFX, signs and verifies separate x64 and ARM64 outputs, and emits architecture-specific App Installer descriptors that support controlled update and rollback. A production certificate and deployment endpoint are still required.

## Remaining release gates

1. Run and record the full shell/TUI/Unicode/input/security/latency/soak matrix for the new package-local renderer; integration alone is not GA evidence.
2. Run the live Windows Agent, repeated OTP/MFA, PPK v2/v3, SSH jump, ProxyCommand, and HTTP/SOCKS compatibility matrix; add proxy-DNS verification and a full reconnect policy, and record negotiated-information fingerprint/reconnect/unexpected-drop/no-exec/indirect-route acceptance.
3. Run live-server evidence for permission changes, pause/resume, recursive delete, all collision policies, large/deep paths, and 16-target Multi transfer; restart queue discovery and deterministic N→1 collision isolation now have an Alpha baseline.
4. Add a post-connect tunnel manager and local/remote/dynamic lifecycle and failure integration tests. Non-loopback forwarding already requires an explicit high-risk confirmation and emits a warning diagnostic.
5. Finish streaming command output, typed named parameters, durable Multi details/export, timeouts, and redacted local activity records.
6. Complete credential-free sharing/import preview, support bundles, accessibility, clean-install/upgrade/rollback, performance, and soak evidence. Signed packaging/update automation exists but has not produced a production-signed acceptance artifact.

The authoritative requirement-by-requirement status remains in [Requirements Traceability](REQUIREMENTS.md). Product admission rules and explicit non-goals are fixed in [Product Scope](PRODUCT_SCOPE.md).

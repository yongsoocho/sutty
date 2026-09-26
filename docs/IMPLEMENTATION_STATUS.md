# Sutty Alpha implementation status

This document maps the current repository to Sutty's local-first Windows SSH/SFTP product scope. It is intentionally evidence-based: **Implemented** means code and focused verification exist; it does not imply a GA release.

## Reliability review — 2026-09-26

Source baseline: `90a6e04e6a66898850f0a53b16bd3d43257d963d` plus the current working-tree
changes. This is Alpha 4 development work, not an exact published or signed candidate.

- Connection headers distinguish Sutty SSH `user@host:port`, local PC shells, and external
  terminal-only programs. The selected Files browser and the all-session transfer queue have
  separate scope labels. External programs show saved-host/import guidance instead of a
  remote-looking local browser. Existing stale-response and pinned-target protection remains.
- Multi retains the 3×3 layout and 16-tab/two-page model, but commands select only connected
  Sutty SSH. Every run previews all endpoints, off-page/type counts and the exact command,
  followed by extra PROD confirmation. Canceled/invalid requests retain drafts; the approved
  set uses independent SSH exec without inheriting the visible PTY's directory/environment/sudo.
  Waits are bounded to 60 seconds, including synchronous channel startup. Cancellation requests,
  unknown outcomes, missing exit codes and truncated output remain explicit; no automatic replay.
- Remote edits compare bounded remote SHA-256 content before confirmation and queued execution,
  detecting same-size/time changes without a server shell utility. Read errors stop automatic
  upload. Retained copies, immutable upload snapshots and failed-reload preservation remain;
  recovery notes identify the original server and sensitive local files. A write after comparison
  is still possible without server-side coordination.
- Transfers separate data percentage, verification, promotion and completion. Disabled controls
  explain original-host reconnection, Edits review and Multi recovery. Late progress cannot
  replace terminal queue states. Transfer Center disposal drains an active refresh before return.
- Debug/Release default to ReadyToRun/trimming disabled, matching existing CI and package
  workflows. ROADMAP now treats sharing/editing/tunnels as implemented features awaiting
  acceptance. The [16 workflow scenarios](RELEASE_ACCEPTANCE.md#daily-workflow-review--일상-작업-점검)
  remain separate from focused automated tests and existing release gates.

기존 구조를 유지한 대상·명령 안전성·편집 복구 개선입니다. 아래 자동 검증과 실제 후보 인수는
구분하며, 실서버·설치·다른 PC·고배율·수동 입력 결과를 통과로 대신 기록하지 않습니다.

## Product boundary

- Windows 11 desktop application, local-first, with no account or cloud control plane.
- The selected local or SSH terminal anchors the workspace. Fresh startup and manually closing the last tab open PowerShell; the `+` menu offers both PowerShell and CMD. Global Home, Hosts, Transfers, and Commands open on the right in wide windows or below the terminal under 1100 logical pixels. Settings fills the workspace and covers the shell while sessions stay alive. Each SSH tab's Files, Commands, and Tunnels also move below the terminal in narrow layouts. Multi Command is a separate destination with a 3×3 grid and the shared command library.
- The current Alpha is centered on per-user local use. Small teams can exchange a reviewed local JSON file containing selected host/route/tunnel/command definitions, then bind their own keys/accounts by authentication alias. No shared credential store, account service, or synchronization backend is added.
- English and Korean first-party UI.
- Fresh production storage contains no sample hosts, commands, credentials, or connection history.

## Implemented foundation

| Area | Current evidence |
| --- | --- |
| Runtime and architecture | .NET 10; x64 and ARM64 solution platforms; UI, Core, Setting, Command, and the pinned Windows SSH Agent compatibility project have explicit responsibilities. |
| Application shell | Global and selected-session navigation use explicit view-independent state. Cached Home/Hosts/Transfers/Commands pages open on the right or below the terminal under 1100 logical pixels; full-page Settings covers the shell without closing sessions. SSH tools move below the terminal when narrow. Cards, toolbars, local/remote file panes, and settings controls reflow to available width. Fresh startup and manual last-tab closure open PowerShell. Seven fixed Alt accelerators are handled explicitly; terminal WebView input forwards application shortcuts instead of sending them to the PTY. Manual GUI acceptance remains unverified. |
| Shell tab lifetime | Actual SSH PTY/transport or local/external process termination closes only its owning tab by default. Settings can retain ended tabs without restarting them. Initial failures remain visible; pending transfers/edits retain recovery confirmation. Automatic last-tab closure returns to Home. |
| Real sessions | Production session creation uses the SSH.NET-backed session only. SSH, Terminal, and SFTP states are independent. Unexpected primary-transport errors retire only the current client generation, clear live handshake data, run best-effort owned-resource cleanup, and publish `Failed` without racing explicit disconnect. Retained failed or disconnected tabs offer an explicit manual reconnect that creates a fresh shell. Saved Hosts are reloaded through the current profile and optional vault; one-off sessions return a credential-free draft to Quick Connect. Commands, terminal input, transient secrets, transport objects, and trust-once decisions are never replayed. |
| SSH authentication | Password, private keys including PPK v2/v3, Windows SSH Agent, and repeated multi-prompt keyboard-interactive OTP/MFA flows are wired through SSH.NET and the secure UI prompt. |
| Host identity | Unknown keys fail closed, one-time and persisted trust are explicit, and changed keys are blocked. |
| Connection information | The primary SSH handshake is captured as an in-memory, credential-free snapshot and exposed in an accessible read-only/copy flyout: server/client identification, KEX, verified host-key algorithm and SHA-256 fingerprint, plus both cipher/MAC/compression directions. Connection alone issues no banner or home-directory discovery command. |
| Interactive terminal | Persistent SSH PTY and local PowerShell/CMD ConPTY use package-local xterm.js 6.0.0 in a hardened WebView2. Both local shells are available from `+` / `Ctrl+T`. ANSI/VT color/style, alternate screen, mouse/input modes, IME/Unicode cells, search, clipboard shortcuts, measured server-side resize, and bounded acknowledged output delivery are integrated. Manual CMD input/resize acceptance remains unverified. |
| Latest output copy | One clipboard icon beside the CONPTY/PTY dimensions requests the latest rendered command output, including errors. Runtime PowerShell/CMD prompt markers do not modify profile files; custom or unmarked SSH shell boundaries use best-effort detection. The result reflects PTY expansion/processing of tabs, control sequences, and newlines, not a byte-for-byte stdout/stderr capture. Manual clipboard acceptance remains unverified. |
| Theme catalog | [36 shared app/terminal palettes](THEMES.md) include VS Code defaults, Dracula, Monokai, GitHub, Nord, Tokyo Night, Catppuccin, and more. Gradient accents remain; button text selects black/white for contrast. Named app-follow mode applies ANSI colors even across dark-to-dark switches. Existing saved ids remain compatible; palette brushes, status colors, tab borders, and tab buttons refresh with theme changes. |
| Local connection launcher | A separate Home card replaces its recent-host card and accepts validated one-line direct connection commands such as `ssh worker1` and `multipass connect master` and opens their installed executable in a new local ConPTY terminal without a shell. OpenSSH therefore reads the user's existing `.ssh/config`, `IdentityFile`, SSH Agent, and `PATH` configuration. Saves join Hosts favorites (10,000 shared profiles), with one-time transactional migration from the archived old library. Empty/filled host stars add/remove favorites. External profiles are excluded from sharing and automatic restore; explicit clicks or --host rebuild their launch plan. Launch history (250 maximum) remains local-only; they retain canonical commands and finite outcomes, never resolved executable paths, terminal output, passwords, tokens, or passphrases. Actual SSH Agent/key/config, Multipass, and UI acceptance remain unverified. |
| Structured commands | Standard output, standard error, exit status/signal, timing, and cancellation are preserved for the user-visible Commands workspace and Multi execution. The existing persisted `Repl` setting value remains compatible. |
| SFTP baseline | Session-bound dual-pane Files supports absolute paths, back/forward, parent navigation, refresh, hidden toggles, folder-first name/size/modified sorting, multi-selection, and locally persisted host-specific remote favorites. Pane-to-pane file/folder drops, Explorer uploads, and transfer buttons use the same durable collision/staging/checkpoint/verification engine; all drops copy and pin the destination before dialogs. Existing remote search, rename, safe move/delete, permissions, and folder creation remain. The live Transfer Center enables controls only for an exact eligible executor; cross-process queue locks and execution leases remain intact. |
| Remote editing | Regular text files up to 8 MiB open in a configured external executable. Save detection defaults to manual upload; auto-upload is opt-in per file and stops on failure or conflict. Host/path and upload snapshots are pinned, size/time or remote SHA-256 content changes trigger review, and all transfers use the existing durable safe engine. Failed edits require review rather than generic queue retry. Reload commits a fresh working copy only after verification; existing copies survive failed reload, close, or upload errors in the recovery folder. Unsaved editor buffers cannot be inspected; copies may contain sensitive content and are not automatically deleted. Bounded content comparison detects same-size/time edits; writes after the preflight comparison still require server-side coordination. |
| Terminal/Files linkage | Files previews and copies a safely quoted POSIX directory command without a newline; it never injects it into a running TUI or executes it. Terminal → Files uses an explicit absolute path. Automatic shell-output parsing/current-directory tracking is not implemented. |
| Multi SFTP | Explicitly checked SFTP sessions support 1→N upload and N→1 download. A preflight dialog reviews the targets, source, destination, and conflict policy; server results are isolated, name collisions use deterministic server folders, successful targets remain complete, and retry addresses only failed/incomplete targets with the original policy. New, retry, and restored batches hold a target lease for every executing server until that batch ends; batch-wide global Pause/Cancel remains unavailable. |
| Saved Hosts | Explicit SQLite profiles support create/update, credential-free duplicate, delete, search, tags, groups, environments, favorites, and authentication aliases. Existing format imports now preview per-item additions, changes, duplicates, and errors with Add/Skip/Copy/Update choices; local credentials are not copied to a different endpoint. |
| Definition sharing | The Hosts/History entry point is removed. Retained sharing/preview code supports selected hosts, routes, tunnels, groups/tags, and commands can be previewed and exported as schemaVersion 1 JSON, then selectively imported (4 MiB / 1,000 definitions). Credentials, vault IDs, private-key paths, trust, and histories are omitted; imported external ProxyCommand routes are blocked. Each PC supplies its own authentication. Server identifiers and tokens embedded in user-authored command text still need manual review. |
| Saved Host launcher | `sutty.UI.exe --host <id or exact name>` resolves an existing Saved Host and enters the same secure connection flow. It rejects credential arguments and does not replace the normal window's Workspace snapshot. |
| Credential vault | Opt-in AES-256-GCM records use a random master key protected for the current Windows user. Plaintext secrets are excluded from settings, SQLite, history, and crash messages. |
| Connection history | Every completed attempt appends success, failure, or cancellation, bounded diagnostic code, and duration. Duplicate attempts remain separate rows. Retention and frequent-host count are settings. |
| Desktop state | Theme, language, terminal mode, terminal palette/cursor/scrollback/accessibility/profile-loading options, window sizes, and right-panel width persist locally. A separate atomic workspace snapshot remembers local tabs' PowerShell/CMD choice and Saved Host ids, asks before SSH reconnection by default, and never replays commands. Missing or unknown local shell values restore PowerShell, and Saved Host entries retain no local shell. Resize and workspace writes are debounced. |
| Route foundation | Direct, HTTP CONNECT, SOCKS4, SOCKS5, SSH jump, and ProxyCommand routes are resolved before client creation and shared by SSH/SFTP. Saved hosts restore credential-free route/tunnel definitions while route secrets remain in the Windows-user vault. Strict route policy rejects Direct without fallback; one credential-free correlation context is created per session. |
| Connection Doctor | Failures lead with a recovery action. Nine ordered stages, stable locale-independent error codes, safe copy summaries, and bounded technical details remain available in a collapsed diagnostic section and Settings > Troubleshooting. Connection history retains the actual Password, PublicKey, Agent, or KeyboardInteractive type and stable failure code. |
| Known Host management | The v2 fail-closed store safely loads v1, tracks first-trusted/last-used times, and exposes inspect, compare-and-swap removal, deliberate old/new-key rotation with a required verification checkbox and reason, plus bounded local trust activity in Settings. |
| Redacted support bundle | Settings lists each open SSH connection plus a credential- and endpoint-free retained failure, previews the selected session/status/error/correlation/event count locally, and creates a ZIP with an exact two-file allowlist (`manifest.json`, `report.json`). The preview binds creation to the exact diagnostic-event snapshot. Captured failures are authoritative; a validated, stage-aware context code is used only when that failure event is missing, while an explicit same-stage success suppresses stale fallback and conflicting non-`NONE` codes fail closed. The deterministic atomic archive serializes no session label, endpoint, username, path, host-key fingerprint, transcript, command, output, secret, or raw log field. |
| Diagnostic log privacy | Connection logs remain intentionally detailed for local troubleshooting. A persistent warning states that hostnames, usernames, file paths, host-key fingerprints, and other infrastructure identifiers may be present, and the copy action is explicitly labeled as raw-log copy. |
| Forwarding | The connected session's Tunnels page exposes local/remote/dynamic definitions, bind/destination, listener state, start/stop, and actionable errors. Runtime additions start stopped and default to loopback; external bind starts require confirmation. Session shutdown owns listener cleanup. Runtime additions are session-only; automatic reconnect/restoration is not added. |
| Terminal productivity | REPL JSON/YAML and severity highlighting, bounded command suggestions, Right/Tab acceptance, tab/navigation/settings shortcuts, and Insert-style copy/paste are implemented. |
| Support and evidence governance | The exact Windows/architecture, server/authentication/key, route/forwarding, terminal, and SFTP claim boundaries use four support states in [Supported environments](SUPPORTED_ENVIRONMENTS.md). The strict [live-evidence schema](evidence/EVIDENCE_SCHEMA.md) separates run results from support promotion. Writers are always unreviewed; a write-once post-run review records source hashes and a declared public reviewer identifier, the evidence root uses an exact allowlist, Git history rejects mutation of existing bundles, and [release governance](RELEASE_GOVERNANCE.md) defines exact main/tag rulesets plus a fifth acceptance-provenance asset. The repository still contains no fabricated live `Pass` record. |

## Verification added for this milestone

- Credential round-trip, reload, no-plaintext-at-rest, and authenticated-tamper rejection.
- Empty local database, legacy saved-host migration, profile create/update/favorite/delete, append-only duplicate history, outcome records, and frequent-host aggregation.
- Legacy settings compatibility, value normalization, atomic persistence, panel-width persistence, corrupt-file fallback, and credential-free 16-tab workspace snapshot normalization/clearing. Local shell tests cover PowerShell/CMD round trips, older or unknown values defaulting to PowerShell, and shell-field clearing on Saved Host entries.
- Pure shell-state checks cover global navigation state, full-page Settings without session disposal, fixed Alt+1–5 mappings, Terminal/Files/Commands/Tunnels transitions, legacy `Repl` compatibility, connection-persistence decisions, invalid route rejection, and active-workspace cleanup. The UI build keeps all seven Alt accelerators registered and handled, while terminal renderer checks require the WebView shortcut bridge to suppress default browser/PTY handling without intercepting Ctrl/Shift or AltGr combinations. These state checks do not replace manual acceptance of the terminal and responsive panes.
- Theme checks cover all 36 palette definitions, unique names/ids, valid 16-color ANSI tables, actual settings save/load normalization, old terminal ids, app-follow dark-to-dark changes, and independent terminal overrides.
- Existing terminal parser/input and safe SFTP path checks remain part of the solution; transfer checks now cover recursive/empty-directory copy, bounded filename search, cross-directory file/folder move and self-descendant rejection, resume offsets, checksums, conflict-policy behavior and durable policy persistence, safe recursive-delete previews, non-secret checkpoint persistence, per-target failure isolation, and failed-only retry.
- Local Files checks cover name/size/date ordering, hidden-file projection, back/forward branching, failed navigation, canceled/superseded completion, and shutdown. Favorite-folder checks cover host isolation, Unicode, duplicate suppression, removal, and preservation of corrupt or future-schema files. Internal and Explorer drops share the transfer path; manual drag-and-drop acceptance remains unverified.
- Focused remote-edit checks cover immutable upload snapshots, metadata conflicts, text/size restrictions, cancellation, retained working copies, and safe editor arguments. Host-sharing checks cover credential exclusion, schema/size limits, per-row import decisions, and local authentication preservation. Tunnel lifecycle and directory-command quoting have credential-free tests. These checks do not constitute a live editor/server/forwarding matrix.
- Focused one-line launcher checks cover direct executable planning, Windows argument quoting, rejection of shell/interpreter forms and SSH remote commands, safe favorite/history retention, bounded pruning, and credential-like storage rejection. They do not verify a real SSH Agent/key/config profile, Multipass instance, or UI interaction.
- Transfer Center checks cover fail-closed executor registration, state-specific command routing, failed-only retry, serialized duplicate controls, event-coalesced snapshots, external queue-file refresh, concurrent queue writers, and operating-system target leases across Sutty processes. Multi target leases are compiled through the x64 UI path; live batch control remains outside the claim.
- Package-local renderer checks cover restrictive CSP, absence of remote asset URLs and `innerHTML`, input/output/resize bridge primitives, and the reviewed xterm.js SHA-256.
- Route-policy rejection, credential-free diagnostic correlation context, structured-text classification, danger/warning classification, and command-suggestion ordering have focused self-tests.
- SSH.NET negotiated-property and transport-error event availability, the immutable connection-information field allowlist, normalization, and absence on new/failed connections have focused automated checks. The manual-reconnect policy has focused checks for eligible states, deep-copy isolation, and omission of secrets and prompt delegates; static source review confirms removal of automatic remote commands and replay. Live fingerprint, reconnect, unexpected-drop cleanup, no-exec, and indirect-route evidence remains pending.
- The user-facing strict-route setting replaces organization-scale terminology. Saved profiles read the legacy boolean into `DisableDirect`, subsequent saves emit only the current field, and unknown/retired/corrupt route metadata becomes an explicit non-connectable `Unsupported` or `Corrupt` state with a stable recovery error code and Host-editor guidance.
- A product-scope check gates CI, Alpha archives, and signed-package workflows. It keeps primary product documentation vendor-neutral, allows technical format names only in migration/import documentation, rejects replacement overclaims, organization-scale positioning, placeholder labels, and superseded binary plan names, and has fixture-based self-tests.
- `CONTRIBUTING.md`, the bilingual Development Playbook, PR template, and feature/bug forms define Core → Test → UI → live-validation order, vertical slices, lifecycle and secret rules, SFTP integrity, Multi safety, and Definition of Done. A fixture-tested PR-body contract rejects missing, duplicated, reordered, comment-only, empty, or unexplained placeholder sections, every unknown requirement ID (including IDs mixed with valid ones), and an entirely unchecked Definition of Done. Its trusted-base `pull_request_target` workflow executes no PR-head code and is required in both tracked and live `main` rulesets; the earlier head-controlled duplicate has been removed.
- SSH.NET is upgraded to the security-fixed 2026.0.0 release and lock files are refreshed. The official Windows Agent adapter source is pinned in-tree, compiled directly against SSH.NET 2026, and can be made a required self-test gate; a live Agent service/key and live server matrix are still required.
- A credential-free atomic transfer queue survives process restart, converts abandoned running work to interrupted state, preserves completed targets, and exposes explicit restore/resume actions in Files and Multi. Focused tests also cover competing store instances and queue writers, a 32-contender single-winner claim, stale/idempotent lease disposal, completed-target rejection, eligible cancelled-target retry, and cross-process target exclusion.
- A credentialed live-server harness now covers isolated connection-information/reconnect checks, smoke, disconnect/resume fault injection, configurable 100 GB/100,000-file scale, and 16-session soak modes. These modes have not been run without an approved server, and their candidate writer cannot promote a successful automated subset beyond `Blocked` until the full gate coverage is recorded.
- A manual signed-MSIX workflow validates the production PFX, signs and verifies separate x64 and ARM64 outputs, and emits architecture-specific App Installer descriptors that support controlled update and rollback. A production certificate and deployment endpoint are still required.

## Usability follow-up — 2026-09-27

Saved/favorite and recent-host cards have an upper-right X with contextual confirmation. Recent deletion removes one connection_log id and preserves all saved profiles and other attempts. The Hosts/History sharing shortcut was removed; underlying sharing code and Settings host imports remain. Successful one-line Open clears its submitted draft without clearing newer input. Files names its real SFTP endpoint and explains using a destination connection through SSH Jump for another server.

Multi uses explicit three-column placement rather than adaptive maximum-column wrapping. Previous/next/page controls are hidden but internal paging remains. The current screen exposes the first nine tabs; bulk and individual selection target visible connected Sutty SSH only, with explicit excluded counts. Hidden selections cannot enter a broadcast and Clear all removes stale selections. These changes do not extend raw terminal broadcasting or infer a nested SSH target.

Validation: x64 Debug/Release and ARM64 Debug UI builds passed with zero warnings/errors in
isolated `artifacts/usability-*` outputs. Command and Setting self-tests passed, including exact
history deletion isolation, mixed 16-tab selection, hidden/stale/disconnected targets, reorder
retention, and fractional/unbounded viewport geometry. Product-scope, live-evidence/history,
XML and whitespace checks passed. Read-only review found no remaining concrete defect. These
checks do not establish actual WinUI rendering/click timing or live SSH Jump acceptance; no new
manual UI/server/install validation or live evidence was performed.

## Local verification — 2026-09-27

The Home/favorites/disconnect revision passed x64 Debug, x64 Release, and ARM64 Debug UI builds
with zero warnings/errors (`--no-restore`, `WindowsPackageType=None`, isolated
`artifacts/home-disconnect-*` outputs). Command, Setting, SFTP, Terminal, Core.Security, and
credential-free live-evidence self-tests passed. Native terminal tests exercised PowerShell/CMD
I/O, exit and cleanup. New checks cover unified favorite migration, unavailable executables,
ID collisions, deduplication, secret filtering, default/legacy/disabled auto-close settings,
SSH PTY logout with live transport, initial failures, independent tab ownership, duplicate
notifications, and retaining ended shells without automatic restart.

All nine PowerShell fixture suites and product-scope/evidence/history guards passed. Node output
checks passed 16 tests; two optional native replay fixtures were skipped because the capture
directory was not configured. Code review corrected favorite-ID collision and storage-error
boundaries; automatic tab closes queue around recovery prompts. No new live evidence manifest,
signed package, or release was produced. GUI layout/input, actual SSH logout/server disconnect,
editor/drop, installation and the sixteen workflow acceptance scenarios remain unverified.

Home 한 줄 연결 분리·호스트 즐겨찾기 통합·기본 자동 탭 닫기를 구현하고 세 구성 빌드와
관련 자동 검사를 통과했습니다. 종료 탭을 남겨도 다시 선택만으로 셸을 재실행하지 않습니다.
화면 조작·실서버·설치 인수는 수행하지 않았으며 기존 미검증 항목을 통과로 바꾸지 않았습니다.

## Local verification — 2026-09-26

Environment: Windows `10.0.26200.0`, .NET SDK `10.0.400`; baseline `90a6e04e6a66` plus
this working tree. Locked x64 Release and ARM64 Debug restore passed with the aligned default
ReadyToRun/trimming settings. Final x64 Debug, x64 Release, and ARM64 Debug compilation use
separate `artifacts/review-v2-*` directories to avoid touching the running trial app.

Command, Setting, SFTP, Terminal, and Core.Security self-tests passed. New regressions cover
draft revision retention, all-page selection, ignored cancellation, synchronous exec startup,
blocking transport-cancellation callbacks, unknown exit status, same-size/time remote edits,
bounded reads, failed reload preservation, verification/promotion order, and late progress after
success/failure. SFTP testing exposed a refresh-timer disposal race; draining the active refresh
fixed the locked-file cleanup and the complete SFTP suite passed on rerun. Sixteen JavaScript
output-copy tests and the credential-free live-evidence writer self-test passed. All nine
PowerShell policy/fixture suites, product-scope, evidence/history and whitespace checks passed.
No live result manifest was added.

The first isolated Debug launch exited with `XamlParseException` (`0x802B000A`) before a
targetable window. A standard Release launch remained running, but **Windows Computer Use was
stopped by the user's physical Escape input before screenshot/input acceptance**. No further
UI automation was performed; the final rebuilt outputs were not relaunched. These results are
compilation and automated-test evidence, not a resolved startup/visual acceptance claim. One
intermediate build also hit files locked by that trial app; final builds use separate directories.

Live-server environment variables were not configured. SSH interoperability, real editor/drop
behavior, 100/150/200% DPI and IME, cross-PC sharing, scale/soak, and install/update/rollback remain
unverified. No ZIP/MSIX was signed, promoted or published. The sixteen workflow acceptance
scenarios remain Blocked pending the exact candidate and required environments.

명령·설정·SFTP·터미널·보안과 출력 복사·정책 자동 검사를 통과했습니다. 초기 Debug 시작에서
XAML 오류가 기록됐으며 Release 실행 후 사용자의 Escape로 화면 자동화를 중단했습니다.
최종 산출물의 시작·화면·입력 인수는 미검증입니다. 실서버 정보가 없어 서버 검증도 수행하지
않았고 서명·설치·공개 배포 완료로 표시하지 않습니다.

## Local verification — 2026-09-22

The earlier persistent-shell and CMD checkpoint passed x64 Debug, ARM64 Debug, and x64 Release compilation with zero warnings and errors. Release used ReadyToRun and trimming disabled; the standard optimized Release restore remains blocked by the existing lock-file dependency mismatch. Windows ConPTY self-tests exercised PowerShell and CMD command execution, Korean output, resizing, broadcast markers, exit/reopen, and output-heavy shutdown. Settings tests covered shell selection round trips and legacy/unknown-value migration. Product-scope and whitespace checks passed at that checkpoint.

The subsequent full-page Settings, responsive-pane, clipboard, and theme revision passed final isolated x64 Debug, x64 Release, and ARM64 Debug UI compilation with zero warnings and errors (`WindowsPackageType=None`; Release disables ReadyToRun and trimming). The x64 Debug executable is under `artifacts/ui-final`. Settings tests passed, including 36 named-palette persistence round trips, legacy terminal ids, and named dark-to-dark ANSI changes. The final terminal self-test passed. All 18 JavaScript output-copy tests passed, including replayed native PowerShell/CMD traffic, stdout/stderr ordering, blank output, whitespace, chunked Unicode, large output, and nested SSH. Product-scope and whitespace checks passed. These results do not establish packaged or visual acceptance. **Windows UI automation was stopped by the user's Escape input on 2026-09-22 before a screenshot of the rebuilt window**; layout, theme appearance, and manual clipboard acceptance remain unverified. Live SSH custom-prompt acceptance also remains pending.

## Local verification — 2026-09-06

The daily-workflow changes compiled with zero warnings and errors in Debug and Release for x64 and ARM64. Release used the existing Alpha/CI settings with ReadyToRun and trimming disabled. The five self-test executables (Command, Setting, SFTP, Terminal, and Core.Security) passed, including the added editing, sharing, navigation, and tunnel checks. The credential-free live-evidence self-test and nine PowerShell policy/fixture suites passed. Product-scope, evidence/history, whitespace, and the final x64 publish artifact checks passed; the evidence directory still contains no committed live result manifests.

The local x64 self-contained archive is an unsigned development build of the working tree, not a production-signed release or an exact clean-tag acceptance artifact. Real-server checks could not start because the Docker Linux engine was unavailable. Windows UI automation was stopped by the user's Escape input, so visual, drag-and-drop, and external-editor acceptance remain unverified. These results do not promote any live compatibility claim.

## Remaining release gates

1. Run and record the full shell/TUI/Unicode/input/security/latency/soak matrix for the new package-local renderer; integration alone is not GA evidence.
2. Run the live Windows Agent, repeated OTP/MFA, PPK v2/v3, SSH jump, ProxyCommand, and HTTP/SOCKS compatibility checks for environments claimed as supported; verify proxy DNS and negotiated-information fingerprint/manual-reconnect/unexpected-drop/no-exec behavior.
3. Run live-server evidence for permission changes, pause/resume, recursive delete, collision policies, large/deep paths, and Multi transfer. Manually exercise pane drops and external-editor save/conflict/failure/reload/close workflows; restart recovery and destination preservation still need live acceptance.
4. Validate the implemented post-connect tunnel manager against real local/remote/dynamic listeners, port conflicts, server policy, and session shutdown. Non-loopback starts require explicit confirmation.
5. Validate JSON sharing/import and each PC's credential binding, support-bundle workflows, accessibility, clean-install/upgrade/rollback, performance, and soak. Signed packaging/update automation exists but has not produced a production-signed acceptance artifact.

For the personal/small-team release, representative transfer acceptance starts with a 1 GiB file and 1,000 small files, plus ten concurrent SSH sessions for one hour. These are test scenarios, not product limits. The existing larger scale harness is available when a use case requires it. Automatic reconnect, automatic SFTP/tunnel restoration, command streaming, named parameters, complex docking, directory comparison, and automatic synchronization are follow-up work and do not block the daily SSH/SFTP workflow on their own.

개인·소규모 팀 출시의 대표 검증은 1 GiB 파일, 작은 파일 1,000개, SSH 세션 10개를 1시간 함께 사용하는 흐름부터 확인합니다. 제품 한도를 뜻하지 않습니다. 자동 재연결·고급 Commands·복잡한 도킹·디렉터리 비교·자동 동기화는 후속 개선이며, 일상 SSH/SFTP 기능의 출시를 단독으로 막는 항목이 아닙니다.

Usage and short reproduction steps are in [Daily workflow](DAILY_WORKFLOW.md). The authoritative requirement-by-requirement status remains in [Requirements Traceability](REQUIREMENTS.md). Product admission rules and explicit non-goals are fixed in [Product Scope](PRODUCT_SCOPE.md). Alpha 4 slice order and exit criteria are fixed in the [Alpha 4 execution plan](ALPHA4_EXECUTION_PLAN.md); no live gate is complete until its reviewed bundle satisfies the [evidence schema](evidence/EVIDENCE_SCHEMA.md).

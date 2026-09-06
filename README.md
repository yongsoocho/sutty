<p align="center">
  <img src="src/sutty.UI/Assets/sutty.targetsize-256.png" alt="Sutty logo" width="112" />
</p>

<h1 align="center">Sutty</h1>

<p align="center"><strong>A local-first SSH/SFTP operations workspace for Windows.</strong></p>

<p align="center">
  <a href="#english">English</a> · <a href="#한국어">한국어</a>
</p>

> **Alpha, not GA.** Sutty is an active engineering build. Review the limitations before using important systems.

> **알파, GA 아님.** Sutty는 개발 중인 엔지니어링 빌드입니다. 중요한 시스템에 사용하기 전에 현재 한계를 확인해야 합니다.

> **Latest published / 최신 공개본:** [`v0.1.0-alpha.3`](https://github.com/yongsoocho/sutty/releases/tag/v0.1.0-alpha.3) · **Current candidate / 현재 후보:** [`v0.1.0-alpha.4`](docs/releases/v0.1.0-alpha.4.md) · [Download / 다운로드](https://github.com/yongsoocho/sutty/releases) · [Install / 설치](docs/ALPHA_INSTALL.md)

## English

Sutty is a **Windows local-first SSH/SFTP operations workspace for individuals and small teams**. It brings everyday local terminals, SSH, SFTP, reusable commands, and multi-session operations into one workspace. That is a product goal, not a statement that every planned capability is complete.

The product is local-first and Windows-only. Its global navigation has five destinations:

- **Home** — Quick Connect and the first-run starting point.
- **Hosts** — Saved Hosts, favorites, and recent connections.
- **Transfers** — the cross-session transfer view.
- **Commands** — reusable commands and deliberate multi-host operations.
- **Settings** — connection, terminal, security, support, and application preferences.

Each selected SSH session keeps **Terminal**, **Files**, **Commands**, and **Tunnels** in one exact
host context. **Commands** is the visible product label for structured, non-interactive command
cells; the internal persisted value `Repl` remains unchanged for existing settings and workspace
compatibility. Local PowerShell remains available as an explicit choice from the new-tab menu.

Small teams can exchange selected host, group, tag, route, tunnel, and command definitions in one local JSON file. Export shows the exact content; import previews additions, changes, duplicates, and unsupported items before applying each choice. Each PC binds its own keys/accounts through an authentication alias. No accounts, shared credentials, RBAC, central administration, or live collaboration are added. See [Daily workflow](docs/DAILY_WORKFLOW.md).

### Implemented Alpha baseline

- Password, OpenSSH/PEM/PKCS#8/PPK v2-v3 private-key, Windows SSH Agent, and OTP/MFA keyboard-interactive authentication. Keyboard-interactive challenges can contain multiple prompts and can repeat during one connection; secrets remain transient unless the encrypted vault is explicitly enabled.
- The top `+` button and `Ctrl+T` open a menu for **New SSH connection**, **Open saved host**, **Local PowerShell**, and **Import hosts**; New SSH connection is emphasized by default. Local PowerShell uses a real ConPTY process with runtime resize and process-tree cleanup.
- A real persistent PTY channel rendered by package-local xterm.js in a hardened WebView2, with ANSI/VT color and style, mouse/input modes, IME/CJK/emoji handling, alternate screen, search, clipboard shortcuts, bounded output backpressure, and runtime server-side resize.
- Fail-closed SSH host-key verification. Unknown keys offer **Connect once**, **Trust and save**, or **Cancel**; changed saved keys are blocked.
- Read-only SSH connection information for the primary transport shows server/client identification, KEX, verified host-key algorithm and SHA-256 fingerprint, and both cipher/MAC/compression directions. Merely connecting no longer runs automatic banner or home-directory discovery commands.
- Independent SSH, Terminal, and SFTP states, so an unavailable SFTP subsystem does not close a working SSH session.
- Commands and Multi execution backed by structured standard output, standard error, exit status/signal, and duration; reusable positional command templates remain available.
- Dual-pane Files supports absolute paths, back/forward, parent, refresh, hidden-file toggles, name/size/modified sorting, multi-selection, and host-specific remote favorite folders. Pane-to-pane file/folder drag-and-drop, transfer buttons, and Windows Explorer → Remote drop all use the same durable collision/staging/checkpoint/verification flow. Drops copy and retain the source; the destination is pinned before dialogs. Remote search, rename, move without overwrite, safe recursive deletion, permissions, and folder creation remain available.
- Remote text editing uses a configured external `.exe` (Notepad by default) for regular files up to 8 MiB. Saves are detected and **Upload changes** is the default; automatic upload must be enabled for that file. Size/time conflict checks, explicit Save as/reload, immutable upload snapshots, and the existing safe transfer queue protect the workflow. Copies remain in the local recovery folder, including after errors or closing; failed reloads retain the previous copy. Metadata comparison cannot prevent every concurrent edit. Failed edit jobs require review in **Edits**, rather than generic queue retry.
- **Open in terminal** previews a safely quoted POSIX `cd` command and copies it without a newline; users paste and execute at a shell prompt themselves. Terminal → Files accepts an explicit absolute path. No output parsing or automatic shell command is used for path synchronization.
- A live global Transfer Center projects the durable queue without manual refresh and provides Pause, Resume, failed-target Retry, Cancel, completed-record removal, and state/direction/target filtering. Commands are enabled only when the exact connected Files executor can accept them; Multi batches remain visible but do not claim global Pause/Cancel. Queue mutations and target execution leases are serialized across Sutty processes.
- A compact per-panel transfer queue with queued/running state, an explicit `0%`–`100%` value, progress bar, speed, ETA, cancellation, and an eight-job cap. Transfers support resumable deterministic partial files, persisted non-secret checkpoints, configurable transient-failure retries, and user-selectable final-size or SHA-256 verification (safe SHA-256 by default).
- Safe file-transfer staging. Uploads use a remote temporary name and preserve an existing destination during promotion; downloads use an adjacent local temporary file. Multi supports 1→N upload and N→1 download for explicitly checked sessions, with per-server progress/results, deterministic local isolation, and failed/incomplete-target-only retry. A credential-free atomic job queue restores incomplete single and Multi transfers after restart.
- Append-only connection-attempt history plus explicit Saved Host profiles, duplication without credentials, groups, environments, favorites, and search in SQLite.
- Credential-free Saved Host launcher: `sutty.UI.exe --host <id or exact name>` opens an existing profile while rejecting password/passphrase arguments; `sutty.UI.exe --version` reports the Alpha build.
- Opt-in local credential storage using a per-user Windows-protected AES-256-GCM vault; SQLite and settings contain only opaque credential references.
- Up to 16 mixed local/SSH tabs, zero default Multi targets, and an extra confirmation for broadcasts that include PROD-tagged SSH sessions.
- Optional restart-safe Workspace restoration remembers only local tabs and opaque Saved Host ids. SSH reconnection asks first by default, and previous commands are never stored or replayed.
- Failed or disconnected SSH sessions expose an explicit Reconnect action that always creates a new shell. Saved Hosts are reloaded from the current profile and optional encrypted vault; one-off sessions return to Quick Connect with a credential-free draft. Previous commands, terminal input, transport objects, and trust-once decisions are never replayed. Automatic reconnect and automatic SFTP/tunnel recovery remain unimplemented.
- Immediately applied Korean/English settings, atomic settings persistence, dark/light themes, terminal palettes (including Ubuntu, Atom One Dark, Dracula, GitHub, and Solarized), cursor/scrollback/accessibility controls, and optional PowerShell profile loading for prompt customizers.
- Direct, HTTP CONNECT, SOCKS4, SOCKS5, SSH jump, and external ProxyCommand routes shared by SSH and SFTP. Strict route policy rejects direct routes instead of silently falling back.
- The connected session's **Tunnels** page lists local/remote/dynamic bind, destination, state, and errors, and can add a stopped rule or start/stop a tunnel. New rules default to loopback; starting an external bind requires confirmation. Runtime rules last for that session and all listeners stop with it.
- Saved Hosts restore route and tunnel definitions while route credentials remain in the encrypted vault. Legacy imports and `schemaVersion: 1` JSON sharing use per-item previews with Add/Skip/Copy/Update choices. Sharing omits credentials, private-key paths, trust, and history; external ProxyCommand content is omitted and those imported routes are blocked. Hostnames and user-authored commands still require review for sensitive content.
- Commands JSON/YAML syntax highlighting, red critical/error and amber warning marking, and history/saved-command suggestions accepted with Right Arrow or Tab.
- Keyboard-first navigation: `Alt+1` Home, `Alt+2` Hosts, `Alt+3` Transfers, `Alt+4` Commands, `Alt+5` Settings, `Alt+6` the selected session's Terminal, and `Alt+7` its Files. `Ctrl+1`–`Ctrl+9` switch tabs, `Ctrl+T` opens the `+` menu, `Ctrl+,` opens Settings, and Insert-style copy/paste remains available. Alt navigation is registered and consumed before system-menu handling; silent operation for the exact packaged candidate remains a manual release check. See [Keyboard shortcuts](docs/KEYBOARD_SHORTCUTS.md).

### Why this is not GA

- The package-local xterm.js/WebView2 renderer is integrated for both SSH and local ConPTY, but the required shell/TUI/Unicode/input/security/latency/soak acceptance matrix is not complete. Terminal compatibility therefore remains **Alpha, not GA**. See [ADR 0001](docs/adr/0001-terminal-renderer.md).
- Windows Agent, repeated OTP/multi-prompt keyboard-interactive authentication, PPK v2/v3, SSH jump, and external ProxyCommand routes are integrated, but their live-server/agent/route compatibility matrix is incomplete. Negotiated connection information is exposed without issuing a remote command, and manual reconnect is implemented without replay; live fingerprint/reconnect/no-exec/indirect-route acceptance and opt-in automatic reconnect remain pending. Central route-policy distribution and command replay after a full SSH reconnect remain outside the current implementation.
- Saved Hosts support duplication and import/export previews; real cross-PC import, credential binding, and manual UI acceptance remain unverified. Broader bulk management and operating-system credential-broker integration remain planned.
- SFTP transfer/recovery has an Alpha implementation, but manual pane drag-and-drop, external-editor save/conflict/failure/close, server permissions, and large/deep-path live acceptance remain unverified. Synchronized browsing and directory comparison are not implemented.
- Commands output is completion-based rather than streamed. Multi uses structured per-host results, but its UI truncates output to a compact preview and has no persistent local activity export, timeout, or streaming workflow.
- The runtime tunnel manager has focused lifecycle tests; real local/remote/dynamic forwarding and port-failure acceptance remain unverified. A signed-MSIX/update/rollback workflow exists for x64 and ARM64, but no production certificate or accepted signed clean-install artifact has been supplied. Connection Doctor, Known Host management, and local support bundles exist; the GA compatibility/accessibility matrices remain incomplete.

The detailed current-state mapping is in [Requirements Traceability](docs/REQUIREMENTS.md), with the latest milestone summary in [Alpha implementation status](docs/IMPLEMENTATION_STATUS.md). Exact compatibility-claim boundaries are in [Supported environments](docs/SUPPORTED_ENVIRONMENTS.md), and live evidence must follow the [evidence schema](docs/evidence/EVIDENCE_SCHEMA.md). Live-server, scale, soak, and signed-package gates are in [Release acceptance](docs/RELEASE_ACCEPTANCE.md), with Alpha 4 ordering and exit criteria in the [Alpha 4 execution plan](docs/ALPHA4_EXECUTION_PLAN.md) and protected publication controls in [Release governance](docs/RELEASE_GOVERNANCE.md). Product admission rules and explicit non-goals are fixed in [Product Scope](docs/PRODUCT_SCOPE.md), with longer-term delivery order in the [Roadmap](docs/ROADMAP.md), engineering rules in [Contributing](CONTRIBUTING.md) and the [Development Playbook](docs/DEVELOPMENT_PLAYBOOK.md), and supporting rationale in [Product Direction](docs/PRODUCT_DIRECTION.md).

### Explicitly unsupported scope

Sutty does not support FTP, FTPS, Telnet, Serial, RDP, VNC, X11 forwarding, cloud accounts or sync, Team Vault/RBAC/SSO, terminal collaboration, mobile, macOS, or Linux applications. These are not hidden Alpha features. Small-team sharing is credential-free and file-based; accounts, shared credentials, central administration, and live collaboration are outside the local-first product boundary.

### Trust, credentials, and local data

By default, Sutty does **not** persist passwords or private-key passphrases. If the user explicitly enables **Remember credentials** for a Saved Host, Sutty stores only that secret in an AES-256-GCM vault whose random master key is protected by Windows DPAPI for the current user. Secrets are never written to SQLite or `settings.json`.

Local files under `%LOCALAPPDATA%\sutty` include:

- `settings.json` — preferences, recent tags, and recent private-key **paths**.
- `workspace.json` — up to 16 local-tab markers and opaque Saved Host ids; no credentials or terminal commands.
- `remote-path-favorites.json` — local-only favorite remote folders per host.
- `edits/` — retained working copies, upload snapshots, and recovery notes. File contents may be sensitive; close the editor and remove retained copies yourself when no longer needed.
- `sutty.db` — command templates and usage plus connection history, pins, host/user/port/auth metadata, private-key paths, and tags.
- `known-hosts.json` — public trusted SSH host keys and fingerprints.
- `sftp-transfer-checkpoints.json` — non-secret source/destination paths, sizes, timestamps, and offsets used to resume explicitly restarted transfers.
- `sftp-transfer-queue.json` — credential-free single/Multi transfer intent and per-target state used for explicit restart recovery.
- `vault.key` and `vault.json` — the DPAPI-protected master key and authenticated encrypted credential records, created only when the local vault is used.
- `crash.log` — local unhandled-exception type and HRESULT only; original exception text and secrets are not written.

Private-key file contents stay in the user-selected external file. Unknown keys are rejected before a trust prompt and retried only after an explicit decision. A changed saved key is never silently replaced.

Read [Security](SECURITY.md) before reporting or sharing diagnostic data.

### Architecture

| Project | Current responsibility |
| --- | --- |
| [`sutty.UI`](src/sutty.UI) | WinUI 3 shell, local/SSH terminal presentation, Commands, Files, Multi, and settings UI |
| [`sutty.Core`](src/sutty.Core) | Local ConPTY, SSH sessions, command results, interactive-terminal contract, host-key trust, and SFTP services |
| [`sutty.SshAgent`](src/sutty.SshAgent) | Upstream-pinned Windows OpenSSH Agent/Pageant adapter compiled against the selected SSH.NET runtime |
| [`sutty.Command`](src/sutty.Command) | SQLite command templates, connection history, pins, and non-secret drafts |
| [`sutty.Setting`](src/sutty.Setting) | Atomic JSON-backed application settings |
| [`tests`](tests) | Focused self-tests plus an opt-in credentialed live-server smoke/connection-info/fault/scale/soak harness; not a completed GA matrix |

### Prerequisites, build, and run

- Windows 11 24H2 or later
- x64 or ARM64
- .NET SDK 10.0.400, selected by `global.json`
- Windows App SDK 2.3.1 and SSH.NET 2026.0.0, pinned by the project and lock files
- Internet access for the first NuGet restore

For x64:

```powershell
git clone https://github.com/yongsoocho/sutty.git
cd sutty

dotnet restore sutty.slnx -p:Platform=x64
dotnet build sutty.slnx -c Debug -p:Platform=x64 --no-restore
& .\src\sutty.UI\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\sutty.UI.exe
```

Replace `x64` and `win-x64` with `ARM64` and `win-arm64` for ARM64. The unpackaged build carries the Windows App SDK runtime with the application.

Focused self-tests after a Debug build:

```powershell
.\tests\product-scope\Assert-ProductScope.Tests.ps1
.\tests\release-metadata\Assert-ReleaseMetadata.Tests.ps1
.\tests\release-candidate\Assert-AlphaCandidate.Tests.ps1
.\tests\live-evidence\Assert-LiveEvidence.Tests.ps1
.\tests\live-evidence-review\Review-LiveEvidence.Tests.ps1
.\tests\evidence-history\Assert-EvidenceHistory.Tests.ps1
.\tests\release-attestation\Assert-ReleaseAttestation.Tests.ps1
.\tests\repository-governance\Assert-RepositoryGovernance.Tests.ps1
.\.github\scripts\Assert-ProductScope.ps1
.\.github\scripts\Assert-LiveEvidence.ps1 -EvidenceRoot .\docs\evidence
.\.github\scripts\Assert-EvidenceHistory.ps1 -RepositoryRoot . -BaseCommit HEAD -WorkingTree
dotnet run --project tests/sutty.Core.Security.SelfTest/sutty.Core.Security.SelfTest.csproj -c Debug --no-build
dotnet run --project tests/sutty.Command.SelfTest/sutty.Command.SelfTest.csproj -c Debug --no-build
dotnet run --project tests/sutty.Terminal.SelfTest/sutty.Terminal.SelfTest.csproj -c Debug --no-build
dotnet run --project tests/sutty.Setting.SelfTest/sutty.Setting.SelfTest.csproj -c Debug --no-build
dotnet run --project tests/sutty.Sftp.SelfTest/sutty.Sftp.SelfTest.csproj -c Debug --no-build
```

See [Contributing](CONTRIBUTING.md) for the authoritative local verification sequence and restore/build prerequisites.

### License

Sutty is available under the [MIT License](LICENSE).

---

## 한국어

Sutty는 **개인 사용자와 소규모 팀을 위한 Windows local-first SSH/SFTP operations workspace**입니다. 일상적인 로컬 터미널, SSH, SFTP, 재사용 명령, 다중 세션 운영을 하나의 작업 공간에 통합합니다. 이는 제품 목표이며 계획한 모든 기능이 현재 완성됐다는 뜻은 아닙니다.

제품은 로컬 우선·Windows 전용이며 다섯 개의 전역 이동 목적지를 제공합니다.

- **Home** — Quick Connect와 첫 실행 시작점
- **Hosts** — 저장 Host·즐겨찾기·최근 연결
- **Transfers** — 세션 전체의 전송 화면
- **Commands** — 재사용 명령과 명시적 다중 Host 작업
- **Settings** — 연결·터미널·보안·지원·앱 설정

선택한 SSH 세션은 하나의 정확한 Host 문맥에서 **Terminal**, **Files**, **Commands**,
**Tunnels**를 함께 유지합니다. 구조화된 비대화형 명령 셀의 화면 이름은
**Commands**이며, 기존 설정과 작업 공간 호환성을 위해 내부 저장값 `Repl`은 변경하지
않습니다. Local PowerShell은 새 탭 메뉴에서 명시적으로 선택할 수 있습니다.

소규모 팀은 선택한 Host·그룹·태그·route·tunnel·명령 정의를 로컬 JSON 파일 하나로 공유할 수 있습니다. 내보낼 원문과 가져올 추가·변경·중복·미지원 항목을 먼저 확인하고 항목별로 적용하며, 각 PC에서 인증 별칭에 맞는 자신의 키·계정을 연결합니다. 계정, 공유 자격증명, RBAC, 중앙 관리, 실시간 협업은 추가하지 않습니다. [일상 작업 안내](docs/DAILY_WORKFLOW.md)를 참고하세요.

### 구현된 Alpha 기준선

- 비밀번호, OpenSSH/PEM/PKCS#8/PPK v2-v3 개인키, Windows SSH Agent, OTP/MFA keyboard-interactive 인증을 지원합니다. 한 연결에서 여러 질문이 반복되어도 처리하며, 암호화 Vault를 명시적으로 켜지 않으면 비밀값은 저장하지 않습니다.
- 상단 `+` 버튼과 `Ctrl+T`는 **New SSH connection**, **Open saved host**, **Local PowerShell**, **Import hosts** 메뉴를 열며 New SSH connection을 기본으로 강조합니다. Local PowerShell은 실제 ConPTY 프로세스, 실행 중 크기 변경, 프로세스 트리 정리를 사용합니다.
- 패키지 내부 xterm.js와 보안 설정한 WebView2로 표시하는 실제 지속 PTY 채널. ANSI/VT 색·스타일, 마우스·입력 모드, IME·한글·이모지, 대체 화면, 검색, 클립보드 단축키, 제한된 출력 백프레셔, 실행 중 서버 측 크기 변경을 지원합니다.
- 기본 차단 방식의 SSH 호스트키 검증. 알 수 없는 키는 **이번만 연결**, **신뢰하고 저장**, **취소**를 제공하며 저장된 키가 바뀌면 연결을 차단합니다.
- 주 SSH 전송의 서버·클라이언트 식별, KEX, 검증된 호스트 키 알고리즘과 SHA-256 지문, 양방향 cipher·MAC·압축을 보여주는 읽기 전용 연결 정보. 연결만으로 자동 banner나 홈 디렉터리 탐색 명령을 실행하지 않습니다.
- SFTP subsystem을 사용할 수 없어도 작동 중인 SSH 세션을 닫지 않는 SSH·Terminal·SFTP 독립 상태
- 표준 출력·표준 오류·종료 상태/signal·소요 시간을 구조화하는 Commands·Multi 명령 실행과 재사용 가능한 위치형 명령 템플릿
- Dual-pane Files는 절대 경로, 뒤로/앞으로, 상위 폴더, 새로 고침, 숨김 표시, 이름·크기·수정일 정렬, 다중 선택, 호스트별 원격 즐겨찾기를 제공합니다. 패널 간 파일·폴더 드래그앤드롭, 전송 버튼, Windows Explorer → Remote 드롭은 같은 영속 큐·충돌·staging·checkpoint·검증 흐름을 사용합니다. 드롭은 원본을 보존하는 복사이며 대화상자 전에 목적지를 고정합니다. 원격 검색·이름 변경·덮어쓰기 없는 이동·안전 재귀 삭제·권한 변경·폴더 생성도 유지합니다.
- 8 MiB 이하의 일반 텍스트 파일을 지정한 외부 `.exe` 편집기(기본 메모장)로 열 수 있습니다. 저장을 감지한 뒤 **서버에 반영**이 기본이며 파일별로 자동 반영을 켤 수 있습니다. 크기·수정시각 충돌 확인, 다른 이름/다시 내려받기, 고정된 업로드 사본, 기존 안전 전송 큐를 사용합니다. 오류·종료 후에도 로컬 복구 폴더에 편집본을 보관하고, 다시 내려받기 실패 시 이전 편집본을 유지합니다. 메타데이터 비교는 모든 동시 수정을 막지 못합니다. 실패한 편집 작업은 일반 큐 재시도 대신 **편집본**에서 다시 확인해야 합니다.
- **터미널에서 열기**는 안전하게 인용한 POSIX `cd` 명령을 미리 보여주고 줄바꿈 없이 복사합니다. 사용자가 셸 프롬프트에서 붙여넣고 실행합니다. Terminal → Files는 명시적인 절대 경로 입력을 사용하며 출력 파싱이나 자동 명령으로 경로를 동기화하지 않습니다.
- 수동 새로 고침 없이 영속 큐를 투영하고 일시정지·재개·실패 대상 재시도·취소·완료 기록 제거와 상태·방향·대상 필터를 제공하는 전역 Transfer Center. 정확히 일치하는 연결된 Files 실행자가 요청을 받을 수 있을 때만 명령을 활성화합니다. Multi batch는 표시하지만 전역 일시정지·취소 완료를 주장하지 않습니다. 큐 변경과 대상 실행 lease는 여러 Sutty 프로세스 사이에서도 직렬화합니다.
- 대기·실행 상태, 명시적인 `0%`–`100%` 숫자, 진행 막대, 속도, ETA, 취소, 최대 8개 작업을 제공하는 패널별 전송 큐. 결정적인 partial 파일, 비밀정보 없는 영속 체크포인트, 설정 가능한 일시 오류 재시도, 사용자가 선택하는 최종 크기 또는 SHA-256 검증(기본값은 안전한 SHA-256)으로 전송을 재개할 수 있습니다.
- 안전한 파일 전송 준비 단계. 업로드는 원격 임시 이름을 사용하고 기존 대상을 보존한 채 승격하며, 다운로드는 같은 로컬 디렉터리의 임시 파일을 사용합니다. Multi는 명시적으로 체크한 세션의 1→N 업로드와 N→1 다운로드, 서버별 진행률·결과, 결정적인 로컬 경로 분리, 실패·미완료 대상만 재시도를 지원합니다. 자격증명 없는 atomic job queue가 재실행 후 Single·Multi 미완료 전송을 복원합니다.
- SQLite 기반 append-only 접속 시도 기록과 명시적인 저장 호스트·자격증명 없는 복제·그룹·환경·즐겨찾기·검색
- 자격증명 없는 저장 Host 실행: `sutty.UI.exe --host <ID 또는 정확한 이름>`으로 기존 프로필을 열며 비밀번호·키 암호 인자는 거부하고, `sutty.UI.exe --version`으로 Alpha 버전을 확인
- Windows 사용자별 보호와 AES-256-GCM을 사용하는 선택형 로컬 자격증명 보관소. SQLite와 설정에는 불투명 참조만 저장
- 로컬/SSH 혼합 최대 16개 탭, 기본 선택 0개의 Multi 대상, PROD 태그 SSH 세션이 포함된 브로드캐스트의 추가 확인
- 선택형 Workspace 복원은 로컬 탭과 불투명 저장 Host ID만 기억합니다. SSH 재연결은 기본적으로 먼저 확인하며 이전 명령은 저장하거나 재실행하지 않습니다.
- 실패하거나 끊긴 SSH 세션은 항상 새 Shell을 만드는 명시적 재연결을 제공합니다. 저장 Host는 현재 profile과 선택형 암호화 Vault를 다시 읽고, 일회성 세션은 비밀값 없는 초안을 Quick Connect로 돌려보냅니다. 이전 명령·터미널 입력·transport 객체·이번만 신뢰 결정은 재실행하거나 재사용하지 않습니다. 자동 재연결과 SFTP·tunnel 자동 복구는 아직 구현하지 않았습니다.
- 즉시 반영되는 한국어/영어 설정, 원자적 설정 저장, 다크/라이트 테마, Ubuntu·Atom One Dark·Dracula·GitHub·Solarized 터미널 팔레트, 커서·스크롤백·접근성 설정, 프롬프트 꾸미기를 위한 선택형 PowerShell 프로필 로딩
- SSH와 SFTP가 함께 사용하는 Direct·HTTP CONNECT·SOCKS4·SOCKS5·SSH Jump·외부 ProxyCommand 연결 경로. 엄격 경로 정책에서는 Direct 경로와 조용한 우회를 차단합니다.
- 연결된 세션의 **Tunnels**에서 Local·Remote·Dynamic의 바인드·목적지·상태·오류를 보고, 중지 상태 규칙 추가와 시작/중지를 할 수 있습니다. 새 규칙의 기본 바인드는 loopback이며 외부 바인드 시작은 확인을 요구합니다. 실행 중 추가한 규칙은 이번 세션에만 적용하고 세션 종료 시 모든 수신 포트를 닫습니다.
- 저장 Host는 route·tunnel 정의를 복원하고 route 자격증명은 암호화 Vault에 둡니다. 기존 형식 가져오기와 `schemaVersion: 1` JSON 공유는 항목별 추가/건너뛰기/복제/갱신 미리보기를 사용합니다. 공유는 자격증명·개인키 경로·신뢰·기록을 제외하며 외부 ProxyCommand 내용은 빼고 해당 가져오기 경로는 차단합니다. 호스트명과 사용자가 작성한 명령의 민감정보는 공유 전에 검토해야 합니다.
- Commands JSON/YAML 문법 강조, critical/error 빨간색·warning 노란색 표시, 최근/저장 명령 제안과 오른쪽 화살표·Tab 적용
- 키보드 중심 이동: `Alt+1` Home, `Alt+2` Hosts, `Alt+3` Transfers, `Alt+4` Commands, `Alt+5` Settings, `Alt+6` 선택 세션의 Terminal, `Alt+7` 해당 세션의 Files. `Ctrl+1`–`Ctrl+9`는 탭을 전환하고 `Ctrl+T`는 `+` 메뉴를 열며, `Ctrl+,`는 Settings를 엽니다. Insert 방식 복사·붙여넣기도 유지합니다. Alt 화면 전환은 시스템 메뉴 처리 전에 등록·소비되며, 정확한 배포 후보에서 기계음이 나지 않는지는 수동 릴리스 검사로 남아 있습니다. [키보드 단축키](docs/KEYBOARD_SHORTCUTS.md)를 참고하세요.

### GA가 아닌 이유

- 패키지 내부 xterm.js/WebView2 렌더러를 SSH와 로컬 ConPTY에 연결했지만 필수 셸·TUI·Unicode·입력·보안·지연·장시간 실행 인수 매트릭스는 아직 완성되지 않았습니다. 따라서 터미널 호환성은 계속 **Alpha이며 GA가 아닙니다**. [ADR 0001](docs/adr/0001-terminal-renderer.md)을 확인하세요.
- Windows Agent, 반복 OTP·다중 prompt keyboard-interactive 인증, PPK v2/v3, SSH Jump, 외부 ProxyCommand 경로를 통합했지만 실제 서버·Agent·경로 호환성 매트릭스는 아직 미완성입니다. 원격 명령 없이 협상 연결 정보를 표시하고 명령 재실행 없는 수동 재연결을 구현했지만 실제 지문·재연결·무명령 연결·간접 경로 인수와 선택형 자동 재연결은 남아 있습니다. 중앙 경로 정책 배포와 전체 SSH 재연결 뒤 명령 재실행은 현재 구현 범위 밖입니다.
- 저장 호스트 복제와 가져오기/내보내기 미리보기를 구현했지만 실제 PC 간 가져오기·자격증명 연결·수동 UI 인수는 검증하지 않았습니다. 폭넓은 일괄 관리와 운영체제 자격증명 브로커 연동은 계획 상태입니다.
- SFTP 전송·복구의 Alpha 구현은 있지만 수동 패널 드래그앤드롭, 외부 편집기의 저장·충돌·실패·종료, 서버 권한, 대용량·깊은 경로 실환경 인수는 검증하지 않았습니다. 동기 탐색과 디렉터리 비교는 구현하지 않았습니다.
- Commands 출력은 스트리밍이 아니라 완료 후 표시됩니다. Multi는 구조화된 호스트별 결과를 사용하지만 UI 출력은 짧게 잘린 미리보기이며 영속 로컬 활동 내보내기, timeout, streaming 흐름이 없습니다.
- 실행 중 터널 관리자는 수명주기 집중 테스트가 있으며 실제 Local·Remote·Dynamic 포워딩과 포트 오류 인수는 검증하지 않았습니다. x64·ARM64 서명 MSIX·업데이트·롤백 workflow는 있지만 production 인증서와 서명된 깨끗한 PC 설치 인수 산출물은 아직 없습니다. Connection Doctor, Known Host 관리, 로컬 support bundle은 구현했으며 GA 호환성·접근성 매트릭스는 미완성입니다.

현재 상태의 상세 연결표는 [요구사항 추적표](docs/REQUIREMENTS.md), 이번 마일스톤 요약은 [Alpha 구현 상태](docs/IMPLEMENTATION_STATUS.md)에 있습니다. 정확한 호환성 주장 경계는 [지원 환경](docs/SUPPORTED_ENVIRONMENTS.md), 실환경 증거 계약은 [증거 스키마](docs/evidence/EVIDENCE_SCHEMA.md)를 따릅니다. 실서버·대용량·soak·서명 패키지 게이트는 [출시 인수 기준](docs/RELEASE_ACCEPTANCE.md), Alpha 4 순서와 종료 기준은 [Alpha 4 실행 계획](docs/ALPHA4_EXECUTION_PLAN.md), 보호된 공개 통제는 [릴리스 거버넌스](docs/RELEASE_GOVERNANCE.md)에 있습니다. 기능 채택 규칙과 명시적 비목표는 [제품 범위](docs/PRODUCT_SCOPE.md), 장기 개발 순서는 [로드맵](docs/ROADMAP.md), 개발 규칙은 [기여 가이드](CONTRIBUTING.md)와 [개발 Playbook](docs/DEVELOPMENT_PLAYBOOK.md), 설계 근거는 [제품 방향](docs/PRODUCT_DIRECTION.md)에 정리했습니다.

### 명시적 미지원 범위

Sutty는 FTP, FTPS, Telnet, Serial, RDP, VNC, X11 포워딩, 클라우드 계정·동기화, Team Vault/RBAC/SSO, 터미널 협업, 모바일, macOS, Linux 앱을 지원하지 않습니다. 숨겨진 Alpha 기능이 아닙니다. 소규모 팀 공유는 자격증명 없는 파일 기반이며, 계정·공유 자격증명·중앙 관리·실시간 협업은 로컬 우선 제품 경계 밖입니다.

### 신뢰, 자격 증명, 로컬 데이터

기본적으로 Sutty는 비밀번호와 개인키 passphrase를 **영구 저장하지 않습니다**. 사용자가 저장 호스트에서 **자격 증명 기억**을 명시적으로 켠 경우에만 해당 비밀을 AES-256-GCM 보관소에 저장하고, 임의 master key는 현재 Windows 사용자의 DPAPI로 보호합니다. 비밀은 SQLite나 `settings.json`에 기록하지 않습니다.

`%LOCALAPPDATA%\sutty` 아래의 로컬 파일은 다음과 같습니다.

- `settings.json` — 환경설정, 최근 태그, 최근 개인키 **경로**
- `workspace.json` — 최대 16개의 로컬 탭 표시와 불투명 저장 Host ID. 자격증명과 터미널 명령은 기록하지 않음
- `remote-path-favorites.json` — 호스트별 원격 즐겨찾기 폴더, 로컬에만 보관
- `edits/` — 보관된 편집본·업로드 사본·복구 메모. 민감한 내용이 남을 수 있으므로 더 이상 필요하지 않으면 편집기를 닫고 직접 정리
- `sutty.db` — 명령 템플릿·사용 정보와 접속 기록, pin, 호스트·사용자·포트·인증 메타데이터, 개인키 경로, 태그
- `known-hosts.json` — 신뢰한 공개 SSH 호스트키와 지문
- `sftp-transfer-checkpoints.json` — 사용자가 같은 전송을 다시 시작할 때 이어받기 위한 비밀정보 없는 출발·도착 경로, 크기, 시각, offset
- `sftp-transfer-queue.json` — 명시적 재시작 복원에 사용하는 자격증명 없는 Single·Multi 전송 의도와 대상별 상태
- `vault.key`, `vault.json` — 로컬 보관소 사용 시에만 생성되는 DPAPI 보호 master key와 인증 암호화된 자격 증명 기록
- `crash.log` — 로컬 미처리 예외의 type과 HRESULT만 기록합니다. 원문 예외 텍스트와 비밀정보는 쓰지 않습니다.

개인키 파일 내용은 사용자가 선택한 외부 파일에 그대로 있습니다. 알 수 없는 키는 신뢰 창보다 먼저 거부하고 명시적 결정 뒤에만 새 연결로 재시도합니다. 변경된 저장 키를 조용히 교체하지 않습니다.

진단 정보를 공유하거나 보안 문제를 신고하기 전에 [보안 문서](SECURITY.md)를 확인하세요.

### 구조

| 프로젝트 | 현재 역할 |
| --- | --- |
| [`sutty.UI`](src/sutty.UI) | WinUI 3 셸, 로컬/SSH 터미널 표시, Commands, Files, Multi, 설정 UI |
| [`sutty.Core`](src/sutty.Core) | 로컬 ConPTY, SSH 세션, 명령 결과, 대화형 터미널 계약, 호스트키 신뢰, SFTP 서비스 |
| [`sutty.SshAgent`](src/sutty.SshAgent) | 선택한 SSH.NET 런타임에 맞춰 직접 빌드하는 upstream 고정 Windows OpenSSH Agent/Pageant adapter |
| [`sutty.Command`](src/sutty.Command) | SQLite 명령 템플릿, 접속 기록, pin, 비밀정보 없는 초안 |
| [`sutty.Setting`](src/sutty.Setting) | 원자적으로 저장하는 JSON 환경설정 |
| [`tests`](tests) | 집중형 self-test와 선택 실행하는 자격증명 기반 실서버 smoke·connection-info·fault·scale·soak harness. 아직 완료된 GA 매트릭스가 아닙니다. |

### 준비 사항, 빌드, 실행

- Windows 11 24H2 이상
- x64 또는 ARM64
- `global.json`이 선택하는 .NET SDK 10.0.400
- 프로젝트와 lock file이 고정하는 Windows App SDK 2.3.1과 SSH.NET 2026.0.0
- 최초 NuGet 복원을 위한 인터넷 연결

x64 기준 명령입니다.

```powershell
git clone https://github.com/yongsoocho/sutty.git
cd sutty

dotnet restore sutty.slnx -p:Platform=x64
dotnet build sutty.slnx -c Debug -p:Platform=x64 --no-restore
& .\src\sutty.UI\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\sutty.UI.exe
```

ARM64에서는 `x64`와 `win-x64`을 `ARM64`와 `win-arm64`로 바꾸세요. Unpackaged 빌드는 Windows App SDK 런타임을 앱과 함께 제공합니다.

Debug 빌드 뒤 집중형 self-test를 실행할 수 있습니다.

```powershell
.\tests\product-scope\Assert-ProductScope.Tests.ps1
.\tests\release-metadata\Assert-ReleaseMetadata.Tests.ps1
.\tests\release-candidate\Assert-AlphaCandidate.Tests.ps1
.\tests\live-evidence\Assert-LiveEvidence.Tests.ps1
.\tests\live-evidence-review\Review-LiveEvidence.Tests.ps1
.\tests\evidence-history\Assert-EvidenceHistory.Tests.ps1
.\tests\release-attestation\Assert-ReleaseAttestation.Tests.ps1
.\tests\repository-governance\Assert-RepositoryGovernance.Tests.ps1
.\.github\scripts\Assert-ProductScope.ps1
.\.github\scripts\Assert-LiveEvidence.ps1 -EvidenceRoot .\docs\evidence
.\.github\scripts\Assert-EvidenceHistory.ps1 -RepositoryRoot . -BaseCommit HEAD -WorkingTree
dotnet run --project tests/sutty.Core.Security.SelfTest/sutty.Core.Security.SelfTest.csproj -c Debug --no-build
dotnet run --project tests/sutty.Command.SelfTest/sutty.Command.SelfTest.csproj -c Debug --no-build
dotnet run --project tests/sutty.Terminal.SelfTest/sutty.Terminal.SelfTest.csproj -c Debug --no-build
dotnet run --project tests/sutty.Setting.SelfTest/sutty.Setting.SelfTest.csproj -c Debug --no-build
dotnet run --project tests/sutty.Sftp.SelfTest/sutty.Sftp.SelfTest.csproj -c Debug --no-build
```

전체 로컬 검증 순서와 restore/build 선행 조건은 [기여 가이드](CONTRIBUTING.md)를 기준으로 확인하세요.

### 라이선스

Sutty는 [MIT License](LICENSE)로 배포합니다.

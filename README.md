<p align="center">
  <img src="src/sutty.UI/Assets/sutty.targetsize-256.png" alt="Sutty logo" width="112" />
</p>

<h1 align="center">Sutty</h1>

<p align="center"><strong>A modern SSH workspace for Windows.</strong></p>

<p align="center">
  <a href="#english">English</a> · <a href="#한국어">한국어</a>
</p>

> **Alpha, not GA.** Sutty is an active engineering build. Review the limitations before using important systems.

> **알파, GA 아님.** Sutty는 개발 중인 엔지니어링 빌드입니다. 중요한 시스템에 사용하기 전에 현재 한계를 확인해야 합니다.

> **Current / 현재:** [`v0.1.0-alpha.1`](https://github.com/yongsoocho/sutty/releases/tag/v0.1.0-alpha.1) · [Download / 다운로드](https://github.com/yongsoocho/sutty/releases) · [Install / 설치](docs/ALPHA_INSTALL.md)

## English

Sutty brings everyday **local terminals, SSH, SFTP, reusable commands, and multi-session operations** into one Windows-local workspace. That is a product goal, not a statement that every planned capability is complete.

The product is local-first, Windows-only, and centered on five cooperating work surfaces:

- **Local** — a tabbed Windows PowerShell session backed by Windows ConPTY.
- **Terminal** — an interactive SSH.NET `ShellStream` PTY.
- **REPL** — structured, non-interactive command cells.
- **Files** — SFTP browsing, file operations, and a compact transfer queue.
- **Multi** — deliberate command execution across selected connected sessions.

### Implemented Alpha baseline

- Password, OpenSSH/PEM/PKCS#8/PPK v2-v3 private-key, Windows SSH Agent, and OTP/MFA keyboard-interactive authentication. Keyboard-interactive challenges can contain multiple prompts and can repeat during one connection; secrets remain transient unless the encrypted vault is explicitly enabled.
- Local PowerShell tabs opened with the top `+` button, backed by a real ConPTY process with runtime resize and process-tree cleanup.
- A real persistent PTY channel rendered by package-local xterm.js in a hardened WebView2, with ANSI/VT color and style, mouse/input modes, IME/CJK/emoji handling, alternate screen, search, clipboard shortcuts, bounded output backpressure, and runtime server-side resize.
- Fail-closed SSH host-key verification. Unknown keys offer **Connect once**, **Trust and save**, or **Cancel**; changed saved keys are blocked.
- Independent SSH, Terminal, and SFTP states, so an unavailable SFTP subsystem does not close a working SSH session.
- REPL and Multi command execution backed by structured standard output, standard error, exit status/signal, and duration; reusable positional command templates remain available.
- Remote SFTP navigation and lazy loading; bounded recursive tree enumeration and filename search; file/folder upload and download (including empty directories); same-directory rename; cross-directory move without overwrite; safe recursive deletion; octal permission changes; directory creation; Copy path; and Open in Terminal. Symbolic links are listed but never followed recursively.
- A compact per-panel transfer queue with queued/running state, an explicit `0%`–`100%` value, progress bar, speed, ETA, cancellation, and an eight-job cap. Transfers support resumable deterministic partial files, persisted non-secret checkpoints, configurable transient-failure retries, and user-selectable final-size or SHA-256 verification (safe SHA-256 by default).
- Safe file-transfer staging. Uploads use a remote temporary name and preserve an existing destination during promotion; downloads use an adjacent local temporary file. Multi supports 1→N upload and N→1 download for explicitly checked sessions, with per-server progress/results, deterministic local isolation, and failed/incomplete-target-only retry. A credential-free atomic job queue restores incomplete single and Multi transfers after restart.
- Append-only connection-attempt history plus explicit Saved Host profiles, groups, environments, favorites, and search in SQLite.
- Credential-free Saved Host launcher: `sutty.UI.exe --host <id or exact name>` opens an existing profile while rejecting password/passphrase arguments; `sutty.UI.exe --version` reports the Alpha build.
- Opt-in local credential storage using a per-user Windows-protected AES-256-GCM vault; SQLite and settings contain only opaque credential references.
- Up to 16 mixed local/SSH tabs, zero default Multi targets, and an extra confirmation for broadcasts that include PROD-tagged SSH sessions.
- Optional restart-safe Workspace restoration remembers only local tabs and opaque Saved Host ids. SSH reconnection asks first by default, and previous commands are never stored or replayed.
- Immediately applied Korean/English settings, atomic settings persistence, dark/light themes, terminal palettes (including Ubuntu, Atom One Dark, Dracula, GitHub, and Solarized), cursor/scrollback/accessibility controls, and optional PowerShell profile loading for prompt customizers.
- Direct, HTTP CONNECT, SOCKS4, SOCKS5, SSH jump, and external ProxyCommand routes shared by SSH and SFTP. Strict route policy rejects direct routes instead of silently falling back.
- Session-scoped local, remote, and dynamic forwarding rules that start after SSH authentication and stop with the owning session.
- Saved Hosts restore route and tunnel definitions while route credentials remain in the encrypted vault. Settings can import credential-free OpenSSH config, Windows saved-session registry entries, and legacy INI profiles with duplicate suppression.
- REPL JSON/YAML syntax highlighting, red critical/error and amber warning marking, and history/saved-command suggestions accepted with Right Arrow or Tab.
- Keyboard-first navigation: `Ctrl+1`–`Ctrl+9` tabs, `Ctrl+T` local tab, `Alt+1`–`Alt+6` work surfaces/settings, `Ctrl+,` settings, and Insert-style copy/paste.

### Why this is not GA

- The package-local xterm.js/WebView2 renderer is integrated for both SSH and local ConPTY, but the required shell/TUI/Unicode/input/security/latency/soak acceptance matrix is not complete. Terminal compatibility therefore remains **Alpha, not GA**. See [ADR 0001](docs/adr/0001-terminal-renderer.md).
- Windows Agent, repeated OTP/multi-prompt keyboard-interactive authentication, PPK v2/v3, SSH jump, and external ProxyCommand routes are integrated, but their live-server/agent/route compatibility matrix is incomplete. Managed audited gateways, route policy distribution, and safe command replay after a full SSH reconnect remain unavailable.
- Saved Hosts support create/update, favorite, search, and delete, but duplicate-profile UX, bulk management, and operating-system credential-broker integration remain planned.
- SFTP recursive transfer, durable restart queue/checkpoints, retry/resume, pause, checksum verification, five collision policies, recursive deletion, `chmod`, filename search, cross-directory move, 1→N/N→1 Multi transfer, and failed-target-only retry are implemented. Synchronized browsing, directory comparison, and large/deep-path live multi-host acceptance remain incomplete.
- REPL output is completion-based rather than streamed. Multi uses structured per-host results, but its UI truncates output to a compact preview and has no persistent audit/export, timeout, or streaming workflow.
- Local/remote/dynamic forwarding has session lifecycle support and non-loopback binds require an explicit high-risk confirmation, but a post-connect tunnel manager and the live forwarding matrix remain incomplete. A signed-MSIX/update/rollback workflow exists for x64 and ARM64, but no production certificate or accepted signed artifact has been supplied. Credential-free sharing packs, local support bundles, and the GA compatibility/accessibility matrices remain planned.

The detailed current-state mapping is in [Requirements Traceability](docs/REQUIREMENTS.md), with the latest milestone summary in [Alpha implementation status](docs/IMPLEMENTATION_STATUS.md). Live-server, scale, soak, and signed-package gates are in [Release acceptance](docs/RELEASE_ACCEPTANCE.md). Product admission rules and explicit non-goals are fixed in [Product Scope](docs/PRODUCT_SCOPE.md), with delivery order in the [Roadmap](docs/ROADMAP.md) and supporting rationale in [Product Direction](docs/PRODUCT_DIRECTION.md).

### Explicitly unsupported scope

Sutty does not support FTP, FTPS, Telnet, Serial, RDP, VNC, X11 forwarding, cloud accounts or sync, Team Vault/RBAC/SSO, terminal collaboration, mobile, macOS, or Linux applications. These are not hidden Alpha features. Cloud and team collaboration are outside the local-first product boundary.

### Trust, credentials, and local data

By default, Sutty does **not** persist passwords or private-key passphrases. If the user explicitly enables **Remember credentials** for a Saved Host, Sutty stores only that secret in an AES-256-GCM vault whose random master key is protected by Windows DPAPI for the current user. Secrets are never written to SQLite or `settings.json`.

Local files under `%LOCALAPPDATA%\sutty` include:

- `settings.json` — preferences, recent tags, and recent private-key **paths**.
- `workspace.json` — up to 16 local-tab markers and opaque Saved Host ids; no credentials or terminal commands.
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
| [`sutty.UI`](src/sutty.UI) | WinUI 3 shell, local/SSH terminal presentation, REPL, Files, Multi, and settings UI |
| [`sutty.Core`](src/sutty.Core) | Local ConPTY, SSH sessions, command results, interactive-terminal contract, host-key trust, and SFTP services |
| [`sutty.SshAgent`](src/sutty.SshAgent) | Upstream-pinned Windows OpenSSH Agent/Pageant adapter compiled against the selected SSH.NET runtime |
| [`sutty.Command`](src/sutty.Command) | SQLite command templates, connection history, pins, and non-secret drafts |
| [`sutty.Setting`](src/sutty.Setting) | Atomic JSON-backed application settings |
| [`tests`](tests) | Focused self-tests plus an opt-in credentialed live-server smoke/fault/scale/soak harness; not a completed GA matrix |

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
dotnet run --project tests/sutty.Terminal.SelfTest/sutty.Terminal.SelfTest.csproj -c Debug --no-build
dotnet run --project tests/sutty.Core.Security.SelfTest/sutty.Core.Security.SelfTest.csproj -c Debug --no-build
dotnet run --project tests/sutty.Sftp.SelfTest/sutty.Sftp.SelfTest.csproj -c Debug --no-build
```

### License

Sutty is available under the [MIT License](LICENSE).

---

## 한국어

Sutty는 일상적인 **로컬 터미널, SSH, SFTP, 재사용 명령, 다중 세션 운영**을 하나의 Windows 로컬 작업 공간에 통합하는 것을 목표로 합니다. 이는 제품 목표이며 계획한 모든 기능이 현재 완성됐다는 뜻은 아닙니다.

제품은 로컬 우선·Windows 전용이며 다음 다섯 작업 화면을 함께 제공합니다.

- **Local** — Windows ConPTY 기반의 탭형 Windows PowerShell 세션
- **Terminal** — SSH.NET `ShellStream` 기반 대화형 PTY
- **REPL** — 구조화된 비대화형 명령 셀
- **Files** — SFTP 탐색·파일 작업·간결한 전송 큐
- **Multi** — 사용자가 선택한 연결 세션에만 실행하는 명시적 다중 명령

### 구현된 Alpha 기준선

- 비밀번호, OpenSSH/PEM/PKCS#8/PPK v2-v3 개인키, Windows SSH Agent, OTP/MFA keyboard-interactive 인증을 지원합니다. 한 연결에서 여러 질문이 반복되어도 처리하며, 암호화 Vault를 명시적으로 켜지 않으면 비밀값은 저장하지 않습니다.
- 상단 `+` 버튼으로 여는 로컬 PowerShell 탭. 실제 ConPTY 프로세스, 실행 중 크기 변경, 프로세스 트리 정리를 사용합니다.
- 패키지 내부 xterm.js와 보안 설정한 WebView2로 표시하는 실제 지속 PTY 채널. ANSI/VT 색·스타일, 마우스·입력 모드, IME·한글·이모지, 대체 화면, 검색, 클립보드 단축키, 제한된 출력 백프레셔, 실행 중 서버 측 크기 변경을 지원합니다.
- 기본 차단 방식의 SSH 호스트키 검증. 알 수 없는 키는 **이번만 연결**, **신뢰하고 저장**, **취소**를 제공하며 저장된 키가 바뀌면 연결을 차단합니다.
- SFTP subsystem을 사용할 수 없어도 작동 중인 SSH 세션을 닫지 않는 SSH·Terminal·SFTP 독립 상태
- 표준 출력·표준 오류·종료 상태/signal·소요 시간을 구조화하는 REPL·Multi 명령 실행과 재사용 가능한 위치형 명령 템플릿
- 원격 SFTP 탐색과 지연 로딩, 제한된 재귀 트리 열거·파일명 검색, 파일·폴더 업로드/다운로드(빈 폴더 포함), 같은 디렉터리 내 이름 변경, 덮어쓰기 없는 디렉터리 간 이동, 안전한 재귀 삭제, 8진수 권한 변경, 디렉터리 생성, 경로 복사, Terminal에서 열기. 심볼릭 링크는 표시하지만 재귀적으로 따라가지 않습니다.
- 대기·실행 상태, 명시적인 `0%`–`100%` 숫자, 진행 막대, 속도, ETA, 취소, 최대 8개 작업을 제공하는 패널별 전송 큐. 결정적인 partial 파일, 비밀정보 없는 영속 체크포인트, 설정 가능한 일시 오류 재시도, 사용자가 선택하는 최종 크기 또는 SHA-256 검증(기본값은 안전한 SHA-256)으로 전송을 재개할 수 있습니다.
- 안전한 파일 전송 준비 단계. 업로드는 원격 임시 이름을 사용하고 기존 대상을 보존한 채 승격하며, 다운로드는 같은 로컬 디렉터리의 임시 파일을 사용합니다. Multi는 명시적으로 체크한 세션의 1→N 업로드와 N→1 다운로드, 서버별 진행률·결과, 결정적인 로컬 경로 분리, 실패·미완료 대상만 재시도를 지원합니다. 자격증명 없는 atomic job queue가 재실행 후 Single·Multi 미완료 전송을 복원합니다.
- SQLite 기반 append-only 접속 시도 기록과 명시적인 저장 호스트·그룹·환경·즐겨찾기·검색
- 자격증명 없는 저장 Host 실행: `sutty.UI.exe --host <ID 또는 정확한 이름>`으로 기존 프로필을 열며 비밀번호·키 암호 인자는 거부하고, `sutty.UI.exe --version`으로 Alpha 버전을 확인
- Windows 사용자별 보호와 AES-256-GCM을 사용하는 선택형 로컬 자격증명 보관소. SQLite와 설정에는 불투명 참조만 저장
- 로컬/SSH 혼합 최대 16개 탭, 기본 선택 0개의 Multi 대상, PROD 태그 SSH 세션이 포함된 브로드캐스트의 추가 확인
- 선택형 Workspace 복원은 로컬 탭과 불투명 저장 Host ID만 기억합니다. SSH 재연결은 기본적으로 먼저 확인하며 이전 명령은 저장하거나 재실행하지 않습니다.
- 즉시 반영되는 한국어/영어 설정, 원자적 설정 저장, 다크/라이트 테마, Ubuntu·Atom One Dark·Dracula·GitHub·Solarized 터미널 팔레트, 커서·스크롤백·접근성 설정, 프롬프트 꾸미기를 위한 선택형 PowerShell 프로필 로딩
- SSH와 SFTP가 함께 사용하는 Direct·HTTP CONNECT·SOCKS4·SOCKS5·SSH Jump·외부 ProxyCommand 연결 경로. 엄격 경로 정책에서는 Direct 경로와 조용한 우회를 차단합니다.
- SSH 인증 후 시작하고 해당 세션과 함께 종료하는 Local·Remote·Dynamic 포워딩 규칙
- 저장 Host는 route·tunnel 정의를 복원하고 route 자격증명은 암호화 Vault에만 둡니다. 설정에서 비밀정보 없이 OpenSSH config·Windows 저장 세션 registry·레거시 INI profile을 가져오며 중복은 건너뜁니다.
- REPL JSON/YAML 문법 강조, critical/error 빨간색·warning 노란색 표시, 최근/저장 명령 제안과 오른쪽 화살표·Tab 적용
- `Ctrl+1`–`Ctrl+9` 탭, `Ctrl+T` 로컬 탭, `Alt+1`–`Alt+6` 작업 화면/설정, `Ctrl+,` 설정, Insert 방식 복사·붙여넣기 단축키

### GA가 아닌 이유

- 패키지 내부 xterm.js/WebView2 렌더러를 SSH와 로컬 ConPTY에 연결했지만 필수 셸·TUI·Unicode·입력·보안·지연·장시간 실행 인수 매트릭스는 아직 완성되지 않았습니다. 따라서 터미널 호환성은 계속 **Alpha이며 GA가 아닙니다**. [ADR 0001](docs/adr/0001-terminal-renderer.md)을 확인하세요.
- Windows Agent, 반복 OTP·다중 prompt keyboard-interactive 인증, PPK v2/v3, SSH Jump, 외부 ProxyCommand 경로를 통합했지만 실제 서버·Agent·경로 호환성 매트릭스는 아직 미완성입니다. 관리형 감사 게이트웨이, 경로 정책 배포, 전체 SSH 재연결 뒤 안전한 명령 재실행은 지원하지 않습니다.
- 저장 호스트는 생성·수정·즐겨찾기·검색·삭제를 지원하지만 프로필 복제 UX, 일괄 관리, 운영체제 자격증명 브로커 연동은 계획 상태입니다.
- SFTP 재귀 전송, 영속 재시작 queue·checkpoint, 재시도·재개·일시정지, checksum 검증, 다섯 충돌 정책, 재귀 삭제, `chmod`, 파일명 검색, 디렉터리 간 이동, 1→N/N→1 Multi 전송, 실패 대상만 재시도를 구현했습니다. 동기 탐색·디렉터리 비교와 대용량·깊은 경로·실제 다중 Host 인수는 미완성입니다.
- REPL 출력은 스트리밍이 아니라 완료 후 표시됩니다. Multi는 구조화된 호스트별 결과를 사용하지만 UI 출력은 짧게 잘린 미리보기이며 영속 audit/export, timeout, streaming 흐름이 없습니다.
- Local·Remote·Dynamic 포워딩은 세션 수명주기에 연결했고 loopback이 아닌 bind는 고위험 확인을 요구하지만 연결 후 tunnel 관리자와 실제 포워딩 매트릭스는 미완성입니다. x64·ARM64 서명 MSIX·업데이트·롤백 workflow는 있지만 production 인증서와 인수 완료된 서명 산출물은 아직 없습니다. 자격증명 없는 공유 pack, 로컬 support bundle, GA 호환성·접근성 매트릭스는 계획 상태입니다.

현재 상태의 상세 연결표는 [요구사항 추적표](docs/REQUIREMENTS.md), 이번 마일스톤 요약은 [Alpha 구현 상태](docs/IMPLEMENTATION_STATUS.md), 실서버·대용량·soak·서명 패키지 게이트는 [출시 인수 기준](docs/RELEASE_ACCEPTANCE.md)에 있습니다. 기능 채택 규칙과 명시적 비목표는 [제품 범위](docs/PRODUCT_SCOPE.md), 개발 순서는 [로드맵](docs/ROADMAP.md), 설계 근거는 [제품 방향](docs/PRODUCT_DIRECTION.md)에 정리했습니다.

### 명시적 미지원 범위

Sutty는 FTP, FTPS, Telnet, Serial, RDP, VNC, X11 포워딩, 클라우드 계정·동기화, Team Vault/RBAC/SSO, 터미널 협업, 모바일, macOS, Linux 앱을 지원하지 않습니다. 숨겨진 Alpha 기능이 아니며 클라우드·팀 협업은 로컬 우선 제품 경계 밖입니다.

### 신뢰, 자격 증명, 로컬 데이터

기본적으로 Sutty는 비밀번호와 개인키 passphrase를 **영구 저장하지 않습니다**. 사용자가 저장 호스트에서 **자격 증명 기억**을 명시적으로 켠 경우에만 해당 비밀을 AES-256-GCM 보관소에 저장하고, 임의 master key는 현재 Windows 사용자의 DPAPI로 보호합니다. 비밀은 SQLite나 `settings.json`에 기록하지 않습니다.

`%LOCALAPPDATA%\sutty` 아래의 로컬 파일은 다음과 같습니다.

- `settings.json` — 환경설정, 최근 태그, 최근 개인키 **경로**
- `workspace.json` — 최대 16개의 로컬 탭 표시와 불투명 저장 Host ID. 자격증명과 터미널 명령은 기록하지 않음
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
| [`sutty.UI`](src/sutty.UI) | WinUI 3 셸, 로컬/SSH 터미널 표시, REPL, Files, Multi, 설정 UI |
| [`sutty.Core`](src/sutty.Core) | 로컬 ConPTY, SSH 세션, 명령 결과, 대화형 터미널 계약, 호스트키 신뢰, SFTP 서비스 |
| [`sutty.SshAgent`](src/sutty.SshAgent) | 선택한 SSH.NET 런타임에 맞춰 직접 빌드하는 upstream 고정 Windows OpenSSH Agent/Pageant adapter |
| [`sutty.Command`](src/sutty.Command) | SQLite 명령 템플릿, 접속 기록, pin, 비밀정보 없는 초안 |
| [`sutty.Setting`](src/sutty.Setting) | 원자적으로 저장하는 JSON 환경설정 |
| [`tests`](tests) | 집중형 self-test와 선택 실행하는 자격증명 기반 실서버 smoke·fault·scale·soak harness. 아직 완료된 GA 매트릭스가 아닙니다. |

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
dotnet run --project tests/sutty.Terminal.SelfTest/sutty.Terminal.SelfTest.csproj -c Debug --no-build
dotnet run --project tests/sutty.Core.Security.SelfTest/sutty.Core.Security.SelfTest.csproj -c Debug --no-build
dotnet run --project tests/sutty.Sftp.SelfTest/sutty.Sftp.SelfTest.csproj -c Debug --no-build
```

### 라이선스

Sutty는 [MIT License](LICENSE)로 배포합니다.

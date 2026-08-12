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

## English

Sutty brings everyday **local terminals, SSH, SFTP, reusable commands, and multi-session operations** into one Windows-local workspace. That is a product goal, not a statement that every planned capability is complete.

The product is local-first, Windows-only, and centered on five cooperating work surfaces:

- **Local** — a tabbed Windows PowerShell session backed by Windows ConPTY.
- **Terminal** — an interactive SSH.NET `ShellStream` PTY.
- **REPL** — structured, non-interactive command cells.
- **Files** — SFTP browsing, file operations, and a compact transfer queue.
- **Multi** — deliberate command execution across selected connected sessions.

### Implemented Alpha baseline

- Password and OpenSSH/PEM private-key authentication. Password mode also answers password-like keyboard-interactive prompts through a non-interactive fallback; recent key paths can be suggested without saving the key contents, password, or passphrase.
- Local PowerShell tabs opened with the top `+` button, backed by a real ConPTY process with runtime resize and process-tree cleanup.
- A real persistent PTY channel with runtime server-side resize, control keys, navigation keys, F1–F12, incremental UTF-8 decoding, bounded output buffering, cursor operations, scroll regions, and alternate-screen handling.
- Fail-closed SSH host-key verification. Unknown keys offer **Connect once**, **Trust and save**, or **Cancel**; changed saved keys are blocked.
- Independent SSH, Terminal, and SFTP states, so an unavailable SFTP subsystem does not close a working SSH session.
- REPL and Multi command execution backed by structured standard output, standard error, exit status/signal, and duration; reusable positional command templates remain available.
- Remote SFTP navigation and lazy loading; file upload/download; same-directory rename; file or empty-directory deletion; directory creation; Copy path; and Open in Terminal.
- A compact per-panel transfer queue with queued/running state, an explicit `0%`–`100%` value, progress bar, speed, ETA, cancellation, and an eight-job cap. SSH.NET SFTP calls are serialized per client.
- Safe file-transfer staging. Uploads use a remote temporary name and preserve an existing destination during promotion; downloads use an adjacent local temporary file.
- Append-only connection-attempt history plus explicit Saved Host profiles, groups, environments, favorites, and search in SQLite.
- Opt-in local credential storage using a per-user Windows-protected AES-256-GCM vault; SQLite and settings contain only opaque credential references.
- Up to 16 mixed local/SSH tabs, zero default Multi targets, and an extra confirmation for broadcasts that include PROD-tagged SSH sessions.
- Immediately applied Korean/English settings, atomic settings persistence, and dark/light themes.
- Direct, HTTP CONNECT, SOCKS4, and SOCKS5 connection routes shared by SSH and SFTP. Enterprise mode rejects direct routes instead of silently falling back.
- REPL JSON/YAML syntax highlighting, red critical/error and amber warning marking, and history/saved-command suggestions accepted with Right Arrow or Tab.
- Keyboard-first navigation: `Ctrl+1`–`Ctrl+9` tabs, `Ctrl+T` local tab, `Alt+1`–`Alt+6` work surfaces/settings, `Ctrl+,` settings, and Insert-style copy/paste.

### Why this is not GA

- The SSH PTY and local ConPTY are real and support runtime resize, but the current WinUI text renderer is an **Alpha-only bridge**. It consumes SGR without rendering color/style and lacks mouse protocols and complete wide/combining-cell behavior. See [ADR 0001](docs/adr/0001-terminal-renderer.md).
- SSH agent, OTP/multi-prompt keyboard-interactive UI, legacy `.ppk` import, jump hosts, audited gateway adapters, external proxy commands, and automatic reconnect are unavailable. Direct HTTP/SOCKS proxy routes are an Alpha baseline without the complete enterprise route matrix. Password mode's non-interactive fallback handles password-like prompts only.
- Saved Hosts support create/update, favorite, search, and delete, but duplicate-profile UX, bulk management, and operating-system credential-broker integration remain planned.
- SFTP currently transfers files, not directory trees. It has no pause/retry/resume, final size/checksum verification, `chmod`, synchronized browsing, or a complete collision-policy matrix.
- REPL output is completion-based rather than streamed. Multi uses structured per-host results, but its UI truncates output to a compact preview and has no persistent audit/export, timeout, or streaming workflow.
- Port forwarding, managed gateway profiles, central audit transport, import/export, full enterprise policy, support bundles, signed release automation, and the GA compatibility/accessibility matrices remain planned.

The detailed current-state mapping is in [Requirements Traceability](docs/REQUIREMENTS.md), with the latest milestone summary in [Enterprise implementation status](docs/ENTERPRISE_IMPLEMENTATION_STATUS.md). Product gates and explicit non-goals are in [Product Direction](docs/PRODUCT_DIRECTION.md).

### Explicitly unsupported scope

Sutty does not support FTP, FTPS, Telnet, Serial, RDP, VNC, X11 forwarding, cloud accounts or sync, Team Vault/RBAC/SSO, terminal collaboration, mobile, macOS, or Linux applications. These are not hidden Alpha features. Cloud and team collaboration are outside the local-first product boundary.

### Trust, credentials, and local data

By default, Sutty does **not** persist passwords or private-key passphrases. If the user explicitly enables **Remember credentials** for a Saved Host, Sutty stores only that secret in an AES-256-GCM vault whose random master key is protected by Windows DPAPI for the current user. Secrets are never written to SQLite or `settings.json`.

Local files under `%LOCALAPPDATA%\sutty` include:

- `settings.json` — preferences, recent tags, and recent private-key **paths**.
- `sutty.db` — command templates and usage plus connection history, pins, host/user/port/auth metadata, private-key paths, and tags.
- `known-hosts.json` — public trusted SSH host keys and fingerprints.
- `vault.key` and `vault.json` — the DPAPI-protected master key and authenticated encrypted credential records, created only when the local vault is used.
- `crash.log` — local unhandled-exception details; it is not yet guaranteed to be redacted, so inspect it before sharing.

Private-key file contents stay in the user-selected external file. Unknown keys are rejected before a trust prompt and retried only after an explicit decision. A changed saved key is never silently replaced.

Read [Security](SECURITY.md) before reporting or sharing diagnostic data.

### Architecture

| Project | Current responsibility |
| --- | --- |
| [`sutty.UI`](src/sutty.UI) | WinUI 3 shell, local/SSH terminal presentation, REPL, Files, Multi, and settings UI |
| [`sutty.Core`](src/sutty.Core) | Local ConPTY, SSH sessions, command results, interactive-terminal contract, host-key trust, and SFTP services |
| [`sutty.Command`](src/sutty.Command) | SQLite command templates, connection history, pins, and non-secret drafts |
| [`sutty.Setting`](src/sutty.Setting) | Atomic JSON-backed application settings |
| [`tests`](tests) | Focused terminal, host-key, and safe local-transfer self-tests; not yet the GA integration matrix |

### Prerequisites, build, and run

- Windows 11 24H2 or later
- x64 or ARM64
- .NET SDK 10.0.302, selected by `global.json`
- Windows App SDK 2.3.1 and SSH.NET 2025.1.0, pinned by the project and lock files
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

- 비밀번호와 OpenSSH/PEM 개인키 인증. 비밀번호 방식은 password 형태의 keyboard-interactive prompt에 비대화형 fallback으로 답하며, 키 내용·비밀번호·passphrase를 저장하지 않고 최근 키 경로만 제안할 수 있습니다.
- 상단 `+` 버튼으로 여는 로컬 PowerShell 탭. 실제 ConPTY 프로세스, 실행 중 크기 변경, 프로세스 트리 정리를 사용합니다.
- 실행 중 서버 측 크기 변경, 제어키·탐색키·F1–F12, 점진적 UTF-8 디코딩, 제한된 출력 버퍼, 커서 동작, 스크롤 영역, 대체 화면을 처리하는 실제 지속 PTY 채널
- 기본 차단 방식의 SSH 호스트키 검증. 알 수 없는 키는 **이번만 연결**, **신뢰하고 저장**, **취소**를 제공하며 저장된 키가 바뀌면 연결을 차단합니다.
- SFTP subsystem을 사용할 수 없어도 작동 중인 SSH 세션을 닫지 않는 SSH·Terminal·SFTP 독립 상태
- 표준 출력·표준 오류·종료 상태/signal·소요 시간을 구조화하는 REPL·Multi 명령 실행과 재사용 가능한 위치형 명령 템플릿
- 원격 SFTP 탐색과 지연 로딩, 파일 업로드·다운로드, 같은 디렉터리 내 이름 변경, 파일 또는 빈 디렉터리 삭제, 디렉터리 생성, 경로 복사, Terminal에서 열기
- 대기·실행 상태, 명시적인 `0%`–`100%` 숫자, 진행 막대, 속도, ETA, 취소, 최대 8개 작업을 제공하는 패널별 전송 큐. SSH.NET SFTP 호출은 클라이언트별로 직렬화합니다.
- 안전한 파일 전송 준비 단계. 업로드는 원격 임시 이름을 사용하고 기존 대상을 보존한 채 승격하며, 다운로드는 같은 로컬 디렉터리의 임시 파일을 사용합니다.
- SQLite 기반 append-only 접속 시도 기록과 명시적인 저장 호스트·그룹·환경·즐겨찾기·검색
- Windows 사용자별 보호와 AES-256-GCM을 사용하는 선택형 로컬 자격증명 보관소. SQLite와 설정에는 불투명 참조만 저장
- 로컬/SSH 혼합 최대 16개 탭, 기본 선택 0개의 Multi 대상, PROD 태그 SSH 세션이 포함된 브로드캐스트의 추가 확인
- 즉시 반영되는 한국어/영어 설정, 원자적 설정 저장, 다크/라이트 테마
- SSH와 SFTP가 함께 사용하는 Direct·HTTP CONNECT·SOCKS4·SOCKS5 연결 경로. 기업 모드에서는 Direct 경로와 조용한 우회를 차단합니다.
- REPL JSON/YAML 문법 강조, critical/error 빨간색·warning 노란색 표시, 최근/저장 명령 제안과 오른쪽 화살표·Tab 적용
- `Ctrl+1`–`Ctrl+9` 탭, `Ctrl+T` 로컬 탭, `Alt+1`–`Alt+6` 작업 화면/설정, `Ctrl+,` 설정, Insert 방식 복사·붙여넣기 단축키

### GA가 아닌 이유

- SSH PTY와 로컬 ConPTY는 실제이고 실행 중 크기 변경을 지원하지만 현재 WinUI 텍스트 렌더러는 **Alpha 전용 연결 단계**입니다. SGR을 소비하지만 색·스타일을 표시하지 않고, 마우스 프로토콜과 넓은 문자·결합 문자의 완전한 셀 처리가 없습니다. [ADR 0001](docs/adr/0001-terminal-renderer.md)을 확인하세요.
- SSH agent, OTP·다중 prompt keyboard-interactive UI, 레거시 `.ppk` 가져오기, 점프 호스트, 감사 게이트웨이 어댑터, 외부 프록시 명령, 자동 재연결은 지원하지 않습니다. Direct HTTP/SOCKS 프록시는 Alpha 기준선이며 기업용 전체 경로 매트릭스는 아직 검증되지 않았습니다. 비밀번호 방식의 비대화형 fallback은 password 형태 prompt만 처리합니다.
- 저장 호스트는 생성·수정·즐겨찾기·검색·삭제를 지원하지만 프로필 복제 UX, 일괄 관리, 운영체제 자격증명 브로커 연동은 계획 상태입니다.
- 현재 SFTP는 파일만 전송하며 디렉터리 트리는 전송하지 않습니다. 일시정지·재시도·재개, 최종 크기/checksum 검증, `chmod`, 동기 탐색, 완전한 충돌 정책 매트릭스가 없습니다.
- REPL 출력은 스트리밍이 아니라 완료 후 표시됩니다. Multi는 구조화된 호스트별 결과를 사용하지만 UI 출력은 짧게 잘린 미리보기이며 영속 audit/export, timeout, streaming 흐름이 없습니다.
- 포트 포워딩, 관리형 게이트웨이 프로필, 중앙 감사 전송, 가져오기·내보내기, 전체 기업 정책, 지원 번들, 서명 릴리스 자동화, GA 호환성·접근성 매트릭스는 계획 상태입니다.

현재 상태의 상세 연결표는 [요구사항 추적표](docs/REQUIREMENTS.md), 이번 마일스톤 요약은 [Enterprise 구현 상태](docs/ENTERPRISE_IMPLEMENTATION_STATUS.md), 제품 게이트와 명시적 비목표는 [제품 방향](docs/PRODUCT_DIRECTION.md)에 있습니다.

### 명시적 미지원 범위

Sutty는 FTP, FTPS, Telnet, Serial, RDP, VNC, X11 포워딩, 클라우드 계정·동기화, Team Vault/RBAC/SSO, 터미널 협업, 모바일, macOS, Linux 앱을 지원하지 않습니다. 숨겨진 Alpha 기능이 아니며 클라우드·팀 협업은 로컬 우선 제품 경계 밖입니다.

### 신뢰, 자격 증명, 로컬 데이터

기본적으로 Sutty는 비밀번호와 개인키 passphrase를 **영구 저장하지 않습니다**. 사용자가 저장 호스트에서 **자격 증명 기억**을 명시적으로 켠 경우에만 해당 비밀을 AES-256-GCM 보관소에 저장하고, 임의 master key는 현재 Windows 사용자의 DPAPI로 보호합니다. 비밀은 SQLite나 `settings.json`에 기록하지 않습니다.

`%LOCALAPPDATA%\sutty` 아래의 로컬 파일은 다음과 같습니다.

- `settings.json` — 환경설정, 최근 태그, 최근 개인키 **경로**
- `sutty.db` — 명령 템플릿·사용 정보와 접속 기록, pin, 호스트·사용자·포트·인증 메타데이터, 개인키 경로, 태그
- `known-hosts.json` — 신뢰한 공개 SSH 호스트키와 지문
- `vault.key`, `vault.json` — 로컬 보관소 사용 시에만 생성되는 DPAPI 보호 master key와 인증 암호화된 자격 증명 기록
- `crash.log` — 로컬 미처리 예외 상세. 아직 redaction을 보장하지 않으므로 공유 전에 반드시 확인해야 합니다.

개인키 파일 내용은 사용자가 선택한 외부 파일에 그대로 있습니다. 알 수 없는 키는 신뢰 창보다 먼저 거부하고 명시적 결정 뒤에만 새 연결로 재시도합니다. 변경된 저장 키를 조용히 교체하지 않습니다.

진단 정보를 공유하거나 보안 문제를 신고하기 전에 [보안 문서](SECURITY.md)를 확인하세요.

### 구조

| 프로젝트 | 현재 역할 |
| --- | --- |
| [`sutty.UI`](src/sutty.UI) | WinUI 3 셸, 로컬/SSH 터미널 표시, REPL, Files, Multi, 설정 UI |
| [`sutty.Core`](src/sutty.Core) | 로컬 ConPTY, SSH 세션, 명령 결과, 대화형 터미널 계약, 호스트키 신뢰, SFTP 서비스 |
| [`sutty.Command`](src/sutty.Command) | SQLite 명령 템플릿, 접속 기록, pin, 비밀정보 없는 초안 |
| [`sutty.Setting`](src/sutty.Setting) | 원자적으로 저장하는 JSON 환경설정 |
| [`tests`](tests) | Terminal·호스트키·안전한 로컬 전송 중심 self-test. 아직 GA 통합 매트릭스가 아닙니다. |

### 준비 사항, 빌드, 실행

- Windows 11 24H2 이상
- x64 또는 ARM64
- `global.json`이 선택하는 .NET SDK 10.0.302
- 프로젝트와 lock file이 고정하는 Windows App SDK 2.3.1과 SSH.NET 2025.1.0
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

<p align="center">
  <img src="src/sutty.UI/Assets/sutty.targetsize-256.png" alt="Sutty logo" width="112" />
</p>

<h1 align="center">Sutty</h1>

<p align="center"><strong>A modern SSH workspace for Windows.</strong></p>

<p align="center">
  Terminal, SFTP, command history, and multi-server operations in one workspace.
</p>

<p align="center">
  <a href="#english">English</a> · <a href="#한국어">한국어</a>
</p>

> **Alpha software:** Sutty already has a working MVP shell, but it is not yet a PuTTY-compatible interactive terminal. Read [Current status and limitations](#current-status-and-limitations) before using it with important systems.

## Preview / 미리보기

The interface is undergoing a visual redesign. A current product screenshot will be added here when the redesigned shell stabilizes.

현재 인터페이스를 리디자인하고 있습니다. 리디자인된 셸이 안정화되면 최신 제품 스크린샷을 이곳에 추가할 예정입니다.

<!-- Add the canonical application screenshot here once it is checked into the repository. -->

---

## English

Sutty is a Windows-native workspace for day-to-day SSH operations. Its goal is to keep the remote terminal, remote files, reusable commands, connection history, and multi-server work together without turning them into unrelated tools.

The central product idea is **structured remote work**:

- **REPL** records each command, output, start time, and duration as a reusable cell.
- **Files** keeps the SFTP tree beside the active session.
- **Commands** stores parameterized command templates for repeated work.
- **Multi** broadcasts a command to multiple connected sessions.

### Features available today

- SSH command execution with password or OpenSSH/PEM private-key authentication
- Structured REPL cells and a continuous RAW-style output view
- Multiple tabbed sessions, with up to 16 active sessions
- Remote SFTP tree with drag-and-drop upload, per-file progress, and cancellation
- Independent SSH and SFTP readiness, so an unavailable SFTP subsystem does not close a working SSH session
- SQLite-backed command templates, parameters such as `$1`, and usage history
- Multi-session command broadcast and a 16-slot session overview
- Recent-host history with user-managed pins and reusable, non-secret connection drafts
- Searchable connection tags with recent-tag suggestions, plus recent private-key path suggestions
- Dark/light themes, Korean/English UI, and live application of settings

### Current status and limitations

Sutty is currently an **alpha/MVP**. The following boundaries are intentional and documented rather than hidden:

- **RAW is not a true PTY terminal yet.** Both REPL and RAW execute commands through SSH.NET `SshClient.CreateCommand`. RAW presents the results as one continuous log; it does not provide an interactive shell. Programs such as `vim`, `htop`, `tmux`, `less`, `passwd`, interactive `sudo`, database shells, and full-screen TUI applications will not work correctly.
- **SSH agent and interactive/OTP authentication are unavailable in the UI.** Password and private-key authentication are the supported paths today.
- **Host-key verification and `known_hosts` management are not implemented.** Do not rely on Sutty to establish trust on first connection.
- **PuTTY `.ppk` keys are not accepted by the current SSH.NET version.** Export them as an OpenSSH key with PuTTYgen first.
- **SFTP is upload-focused.** Transfer queues, downloads, remote rename/delete/mkdir/chmod, retry, and overwrite policy are roadmap work.
- Sutty is **Windows-only**. The repository configures x86, x64, and ARM64 targets; the commands below use x64.

### Architecture

| Project | Responsibility |
| --- | --- |
| [`sutty.UI`](src/sutty.UI) | WinUI 3 shell, sessions, terminal/REPL presentation, SFTP tree, and settings UI |
| [`sutty.Core`](src/sutty.Core) | SSH.NET sessions, connection models, and SFTP abstractions/implementation |
| [`sutty.Command`](src/sutty.Command) | SQLite command templates, usage data, and host history |
| [`sutty.Setting`](src/sutty.Setting) | JSON-backed application settings |

Local application data is stored under `%LOCALAPPDATA%\sutty`:

- `settings.json` stores application preferences, recent tags, and recent private-key **paths**.
- `sutty.db` stores command templates, host history, pins, tags, and non-secret connection drafts.

### Prerequisites

- Windows 10 version 1809 (build 17763) or later
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio with WinUI/Windows application development tools if you prefer the IDE workflow
- Git and internet access for the initial NuGet restore

The UI targets .NET 8 and WinUI 3 through Windows App SDK 2.2. The unpackaged build carries the Windows App SDK runtime with it.

### Build and run

```powershell
git clone https://github.com/yongsoocho/sutty.git
cd sutty

dotnet restore src/sutty.UI/sutty.UI.csproj -p:Platform=x64
dotnet build src/sutty.UI/sutty.UI.csproj -c Debug -p:Platform=x64 --no-restore
dotnet run --project src/sutty.UI/sutty.UI.csproj -c Debug -p:Platform=x64 --no-build
```

In a Visual Studio release that supports `.slnx`, open `sutty.slnx`, select `x64`, choose the **sutty.UI (Unpackaged)** launch profile, and run the project. If your release cannot open `.slnx`, open `src/sutty.UI/sutty.UI.csproj` directly instead.

### Security notes

- Sutty does **not** persist passwords or private-key passphrases.
- Recently used private-key paths are stored in plain text in `settings.json` for suggestions and in `sutty.db` when they are part of a History draft or pin.
- Hostnames, aliases, usernames, ports, authentication type, tags, connection timestamps, and command templates are stored locally in SQLite.
- Opening a real History item restores its non-secret fields in Home; Sutty still requires the password or private-key passphrase again.
- Until host-key verification is implemented, use Sutty only where you can independently trust and verify the destination.

### Roadmap

| Version | Focus |
| --- | --- |
| **v0.2 — Real Terminal** | PTY/ShellStream channel, ANSI/VT rendering, terminal resize and control keys, and host-key fingerprints/`known_hosts` |
| **v0.3 — Real SFTP** | Upload/download queue, complete remote file operations, retry/overwrite policy, speed and ETA, path bar, and terminal/SFTP working-directory sync |
| **v0.4 — Daily Driver** | Saved host profiles, Windows Credential Manager, OpenSSH/PuTTY import, `.ppk`, ssh-agent, jump hosts, forwarding, and reconnect |
| **v0.5 — Sutty Workflow** | Searchable REPL cells, reusable playbooks, command palette, safer grouped broadcasts, and transcript export |
| **v1.0 — Product Release** | Crash recovery, auto-update, code signing, installers, integration/transfer tests, documentation, and GitHub Releases |

### License

Sutty is available under the [MIT License](LICENSE).

---

## 한국어

Sutty는 일상적인 SSH 작업을 위한 Windows 네이티브 워크스페이스입니다. 원격 터미널, 원격 파일, 재사용 명령, 접속 기록, 다중 서버 작업을 서로 분리된 도구가 아니라 하나의 작업 흐름으로 묶는 것을 목표로 합니다.

핵심 제품 방향은 **구조화된 원격 작업**입니다.

- **REPL**은 명령, 출력, 시작 시각, 소요 시간을 재사용 가능한 셀로 기록합니다.
- **Files**는 현재 세션 옆에 SFTP 파일 트리를 유지합니다.
- **Commands**는 반복 작업을 위한 파라미터 명령 템플릿을 저장합니다.
- **Multi**는 연결된 여러 세션에 명령을 전송합니다.

### 현재 제공하는 기능

- 비밀번호 또는 OpenSSH/PEM 개인키를 이용한 SSH 명령 실행
- 구조화된 REPL 셀과 연속 로그 형태의 RAW 화면
- 최대 16개의 탭 기반 다중 세션
- 드래그 앤 드롭 업로드, 파일별 진행률, 취소를 지원하는 원격 SFTP 트리
- SFTP subsystem을 사용할 수 없어도 정상 SSH 세션을 유지하는 독립 준비 상태
- SQLite 기반 명령 템플릿, `$1` 형태의 파라미터, 사용 기록
- 다중 세션 명령 전송과 16슬롯 세션 개요
- 사용자가 고정하고 비밀 없는 연결 초안을 다시 불러올 수 있는 최근 호스트 기록
- 검색 가능한 연결 태그와 최근 태그 제안, 최근 개인키 경로 자동 제안
- 다크/라이트 테마, 한국어/영어 UI, 설정 즉시 반영

### 현재 상태와 제한 사항

Sutty는 현재 **알파/MVP** 단계입니다. 아래 한계를 숨기지 않고 명확히 공개합니다.

- **RAW는 아직 진짜 PTY 터미널이 아닙니다.** REPL과 RAW 모두 SSH.NET의 `SshClient.CreateCommand`로 명령을 실행합니다. RAW는 결과를 하나의 연속 로그처럼 표시할 뿐 대화형 셸을 제공하지 않습니다. 따라서 `vim`, `htop`, `tmux`, `less`, `passwd`, 대화형 `sudo`, 데이터베이스 셸, 전체 화면 TUI 프로그램은 정상적으로 사용할 수 없습니다.
- **SSH agent와 대화형/OTP 인증은 UI에서 사용할 수 없습니다.** 현재 지원 경로는 비밀번호와 개인키 인증입니다.
- **호스트 키 검증과 `known_hosts` 관리가 구현되지 않았습니다.** 최초 접속 대상의 신뢰 확인을 Sutty에 의존하면 안 됩니다.
- 현재 SSH.NET 버전에서는 **PuTTY `.ppk` 개인키를 사용할 수 없습니다.** PuTTYgen으로 OpenSSH 키를 내보낸 뒤 사용해야 합니다.
- **현재 SFTP는 업로드 중심입니다.** 전송 큐, 다운로드, 원격 rename/delete/mkdir/chmod, 재시도, 덮어쓰기 정책은 로드맵에 포함되어 있습니다.
- Sutty는 **Windows 전용**입니다. 저장소에는 x86, x64, ARM64 대상이 구성되어 있으며 아래 명령은 x64 기준입니다.

### 아키텍처

| 프로젝트 | 역할 |
| --- | --- |
| [`sutty.UI`](src/sutty.UI) | WinUI 3 셸, 세션, 터미널/REPL 표시, SFTP 트리, 설정 UI |
| [`sutty.Core`](src/sutty.Core) | SSH.NET 세션, 연결 모델, SFTP 추상화와 구현 |
| [`sutty.Command`](src/sutty.Command) | SQLite 명령 템플릿, 사용 데이터, 호스트 기록 |
| [`sutty.Setting`](src/sutty.Setting) | JSON 기반 애플리케이션 설정 |

로컬 애플리케이션 데이터는 `%LOCALAPPDATA%\sutty`에 저장됩니다.

- `settings.json`: 애플리케이션 설정, 최근 태그, 최근 개인키 **경로**
- `sutty.db`: 명령 템플릿, 호스트 기록, pin, 태그, 비밀 없는 연결 초안

### 개발 환경

- Windows 10 버전 1809(빌드 17763) 이상
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- IDE로 개발할 경우 WinUI/Windows 앱 개발 도구가 설치된 Visual Studio
- 최초 NuGet 복원을 위한 Git과 인터넷 연결

UI는 Windows App SDK 2.2 기반 WinUI 3와 .NET 8을 사용합니다. 비패키지 빌드에는 Windows App SDK 런타임이 함께 포함됩니다.

### 빌드와 실행

```powershell
git clone https://github.com/yongsoocho/sutty.git
cd sutty

dotnet restore src/sutty.UI/sutty.UI.csproj -p:Platform=x64
dotnet build src/sutty.UI/sutty.UI.csproj -c Debug -p:Platform=x64 --no-restore
dotnet run --project src/sutty.UI/sutty.UI.csproj -c Debug -p:Platform=x64 --no-build
```

`.slnx`를 지원하는 Visual Studio에서는 `sutty.slnx`를 열고 `x64`를 선택한 다음 **sutty.UI (Unpackaged)** 실행 프로필로 프로젝트를 실행합니다. 사용 중인 버전이 `.slnx`를 열지 못하면 `src/sutty.UI/sutty.UI.csproj`를 직접 여세요.

### 보안 안내

- Sutty는 비밀번호와 개인키 passphrase를 저장하지 않습니다.
- 자동 제안을 위한 최근 개인키 경로는 `settings.json`에 평문으로 저장되며, History 초안이나 pin에 포함되면 `sutty.db`에도 저장됩니다.
- 호스트 이름, 별칭, 사용자명, 포트, 인증 유형, 태그, 접속 시각, 명령 템플릿은 로컬 SQLite에 저장됩니다.
- 실제 History 항목을 열면 Home에 비밀 없는 필드만 복원되며, 비밀번호나 개인키 passphrase는 다시 입력해야 합니다.
- 호스트 키 검증을 구현하기 전까지는 접속 대상을 별도로 신뢰하고 검증할 수 있는 환경에서만 사용하세요.

### 로드맵

| 버전 | 목표 |
| --- | --- |
| **v0.2 — Real Terminal** | PTY/ShellStream 채널, ANSI/VT 렌더링, 터미널 크기 조절과 제어 키, 호스트 키 지문/`known_hosts` |
| **v0.3 — Real SFTP** | 업로드/다운로드 큐, 원격 파일 작업 완성, 재시도/덮어쓰기 정책, 속도와 ETA, 경로 바, 터미널과 SFTP 작업 경로 연동 |
| **v0.4 — Daily Driver** | 저장 호스트 프로필, Windows Credential Manager, OpenSSH/PuTTY 가져오기, `.ppk`, ssh-agent, 점프 호스트, 포트 포워딩, 재연결 |
| **v0.5 — Sutty Workflow** | REPL 셀 검색, 재사용 playbook, 명령 팔레트, 안전한 서버 그룹 전송, 세션 기록 내보내기 |
| **v1.0 — Product Release** | 충돌 복구, 자동 업데이트, 코드 서명, 설치 프로그램, 통합/전송 테스트, 문서화, GitHub Releases |

### 라이선스

Sutty는 [MIT License](LICENSE)로 배포됩니다.

# Sutty Product Direction / Sutty 제품 방향

> A modern SSH workspace for Windows. Alpha, not GA.
>
> Windows를 위한 현대적인 SSH 워크스페이스. Alpha이며 GA가 아닙니다.

[English](#english) · [한국어](#한국어)

## English

### Honest position

Sutty combines everyday **local terminals, SSH, SFTP, reusable commands, and multi-session operations** in one Windows-local workspace. The scope is deliberately limited:

- It concerns SSH/SFTP server operations on Windows.
- It is a target for a future GA release, not a claim that the current Alpha is complete.
- It does not include FTP/FTPS, Telnet/Serial, RDP/VNC/X11, cloud/mobile, or team collaboration.

Therefore the replacement target is the established **Windows SSH and SFTP administrator workflow**, not every protocol or platform available in unrelated client categories. Sutty must not claim full-product parity until that scope changes and the corresponding compatibility gates pass.

Sutty remains local-first: no account, no cloud backend, and no team control plane.

### Product surfaces

1. **Local** — tabbed Windows PowerShell sessions backed by Windows ConPTY.
2. **Terminal** — a persistent, interactive SSH PTY for shells and terminal applications.
3. **REPL** — structured non-interactive commands with separate results, timing, history, and reuse.
4. **Files** — session-aware SFTP navigation and safe transfers.
5. **Commands** — reusable operational templates that can grow into typed snippets/runbooks.
6. **Multi** — selected-session command execution with production safeguards and structured per-host results.

Terminal and REPL are complementary. The REPL remains Sutty's differentiator; it is not a substitute for terminal compatibility.

### Product principles

1. **One host, one logical workspace.** SSH, Terminal, REPL, Files, and future tunnels expose independent state inside one user session.
2. **Capability before controls.** A disabled or planned control must never be marketed as implemented.
3. **Fail closed at identity boundaries.** Unknown host keys need an explicit decision; changed keys are blocked.
4. **No plaintext secret persistence.** Passwords and private-key passphrases are not settings, history, or log metadata.
5. **Safe mutation.** Destructive file actions require confirmation, transfers stage before promotion, and collisions never overwrite silently.
6. **Production targets are deliberate.** Multi starts with zero selected sessions and PROD-tagged targets require an extra confirmation.
7. **Local metadata is still sensitive.** Hosts, usernames, paths, tags, and commands are not credentials, but documentation and exports must treat them as private operational data.
8. **English and Korean move together.** First-party product documentation and UI support only these two languages until both are complete.

### Current Alpha baseline

The current working tree contains tabbed local PowerShell through Windows ConPTY, a real SSH.NET `ShellStream` PTY with runtime server-side resize, a bounded native VT screen model, fail-closed known-host verification, separated SSH/Terminal/SFTP state, structured command results, practical single-file SFTP operations, a compact transfer queue with explicit percentage, Files/Terminal path integration, Saved Hosts with a local encrypted credential vault, append-only connection outcomes, live settings, and a zero-default-target Multi view with a PROD confirmation.

This baseline is useful for development and controlled testing. It has not passed the compatibility, security, accessibility, packaging, large-transfer, or soak gates required for GA.

### Release gates before any GA claim

| Gate | Current gap |
| --- | --- |
| Terminal compatibility | Replace the transitional native renderer with the approved local xterm.js/WebView2 design, harden the bridge, preserve the current server PTY resize contract, and pass the shell/TUI/input/Unicode matrix. |
| Secure host and credentials | Saved Host separation and a local encrypted Vault now exist; complete duplicate/bulk UX, OTP/multi-prompt keyboard-interactive UI, and broader secret-lifetime/redaction tests. |
| Host identity | Keep current unknown/trusted/changed enforcement, then add changed-key UX, rotation/management, audit, and integration coverage. |
| SFTP correctness | Add directory transfer, recursive safety, complete collision policy, symlink handling, permission/error coverage, and large-file/deep-path tests. |
| Transfer integrity | Add pause/retry behavior, final size verification, failure recovery, and evidence that cancel/disconnect cannot corrupt an existing destination. |
| Command correctness | Finish cancellation UX and failure-path regression coverage across REPL and Multi. |
| Production data | Keep first-run databases empty and the current release-artifact check enforced; add packaged clean-install/upgrade evidence so development data cannot return through migration or distribution. |
| Release quality | Add integration/UI/security tests, performance and soak evidence, dependency review, and documented supported builds. |

### P1 gates for the specified Windows product

- Host groups, tags, favorites, profile search, workspace restore, and command-line opening without secret arguments.
- Windows OpenSSH Agent, PPK import, jump hosts, HTTP/SOCKS proxy, limited reconnect, and negotiated-algorithm information.
- Persistent transfer management with configurable limits, retry/resume, verification, and restart recovery.
- Local, remote, and dynamic port forwarding with safe bind warnings.
- Streaming REPL output, typed snippet parameters, durable/exportable per-host Multi results, timeouts, and audit events.
- OpenSSH configuration and legacy SSH/SFTP profile import, encrypted Sutty export, and conflict preview.
- Local enterprise policy, managed host catalogs, diagnostics/support bundle, and redacted audit logging.
- Keyboard, High Contrast, text scaling, Narrator, Korean/English, Windows 11 x64/ARM64, signed MSIX, update, and enterprise deployment validation.

GA requires the relevant P0 and P1 rows in [Requirements Traceability](REQUIREMENTS.md) to be complete with tests and documentation. A code path alone is not a release gate.

### Delivery sequence

1. **Foundation** — supported runtime/platforms, empty production data, architecture boundaries, migrations, and repeatable builds.
2. **Secure SSH Alpha** — Saved Hosts, Vault, Known Hosts, authentication matrix, and a GA-candidate terminal renderer.
3. **SFTP Beta** — two-way file workflows, directory transfers, safe queue/retry/resume, and integrity/failure testing.
4. **Operations Beta** — REPL/snippets, forwarding, jump/proxy, Multi results, production safeguards, and audit.
5. **Enterprise RC** — local policy, import, diagnostics, accessibility, signed MSIX, update, and deployment validation.
6. **GA** — P0/P1 complete, no unresolved Critical/High security issue, support policy published, and release evidence retained.

### Explicitly dropped scope

- FTP and FTPS
- Telnet, Serial, RDP, VNC, and X11 forwarding
- Mobile, macOS, and Linux applications
- Cloud accounts, cloud sync, Team Vault, RBAC, SSO, and terminal collaboration
- Built-in IDE, AI command generation for v1, plugin marketplace, and FIPS certification claims

Directory comparison, remote full-text search, and a portable signed archive remain post-GA candidates, not current commitments.

The security boundary is documented in [Security](../SECURITY.md), and the temporary terminal decision is documented in [ADR 0001](adr/0001-terminal-renderer.md).

---

## 한국어

### 정직한 제품 위치

Sutty는 일상적인 **로컬 터미널, SSH, SFTP, 재사용 명령, 다중 세션 운영**을 하나의 Windows 로컬 작업 공간에 통합합니다. 범위는 의도적으로 제한합니다.

- Windows의 SSH/SFTP 서버 운영 작업을 대상으로 합니다.
- 미래 GA의 목표이며 현재 Alpha가 완성됐다는 뜻은 아닙니다.
- FTP/FTPS, Telnet/Serial, RDP/VNC/X11, 클라우드·모바일·팀 협업은 포함하지 않습니다.

따라서 대체 목표는 Windows에서 널리 쓰이는 **SSH·SFTP 서버 관리 흐름**이며 관련 없는 클라이언트 범주의 모든 프로토콜·플랫폼이 아닙니다. 범위를 바꾸고 해당 호환성 게이트를 통과하기 전에는 전체 제품 동등성을 주장하지 않습니다.

Sutty는 로컬 우선 원칙을 유지합니다. 계정, 클라우드 백엔드, 팀 제어면을 만들지 않습니다.

### 제품 작업 화면

1. **Local** — Windows ConPTY 기반의 탭형 Windows PowerShell 세션
2. **Terminal** — 셸과 터미널 앱을 위한 지속 대화형 SSH PTY
3. **REPL** — 결과·시간·히스토리·재사용을 구조화하는 비대화형 명령
4. **Files** — 세션과 연동되는 SFTP 탐색과 안전한 전송
5. **Commands** — 향후 typed snippet/runbook으로 확장할 재사용 운영 템플릿
6. **Multi** — 운영 환경 보호와 구조화된 호스트별 결과를 갖춘 선택 세션 명령 실행

Terminal과 REPL은 서로 보완합니다. REPL은 Sutty의 차별점이며 터미널 호환성을 대신하지 않습니다.

### 제품 원칙

1. **호스트 하나, 논리 워크스페이스 하나.** SSH, Terminal, REPL, Files, 향후 tunnel은 한 사용자 세션 안에서 독립 상태를 표시합니다.
2. **컨트롤보다 실제 기능.** 비활성 또는 계획 상태의 컨트롤을 구현 완료로 홍보하지 않습니다.
3. **신원 경계에서는 기본 차단.** 알 수 없는 호스트키는 명시적 결정을 요구하고 변경된 키는 차단합니다.
4. **평문 비밀정보를 영구 저장하지 않음.** 비밀번호와 개인키 passphrase는 설정·히스토리·로그 메타데이터가 아닙니다.
5. **안전한 변경 작업.** 파괴적 파일 작업은 확인하고, 전송은 승격 전에 임시 단계에 쓰며, 충돌 대상을 조용히 덮어쓰지 않습니다.
6. **운영 대상은 사용자가 명시적으로 선택.** Multi는 선택 세션 0개로 시작하고 PROD 태그 대상은 추가 확인합니다.
7. **로컬 메타데이터도 민감함.** 호스트·사용자명·경로·태그·명령은 자격 증명은 아니지만 문서와 내보내기에서 개인 운영 데이터로 다룹니다.
8. **영어와 한국어를 함께 유지.** 두 언어가 모두 완성될 때까지 공식 제품 문서와 UI는 영어·한국어만 지원합니다.

### 현재 Alpha 기준선

현재 작업 트리에는 Windows ConPTY 기반 탭형 로컬 PowerShell, 실행 중 서버 측 크기 변경을 지원하는 실제 SSH.NET `ShellStream` PTY, 제한된 네이티브 VT 화면 모델, 기본 차단 known-host 검증, 분리된 SSH/Terminal/SFTP 상태, 구조화된 명령 결과, 실용적인 단일 파일 SFTP 작업, 명시적 퍼센트를 표시하는 간결한 전송 큐, Files/Terminal 경로 연동, 로컬 암호화 자격증명 보관소를 사용하는 저장 호스트, append-only 접속 결과, 즉시 반영 설정, 기본 선택 0개와 PROD 확인을 가진 Multi 화면이 있습니다.

이 기준선은 개발과 통제된 테스트에 사용할 수 있습니다. GA에 필요한 호환성·보안·접근성·패키징·대용량 전송·장시간 실행 게이트를 통과하지 않았습니다.

### GA 주장 전 릴리스 게이트

| 게이트 | 현재 남은 작업 |
| --- | --- |
| 터미널 호환성 | 임시 네이티브 렌더러를 승인된 로컬 xterm.js/WebView2 설계로 교체하고, bridge를 hardening하며, 현재 서버 PTY resize 계약을 유지하고 셸/TUI/입력/Unicode 매트릭스를 통과해야 합니다. |
| 안전한 Host와 자격 증명 | Saved Host 분리와 로컬 암호화 Vault는 구현됐으며 프로필 복제·일괄 UX, OTP·다중 prompt keyboard-interactive UI, 더 넓은 비밀정보 수명·redaction 테스트가 필요합니다. |
| 호스트 신원 | 현재 unknown/trusted/changed 강제를 유지하고, 변경 키 UX, rotation/관리, audit, 통합 검증을 추가해야 합니다. |
| SFTP 정확성 | 디렉터리 전송, 재귀 작업 안전성, 전체 충돌 정책, symlink 처리, 권한·오류 범위, 대용량 파일·깊은 경로 테스트가 필요합니다. |
| 전송 무결성 | 일시정지·재시도, 최종 크기 검증, 실패 복구, 취소·연결 종료가 기존 대상을 손상시키지 않는다는 증거가 필요합니다. |
| 명령 정확성 | REPL·Multi의 취소 UX와 실패 경로 회귀 검증을 완성해야 합니다. |
| 운영 데이터 | 첫 실행 DB를 비워 두고 현재 릴리스 산출물 검사를 계속 강제하며, migration·배포로 개발 데이터가 돌아오지 않도록 package clean install·upgrade 증거를 추가해야 합니다. |
| 릴리스 품질 | 통합·UI·보안 테스트, 성능·장시간 실행 증거, 의존성 검토, 지원 빌드 문서가 필요합니다. |

### 명세상 Windows 제품을 위한 P1 게이트

- Host 그룹·태그·즐겨찾기·프로필 검색, 워크스페이스 복원, 비밀 인자가 없는 명령줄 열기
- Windows OpenSSH Agent, PPK 가져오기, 점프 호스트, HTTP/SOCKS proxy, 제한 자동 재연결, 협상 알고리즘 정보
- 설정 가능한 제한, 재시도·재개·검증·재시작 복구를 갖춘 영속 전송 관리자
- 안전한 bind 경고를 포함한 Local·Remote·Dynamic 포트 포워딩
- 스트리밍 REPL 출력, typed snippet parameter, 영속·export 가능한 호스트별 Multi 결과, timeout, audit event
- OpenSSH 설정과 레거시 SSH/SFTP 프로필 가져오기, 암호화 Sutty 내보내기, 충돌 미리보기
- 로컬 기업 정책, 관리형 Host catalog, 진단/support bundle, redaction된 audit logging
- 키보드, High Contrast, 텍스트 확대, Narrator, 한국어/영어, Windows 11 x64/ARM64, 서명 MSIX, 업데이트, 기업 배포 검증

GA가 되려면 [요구사항 추적표](REQUIREMENTS.md)의 해당 P0·P1 항목에 구현·테스트·문서가 모두 연결되어야 합니다. 코드 경로 하나만으로 릴리스 게이트를 통과한 것으로 보지 않습니다.

### 개발 순서

1. **Foundation** — 지원 runtime/platform, 빈 production 데이터, 구조 경계, migration, 반복 가능한 빌드
2. **Secure SSH Alpha** — Saved Hosts, Vault, Known Hosts, 인증 매트릭스, GA 후보 터미널 렌더러
3. **SFTP Beta** — 양방향 파일 작업, 디렉터리 전송, 안전한 queue/retry/resume, 무결성·실패 테스트
4. **Operations Beta** — REPL/snippet, forwarding, jump/proxy, Multi 결과, 운영 환경 보호, audit
5. **Enterprise RC** — 로컬 정책, 가져오기, 진단, 접근성, 서명 MSIX, 업데이트, 배포 검증
6. **GA** — P0/P1 완료, 미해결 Critical/High 보안 문제 0건, 지원 정책 공개, 릴리스 증거 보존

### 명시적으로 제외한 범위

- FTP와 FTPS
- Telnet, Serial, RDP, VNC, X11 포워딩
- 모바일, macOS, Linux 앱
- 클라우드 계정·동기화, Team Vault, RBAC, SSO, 터미널 협업
- 내장 IDE, v1 AI 명령 생성, plugin marketplace, FIPS 인증 주장

Directory comparison, 원격 전체 텍스트 검색, 서명 portable archive는 GA 이후 후보이며 현재 약속이 아닙니다.

보안 경계는 [보안 문서](../SECURITY.md), 임시 터미널 결정은 [ADR 0001](adr/0001-terminal-renderer.md)에 정리했습니다.

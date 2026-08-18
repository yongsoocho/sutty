# Sutty Product Scope / Sutty 제품 범위

> Sutty is a local-first Windows SSH/SFTP operations workspace for individuals and small teams.
>
> Sutty는 개인과 소규모 팀을 위한 Windows local-first SSH/SFTP operations workspace입니다.

[English](#english) · [한국어](#한국어)

## English

### Product promise

Sutty lets a user finish everyday Windows server work in one local workspace: connect over SSH, use an interactive terminal, run structured commands, move files safely over SFTP, reuse command templates, and deliberately operate on selected sessions.

Local-first is a product boundary, not a temporary implementation detail:

- no Sutty account or cloud backend;
- no credential, command, or terminal-output upload;
- settings, history, saved hosts, trust records, and transfer recovery stay on the current Windows device;
- shareable definitions must be credential-free and require local credential binding after import.

### Supported product surfaces

| Surface | Alpha contract |
| --- | --- |
| Local | Tabbed Windows shell sessions through ConPTY. |
| Terminal | Persistent interactive SSH PTY with resize, keyboard, mouse, Unicode/IME, search, and clipboard handling. |
| REPL | Structured non-interactive commands with distinct input/output blocks, status, timing, and cancellation. |
| Files | Session-bound SFTP tree and safe upload/download workflows with explicit collision handling. |
| Commands | Local reusable command templates with parameter substitution. |
| Multi | Structured commands and SFTP operations sent only to explicitly selected sessions; default selection is zero. |
| Saved Hosts | Local profiles, tags, groups, environments, favorites, routes, tunnels, and optional encrypted credential references. |

An Alpha implementation is not a GA support claim. A capability becomes supported only when its normal, failure, cancellation, shutdown, migration, and relevant live-server paths have evidence.

### Explicitly unsupported

- FTP/FTPS, Telnet, Serial, RDP, VNC, and a built-in X server
- cloud accounts, cloud sync, hosted vaults, or a central control plane
- centralized identity, access policy, audit collection, or remote configuration enforcement
- terminal collaboration, mobile clients, and non-Windows desktop clients
- a built-in IDE, autonomous command generation, or a plugin marketplace

These items are outside the current product definition. They are not backlog promises.

### Non-negotiable safety rules

1. Unknown host keys require an explicit decision; changed keys fail closed.
2. Passwords, passphrases, OTP answers, private-key contents, and secret parameters are never plaintext persistent metadata.
3. Proxy, jump, or strict-route failure never falls back silently to Direct.
4. Existing destination files are not damaged by cancellation, disconnect, retry, resume, or checksum failure.
5. Multi starts with zero targets, shows the selected hosts, and requires extra confirmation for production-tagged targets.
6. Closing a session releases its Terminal, SFTP, tunnel, transfer, and callback resources.
7. UI controls represent working Core behavior; unfinished controls stay out of production UI.

### Feature admission test

A proposed feature enters the roadmap only when all answers are yes:

1. Does it improve safe SSH/SFTP work for an individual or small team on Windows?
2. Can it remain local-first without an account or control plane?
3. Can ownership, state, cancellation, shutdown, and error reporting be defined before UI work?
4. Can secrets and existing user data remain protected?
5. Can the normal and important failure paths be tested?

## 한국어

### 제품 약속

Sutty는 Windows에서 일상적인 서버 작업을 하나의 로컬 작업 공간에서 끝낼 수 있게 합니다. SSH 연결, 대화형 터미널, 구조화 명령 실행, 안전한 SFTP 파일 전송, 명령 템플릿 재사용, 사용자가 선택한 세션 대상 다중 작업이 핵심입니다.

Local-first는 임시 구현 방식이 아니라 제품 경계입니다.

- Sutty 계정과 클라우드 백엔드를 만들지 않습니다.
- 자격증명, 명령, 터미널 출력을 업로드하지 않습니다.
- 설정, 기록, 저장 Host, 신뢰 정보, 전송 복구 상태는 현재 Windows 장치에 둡니다.
- 공유 정의에는 자격증명을 넣지 않으며 가져온 뒤 각 사용자가 로컬 자격증명을 연결해야 합니다.

### 지원 제품 화면

| 화면 | Alpha 계약 |
| --- | --- |
| Local | ConPTY 기반 탭형 Windows shell 세션 |
| Terminal | 크기 변경, 키보드, 마우스, Unicode/IME, 검색, clipboard를 처리하는 지속형 SSH PTY |
| REPL | 입력·출력 블록, 상태, 실행 시간, 취소를 구분하는 구조화 비대화형 명령 |
| Files | 세션에 연결된 SFTP tree와 명시적 충돌 정책을 사용하는 안전한 업로드·다운로드 |
| Commands | 매개변수 치환을 지원하는 로컬 재사용 명령 템플릿 |
| Multi | 사용자가 명시적으로 선택한 세션에만 보내는 구조화 명령·SFTP 작업. 기본 선택은 0개 |
| Saved Hosts | 로컬 프로필, 태그, 그룹, 환경, 즐겨찾기, 경로, 터널, 선택형 암호화 자격증명 참조 |

Alpha에 코드가 있다는 사실은 GA 지원을 뜻하지 않습니다. 정상·실패·취소·종료·마이그레이션과 필요한 실서버 경로에 증거가 있어야 지원 완료로 판정합니다.

### 명시적 미지원

- FTP/FTPS, Telnet, Serial, RDP, VNC, 내장 X Server
- 클라우드 계정·동기화, 호스팅 Vault, 중앙 제어면
- 중앙 사용자 관리·접근 정책·감사 수집·원격 설정 강제
- 터미널 협업, 모바일 클라이언트, Windows 이외 데스크톱 클라이언트
- 내장 IDE, 자율 명령 생성, plugin marketplace

이 항목들은 현재 제품 정의 밖이며 향후 구현 약속이 아닙니다.

### 변경할 수 없는 안전 원칙

1. 알 수 없는 Host Key는 명시적 결정을 요구하고 변경된 Key는 기본 차단합니다.
2. 비밀번호, passphrase, OTP 답변, 개인키 내용, secret parameter를 평문 영구 메타데이터로 저장하지 않습니다.
3. Proxy·Jump·엄격 경로 실패 시 Direct로 조용히 우회하지 않습니다.
4. 취소·연결 종료·재시도·재개·checksum 실패가 기존 목적지 파일을 손상시키지 않아야 합니다.
5. Multi는 대상 0개로 시작하고 선택 Host를 보여주며 운영 태그 대상은 추가 확인합니다.
6. 세션 종료 시 Terminal, SFTP, tunnel, transfer, callback 리소스를 모두 정리합니다.
7. UI control은 실제 Core 동작을 나타내며 미완성 control은 production UI에 두지 않습니다.

### 기능 채택 검사

다음 질문에 모두 예라고 답할 수 있을 때만 로드맵에 추가합니다.

1. Windows 개인·소규모 팀의 안전한 SSH/SFTP 작업을 개선하는가?
2. 계정이나 중앙 제어면 없이 local-first로 유지할 수 있는가?
3. UI보다 먼저 소유권, 상태, 취소, 종료, 오류 보고를 정의할 수 있는가?
4. 비밀정보와 기존 사용자 데이터를 보호할 수 있는가?
5. 정상 경로와 중요한 실패 경로를 테스트할 수 있는가?

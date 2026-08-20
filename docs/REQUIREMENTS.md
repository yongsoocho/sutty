# Requirements Traceability / 요구사항 추적표

This document maps the 2026-08-11 product specification to the current Sutty working tree. It is a development trace, not a GA certification.

이 문서는 2026-08-11 제품 명세와 현재 Sutty 작업 트리를 연결합니다. 개발 추적표이며 GA 인증이 아닙니다.

[English status definitions](#status-definitions--상태-정의) · [한국어 상태 정의](#status-definitions--상태-정의) · [Product Direction / 제품 방향](PRODUCT_DIRECTION.md) · [Terminal ADR / 터미널 ADR](adr/0001-terminal-renderer.md) · [Security / 보안](../SECURITY.md)

## Status definitions / 상태 정의

| Status | English | 한국어 |
| --- | --- | --- |
| **Implemented** | An end-to-end Alpha baseline exists and has focused evidence. This does not replace GA matrix testing. | 종단 간 Alpha 기준선과 집중형 증거가 있습니다. GA 매트릭스 테스트를 대신하지 않습니다. |
| **Partial** | Some code path exists, but the specification or acceptance criteria are incomplete or unverified. | 일부 코드 경로가 있지만 명세 또는 인수 기준이 미완성이거나 검증되지 않았습니다. |
| **Planned** | No usable end-to-end implementation exists in the current product. | 현재 제품에 사용할 수 있는 종단 간 구현이 없습니다. |
| **Drop** | Intentionally outside the approved product scope. | 승인된 제품 범위에서 의도적으로 제외했습니다. |

The evidence baseline is the source tree, not disabled UI or future-looking comments. Primary evidence areas are [SSH sessions](../src/sutty.Core/Sessions/SshNetSession.cs), [host-key security](../src/sutty.Core/Security), [terminal renderer](../src/sutty.UI/Controls/TerminalRendererControl.cs), [terminal/REPL UI](../src/sutty.UI/Views/SessionView.xaml.cs), [SFTP services](../src/sutty.Core/Sftp), [Files UI](../src/sutty.UI/Views/FileTreePanel.xaml.cs), [local command/history storage](../src/sutty.Command), [Multi selection](../src/sutty.UI/Views/MultiSessionGrid.xaml.cs), and [focused self-tests](../tests).

증거 기준은 비활성 UI나 미래형 주석이 아니라 실제 소스 트리입니다. 주요 증거 영역은 위 링크의 SSH, 호스트키, 터미널, SFTP, 로컬 저장소, Multi, self-test입니다.

## Production data / 운영 데이터

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| DATA-001 | P0 | Implemented | Fresh command/history tables start empty; automatic production seed was removed.<br>새 명령·히스토리 테이블은 빈 상태로 시작하며 production 자동 seed를 제거했습니다. |
| DATA-002 | P0 | Implemented | Production session creation uses `SshNetSession`; mock session/SFTP source is absent from the production graph.<br>Production 세션 생성은 `SshNetSession`만 사용하며 mock session/SFTP 소스는 production graph에 없습니다. |
| DATA-003 | P0 | Implemented | The production connection model no longer exposes `UseMockSession`; a transaction removes only legacy mock-marked rows while preserving real history, pins, and tags.<br>Production 연결 모델에서 `UseMockSession`을 제거했으며 transaction은 실제 history·pin·태그를 보존하고 legacy mock 표시 행만 제거합니다. |
| DATA-004 | P1 | Planned | There is no opt-in onboarding flow for sample snippets.<br>샘플 snippet을 사용자가 선택해 설치하는 onboarding 흐름이 없습니다. |
| DATA-005 | P1 | Implemented | Windows CI publishes an unsigned x64 artifact and rejects Mock/Demo/Seed markers and development host-address patterns before upload.<br>Windows CI가 unsigned x64 산출물을 만들고 업로드 전에 Mock/Demo/Seed 표시와 개발용 Host 주소 패턴을 차단합니다. |

## Accessibility / 접근성

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| UX-A11Y-001 | P1 | Partial | Tab, left-navigation, new-tab, Settings, copy, and paste shortcuts exist with focus states; complete tab order, access-key, dialog, and E2E coverage is missing.<br>탭·왼쪽 탐색·새 탭·설정·복사·붙여넣기 단축키와 focus 상태가 있지만 전체 tab order·access key·dialog·E2E 검증이 없습니다. |
| UX-A11Y-002 | P1 | Partial | Theme resources and scalable WinUI controls exist; High Contrast and 200% text-scale acceptance are unverified.<br>테마 리소스와 확대 가능한 WinUI 컨트롤은 있지만 High Contrast·200% 텍스트 확대 인수 검증이 없습니다. |
| UX-A11Y-003 | P1 | Partial | Some controls expose Automation names; full Narrator navigation and HelpText coverage is missing.<br>일부 컨트롤에 Automation name이 있지만 전체 Narrator 탐색과 HelpText가 부족합니다. |
| UX-A11Y-004 | P1 | Partial | Several states combine text and color; environment and warning semantics are not consistently covered.<br>여러 상태가 텍스트와 색을 함께 사용하지만 환경·경고 의미가 일관되게 적용되지 않았습니다. |
| UX-A11Y-005 | P2 | Planned | Reduce Motion is not implemented or tested.<br>Reduce Motion을 구현하거나 테스트하지 않았습니다. |

## Hosts and sessions / Host와 세션

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| HOST-001 | P0 | Partial | Explicit Saved Hosts support create/update, delete, group, environment, tags, favorites, opaque credential references, and credential-free OpenSSH, Windows saved-session, legacy INI, and SFTP Site Manager XML import. Duplicate and bulk-management UX remain planned.<br>명시적 저장 호스트는 생성·수정·삭제·그룹·환경·태그·즐겨찾기·불투명 자격증명 참조와 자격 증명 없는 OpenSSH·Windows 저장 세션·레거시 INI·SFTP Site Manager XML 가져오기를 지원하며 복제·일괄 관리 UX는 계획 상태입니다. |
| HOST-002 | P1 | Partial | Saved Hosts and append-only history are searched together; responsive cards, groups, environments, and favorites exist, while 1,000-host performance evidence is incomplete.<br>저장 호스트와 append-only 히스토리를 함께 검색하며 반응형 카드·그룹·환경·즐겨찾기가 있지만 1,000 Host 성능 증거는 미완성입니다. |
| HOST-003 | P0 | Implemented | Every completed connection attempt appends success, failure, or cancellation, a bounded diagnostic code, and duration without storing secrets.<br>완료된 모든 연결 시도는 비밀정보 없이 성공·실패·취소, 제한된 진단 코드, 소요 시간을 새 행으로 추가합니다. |
| HOST-004 | P0 | Partial | SSH, Terminal, and SFTP state are independent and SFTP failure preserves SSH; configured forwarding has session-bound lifecycle, but dedicated tunnel state/management UI is incomplete.<br>SSH·Terminal·SFTP 상태는 분리되고 SFTP 실패 시 SSH를 유지하며 설정된 forwarding은 세션 수명주기를 따르지만 전용 tunnel 상태·관리 UI는 미완성입니다. |
| HOST-005 | P1 | Implemented | A credential-free atomic workspace snapshot remembers up to 16 local tabs and Saved Host ids. Startup restoration is optional, reconnect confirmation is enabled by default, missing profiles are skipped, and terminal commands are never stored or replayed.<br>비밀정보 없는 원자적 Workspace snapshot이 최대 16개의 로컬 탭과 저장 Host ID를 기억합니다. 시작 복원은 선택형이고 재연결 확인이 기본 활성화되며, 사라진 프로필은 건너뛰고 터미널 명령은 저장하거나 재실행하지 않습니다. |
| HOST-006 | P1 | Partial | Host canonicalization covers DNS, IPv4, IPv6, and nonstandard ports; the required connection matrix is unverified.<br>DNS·IPv4·IPv6·비표준 port를 정규화하지만 필수 연결 매트릭스를 검증하지 않았습니다. |
| HOST-007 | P1 | Implemented | `sutty.UI.exe --host <Saved Host id or exact name>` opens an existing credential-free profile through the normal secure connection flow. Credential arguments are rejected, quoted names are supported, and explicit launch windows do not overwrite the normal Workspace snapshot.<br>`sutty.UI.exe --host <저장 Host ID 또는 정확한 이름>`이 기존 비밀정보 없는 프로필을 정상 보안 연결 흐름으로 엽니다. 자격증명 인자는 거부하고 따옴표 이름을 지원하며 명시적 실행 창은 일반 Workspace snapshot을 덮어쓰지 않습니다. |

## SSH and authentication / SSH와 인증

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| SSH-001 | P0 | Partial | SSH-2 connect, cancellation, and gate-drained disconnect exist. An unexpected primary-transport error retires the current client generation, immediately clears negotiated information, performs best-effort terminal/SFTP/forwarding/route cleanup, and ends in `Failed` without racing an explicit disconnect. Timeout and live fault compatibility acceptance is incomplete.<br>SSH-2 연결·취소·gate-drain 종료를 지원합니다. 예기치 않은 주 transport 오류는 현재 client 세대를 폐기하고 협상 정보를 즉시 지운 뒤 terminal·SFTP·forwarding·route를 최선 방식으로 정리해 명시적 연결 종료와 경합하지 않고 `Failed`로 끝납니다. Timeout·실환경 fault 호환성 인수는 미완성입니다. |
| SSH-002 | P0 | Partial | Password, private key, and repeated multi-prompt keyboard-interactive OTP/MFA flows are implemented; the representative server/provider compatibility matrix is incomplete.<br>비밀번호·개인키와 반복 가능한 다중 prompt keyboard-interactive OTP/MFA 흐름을 구현했지만 대표 서버·인증 제공자 호환성 매트릭스는 미완성입니다. |
| SSH-003 | P1 | Partial | Windows OpenSSH Agent/Pageant identities are exposed through an in-tree, upstream-pinned adapter compiled against SSH.NET 2026, with a friendly unavailable-service failure and an opt-in required live self-test; live Agent/key acceptance remains.<br>Windows OpenSSH Agent/Pageant identity를 SSH.NET 2026에 맞춰 직접 빌드하는 upstream 고정 adapter로 사용하며 서비스 미실행 안내와 선택형 필수 실환경 self-test가 있지만 실제 Agent·키 인수는 남아 있습니다. |
| SSH-004 | P1 | Partial | SSH.NET loads OpenSSH, PEM, PKCS#8, and PPK v2/v3 keys; encrypted/algorithm format acceptance is incomplete.<br>SSH.NET으로 OpenSSH·PEM·PKCS#8·PPK v2/v3 키를 읽지만 암호화·알고리즘별 형식 인수는 미완성입니다. |
| SSH-005 | P0 | Implemented | Unknown/trusted/changed states fail closed, both SSH and SFTP verify, changed keys block, and focused security self-tests cover persistence and concurrency.<br>Unknown/trusted/changed 상태를 기본 차단으로 처리하고 SSH·SFTP 모두 검증하며 변경 키 차단과 저장·동시성 self-test가 있습니다. |
| SSH-006 | P1 | Partial | SSH Jump creates a separately host-key-verified jump connection and loopback forwarding shared by target SSH/SFTP; live topology and failure lifecycle tests remain.<br>SSH Jump는 별도 host-key 검증을 거친 jump 연결과 대상 SSH/SFTP가 공유하는 loopback forwarding을 만들지만 실제 topology·실패 수명주기 검증은 남아 있습니다. |
| SSH-007 | P1 | Partial | Direct, HTTP CONNECT, SOCKS4, SOCKS5, SSH Jump, and external ProxyCommand routes create real connections and the same resolved route is used for SFTP. Unsupported/corrupt saved routes expose distinct fail-closed states and recovery codes instead of becoming usable Direct routes. ProxyCommand placeholders are validated and quoted, unsafe endpoint substitutions are rejected, and the exact expanded command requires confirmation. Explicit proxy-DNS evidence and the live route matrix are incomplete.<br>Direct·HTTP CONNECT·SOCKS4·SOCKS5·SSH Jump·외부 ProxyCommand 경로가 실제 연결을 만들고 SFTP도 같은 resolved route를 사용합니다. 지원하지 않거나 손상된 저장 경로는 사용 가능한 Direct가 되지 않고 서로 다른 fail-closed 상태·복구 코드를 표시합니다. ProxyCommand 치환값을 검증·인용하고 위험한 endpoint 치환을 거부하며 실제 실행 명령을 명시적으로 확인받습니다. 명시적 proxy-DNS 증거와 실환경 경로 매트릭스는 미완성입니다. |
| SSH-008 | P1 | Partial | Keepalive is applied per connection; automatic reconnect and replay safety UX are missing.<br>연결별 keepalive는 적용하지만 자동 재연결과 replay 안전 UX가 없습니다. |
| SSH-009 | P1 | Partial | The primary SSH transport exposes an in-memory, credential-free snapshot of server/client identification, KEX, verified host-key algorithm and SHA-256 fingerprint, and both cipher/MAC/compression directions in an accessible read-only/copy flyout. Connecting no longer runs automatic banner or home-directory discovery commands. Live fingerprint, reconnect, no-exec, and indirect-route acceptance remains pending.<br>주 SSH 전송의 서버·클라이언트 식별, KEX, 검증된 호스트 키 알고리즘과 SHA-256 지문, 양방향 cipher·MAC·압축을 메모리 내 비밀정보 없는 snapshot으로 만들고 접근 가능한 읽기 전용·복사 flyout에 표시합니다. 연결만으로 자동 banner나 홈 디렉터리 탐색 명령을 실행하지 않습니다. 실제 지문·재연결·무명령 연결·간접 경로 인수는 남아 있습니다. |
| SSH-010 | P0 | Planned | There is no explicit product policy or tested override model for legacy algorithms.<br>Legacy 알고리즘을 위한 명시적 제품 정책과 검증된 override 모델이 없습니다. |

## Credential security / 자격증명 보안

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| CRED-001 | P0 | Implemented | Credential storage is opt-in. A random AES-256 key is protected for the current Windows user, each record uses authenticated AES-GCM encryption, and SQLite stores only an opaque reference.<br>자격증명 저장은 선택형입니다. 무작위 AES-256 키를 현재 Windows 사용자에게 보호하고 각 레코드는 인증된 AES-GCM 암호화를 사용하며 SQLite에는 불투명 참조만 저장합니다. |
| CRED-002 | P0 | Partial | Passwords and key passphrases are excluded from settings, history, profile rows, and crash messages; the connection object is cleared after each attempt and tamper/plaintext self-tests exist. Broader memory-lifetime and UI-automation review remains.<br>비밀번호와 키 암호는 설정·히스토리·프로필 행·충돌 메시지에서 제외하며 연결 시도 뒤 객체 값을 지우고 변조·평문 self-test를 수행합니다. 더 넓은 메모리 수명·UI 자동화 검토는 남아 있습니다. |

## Terminal / 터미널

The package-local renderer is integrated, but GA requires the remaining compatibility and security matrix. See [ADR 0001](adr/0001-terminal-renderer.md).

패키지 내부 렌더러는 통합됐지만 GA에는 남은 호환성·보안 매트릭스가 필요합니다. [ADR 0001](adr/0001-terminal-renderer.md)을 확인하세요.

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| TERM-001 | P0 | Partial | Persistent SSH `ShellStream` PTY and local ConPTY both use the package-local xterm.js renderer; the required vim/tmux/htop/sudo compatibility matrix has not passed.<br>지속 SSH `ShellStream` PTY와 로컬 ConPTY가 모두 패키지 내부 xterm.js 렌더러를 사용하지만 필수 vim/tmux/htop/sudo 호환 매트릭스를 통과하지 않았습니다. |
| TERM-002 | P0 | Partial | xterm.js provides ANSI/VT SGR color/style, cursor/erase/scrolling, alternate screen, device responses, and mouse modes. The representative shell/TUI and malicious-sequence matrix is incomplete.<br>xterm.js가 ANSI/VT SGR 색·스타일, 커서·지우기·스크롤, 대체 화면, 장치 응답, 마우스 모드를 제공하지만 대표 셸·TUI 및 악성 sequence 매트릭스는 미완성입니다. |
| TERM-003 | P0 | Partial | xterm.js owns control, navigation, function-key, application-mode, and IME input. Global tab/navigation/settings shortcuts and Ctrl/Shift+Insert are implemented; the exhaustive Windows keyboard-layout matrix is missing.<br>xterm.js가 제어·탐색·기능키·application mode·IME 입력을 처리하며 전역 탭·내비게이션·설정 단축키와 Ctrl/Shift+Insert를 구현했습니다. 전체 Windows 키보드 배열 매트릭스는 남아 있습니다. |
| TERM-004 | P0 | Partial | Runtime server-side resize uses SSH.NET's public `ChangeWindowSize` API; shell/TUI and resize-stress integration evidence remains incomplete.<br>실행 중 서버 측 크기 변경은 SSH.NET 공개 `ChangeWindowSize` API를 사용하지만 셸/TUI·resize stress 통합 증거는 아직 완성되지 않았습니다. |
| TERM-005 | P1 | Partial | xterm.js provides incremental UTF-8 rendering, IME composition, wide/emoji/combining cells, and a screen-reader mode. Korean IME and Unicode edge-case acceptance evidence is incomplete.<br>xterm.js가 점진적 UTF-8 표시, IME 조합, 넓은 문자·이모지·결합 문자 셀, 화면 읽기 모드를 제공하지만 한글 IME와 Unicode 경계 사례 인수 증거는 미완성입니다. |
| TERM-006 | P1 | Partial | Bounded configurable scrollback, selection, Ctrl+Insert copy, Shift+Insert paste, bracketed-paste-aware xterm input, and Ctrl+F search exist; 100,000-line, latency, and long-session evidence is missing.<br>제한·설정 가능한 scrollback, 선택, Ctrl+Insert 복사, Shift+Insert 붙여넣기, bracketed paste를 인식하는 xterm 입력, Ctrl+F 검색이 있지만 100,000줄·지연·장시간 세션 증거는 남아 있습니다. |
| TERM-007 | P1 | Planned | Opt-in transcript storage and retention are not implemented.<br>선택형 transcript 저장과 보존 정책을 구현하지 않았습니다. |
| TERM-008 | P1 | Implemented | REPL cells classify JSON/YAML syntax plus critical/error and warning text with bounded parsing. Recent and saved commands are suggested without execution and accepted with Right Arrow or optional Tab.<br>REPL 셀은 제한된 파싱으로 JSON/YAML 문법과 critical/error·warning 텍스트를 분류합니다. 최근·저장 명령은 실행 없이 제안되며 오른쪽 화살표 또는 선택형 Tab으로 적용합니다. |

## SFTP file system / SFTP 파일 시스템

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| SFTP-001 | P0 | Partial | Remote path navigation, refresh, lazy loading, bounded flattened tree enumeration, and bounded recursive filename search exist; symlinks are listed but not traversed. Broad permission/error and live-server search testing is missing.<br>원격 경로 이동·새로고침·지연 로딩·제한된 평면 tree enumeration·제한된 재귀 파일명 검색이 있고 symlink는 표시하되 따라가지 않습니다. 폭넓은 권한·오류·실서버 검색 검증은 남아 있습니다. |
| SFTP-002 | P0 | Partial | File and recursive directory upload/download, including empty folders, work; 100GB and full Unicode/deep-path/live-server acceptance are missing.<br>파일과 빈 폴더를 포함한 재귀 디렉터리 업로드·다운로드가 동작하지만 100GB·전체 Unicode/깊은 경로·실서버 인수는 남아 있습니다. |
| SFTP-003 | P0 | Partial | Same-parent rename, cross-directory file/folder move without overwrite, file delete, mkdir, and preview-confirmed safe recursive delete exist. Recursive removal rejects remote root and never follows symbolic links; live-server permission and error-matrix evidence remain.<br>같은 상위 디렉터리 내 이름 변경, 덮어쓰기 없는 파일·폴더 간 디렉터리 이동, 파일 삭제, mkdir, 미리보기 확인형 안전 재귀 삭제를 지원합니다. 재귀 삭제는 원격 root를 거부하고 symbolic link를 따라가지 않으며 실서버 권한·오류 매트릭스 증거는 남아 있습니다. |
| SFTP-004 | P1 | Partial | The remote context menu supports octal Unix permission changes, optionally recursive, and skips symbolic links. Server compatibility and authorization acceptance evidence remain.<br>원격 컨텍스트 메뉴에서 8진 Unix 권한 변경과 선택형 재귀 적용을 지원하고 symbolic link는 건너뜁니다. 서버 호환성·권한 인수 증거는 남아 있습니다. |
| SFTP-005 | P0 | Partial | Ask, overwrite, skip, rename, and newer-only are durable per-job policies for file and recursive directory paths; interactive jobs resolve Ask before starting and unattended Ask fails closed. Full live-server collision and race-condition coverage remains.<br>Ask·덮어쓰기·건너뛰기·이름 변경·새 파일일 때만 교체를 파일·재귀 디렉터리 경로의 job 단위 영속 정책으로 지원합니다. 대화형 작업은 시작 전에 Ask를 해소하고 무인 Ask는 실패 종료하며, 전체 실서버 충돌·race 검증은 남아 있습니다. |
| SFTP-006 | P1 | Planned | Local/remote synchronized browsing is unavailable.<br>Local/remote 동기 탐색을 지원하지 않습니다. |
| SFTP-007 | P2 | Planned | Directory comparison is unavailable.<br>Directory comparison을 지원하지 않습니다. |

## Transfer manager / 전송 관리자

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| XFER-001 | P0 | Partial | A credential-free atomic job store and per-panel bounded workers exist. Jobs and target states survive restart, while configurable global/per-host concurrency and a unified manager remain incomplete.<br>자격증명 없는 atomic job 저장소와 패널별 제한 worker가 있습니다. Job·대상 상태는 재시작 후 복원되지만 설정 가능한 전역·Host별 동시성과 통합 관리자는 미완성입니다. |
| XFER-002 | P0 | Partial | Progress, speed, ETA, direction, and state are visible; refresh throttling and large-file evidence are missing.<br>진행률·속도·ETA·방향·상태는 표시하지만 갱신 제한과 대용량 파일 증거가 없습니다. |
| XFER-003 | P0 | Partial | Queued/running cancellation, durable pause/resume that preserves checkpoints, temporary-file retention/cleanup, and configurable transient retry exist. Stalled-call interruption beyond transport cancellation and live failure evidence remain.<br>대기·실행 취소, checkpoint를 보존하는 영속 일시정지·재개, 임시 파일 유지·정리, 설정 가능한 일시 오류 재시도가 있으며 transport 취소를 넘는 stalled-call 중단과 실환경 실패 증거는 남아 있습니다. |
| XFER-004 | P0 | Partial | Upload uses deterministic remote partials, checkpointed resume, checksum-before-promotion, backup/promotion, rollback, and no pre-delete; large fault-injection evidence remains.<br>업로드는 결정적인 원격 partial, checkpoint resume, 승격 전 checksum, backup/promotion, rollback, 선삭제 방지를 사용하지만 대용량 fault-injection 증거는 남아 있습니다. |
| XFER-005 | P1 | Partial | Upload/download resume from validated partial offsets and source metadata; interrupted live-server acceptance is incomplete.<br>검증된 partial offset과 원본 metadata에서 업로드·다운로드를 이어받지만 실서버 중단 인수는 미완성입니다. |
| XFER-006 | P1 | Implemented | Non-secret checkpoints and job/target state survive restart. Files and Multi discover matching incomplete work and require an explicit resume action; abandoned running work becomes interrupted and successful targets stay complete.<br>비밀정보 없는 checkpoint와 job·대상 상태가 재시작 뒤 유지됩니다. Files·Multi가 일치하는 미완료 작업을 찾아 명시적 재개를 요구하며 중단된 실행 작업은 interrupted로 바뀌고 성공 대상은 완료 상태를 유지합니다. |
| XFER-007 | P1 | Partial | Safe mode compares local and remote SHA-256 before final promotion; fast mode performs final-size verification without rereading the remote file. Safe mode is the default, while large-file performance and incompatible-server evidence remain.<br>안전 모드는 최종 승격 전에 로컬·원격 SHA-256을 비교하고 빠른 모드는 원격 파일 재읽기 없이 최종 크기를 검증합니다. 안전 모드가 기본값이며 대용량 성능·비호환 서버 증거는 남아 있습니다. |
| XFER-008 | P1 | Partial | Explicitly checked sessions support 1→N upload and N→1 download with preflight target/source/destination/policy review, independent progress/result state, deterministic destination isolation, and failed/incomplete-only retry. Live 16-target and cancellation evidence remains.<br>명시적으로 체크한 세션은 실행 전 대상·원본·대상 경로·충돌 정책 검토와 함께 1→N 업로드·N→1 다운로드, 독립 진행률·결과, 결정적 목적지 분리, 실패·미완료 대상만 재시도를 지원하지만 실제 16대·취소 검증은 남아 있습니다. |

## Port forwarding / 포트 포워딩

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| TUN-001 | P1 | Partial | Local, remote, and dynamic forwarding rules are implemented and owned by the SSH session; the live interoperability matrix is incomplete.<br>Local·Remote·Dynamic forwarding 규칙을 구현하고 SSH 세션 수명주기에 연결했지만 실제 상호운용 매트릭스는 미완성입니다. |
| TUN-002 | P1 | Partial | Saved hosts persist credential-free local/remote/dynamic forwarding rules and restore them into the connection path automatically. Live bind, reconnect, and migration evidence remains.<br>저장 Host가 자격증명 없는 Local·Remote·Dynamic forwarding 규칙을 저장하고 연결 경로에 자동 복원하지만 실제 bind·재접속·migration 증거는 남아 있습니다. |
| TUN-003 | P1 | Implemented | Non-loopback local, remote, or dynamic bind addresses are classified as externally exposed, shown in a default-cancel high-risk confirmation, and recorded as warning diagnostics.<br>loopback이 아닌 Local·Remote·Dynamic bind 주소를 외부 노출로 분류하고 기본 취소인 고위험 확인창에 표시하며 warning 진단으로 기록합니다. |
| TUN-004 | P2 | Partial | Pre-connect rules create temporary session-scoped tunnels; a post-connect tunnel manager is unavailable.<br>연결 전 규칙으로 세션 범위 임시 tunnel을 만들지만 연결 후 tunnel 관리자는 없습니다. |

## REPL, snippets, and Multi / REPL, snippet, Multi

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| CMD-001 | P0 | Partial | Core results preserve stdout, stderr, exit status/signal, start, duration, and cancellation; full UI cancellation and regression coverage are incomplete.<br>Core 결과는 stdout·stderr·exit status/signal·시작·소요 시간·취소를 보존하지만 전체 UI 취소와 회귀 검증이 미완성입니다. |
| CMD-002 | P1 | Planned | REPL output is displayed after command completion, not streamed.<br>REPL 출력은 스트리밍이 아니라 명령 완료 뒤 표시됩니다. |
| CMD-003 | P1 | Partial | Positional `$1`/`$2` template substitution exists; named typed/validated/secret parameters do not.<br>위치형 `$1`/`$2` 치환은 있지만 이름형 typed/validated/secret parameter는 없습니다. |
| CMD-004 | P1 | Implemented | New sessions are unselected by default and only an explicit prior choice is preserved.<br>새 세션은 기본 미선택이며 사용자가 명시한 기존 선택만 유지합니다. |
| CMD-005 | P0 | Partial | PROD-tagged targets require a confirmation with target count and command preview; environment distribution, typed confirmation, local policy, and local activity records are missing.<br>PROD 태그 대상은 대상 수·명령 미리보기 확인을 거치지만 환경 분포·확인 문구 입력·로컬 정책·로컬 활동 기록이 없습니다. |
| CMD-006 | P1 | Partial | Each host uses a structured result with stdout, stderr, exit code/signal, and duration; the UI shows a truncated combined-output preview plus exit/signal, while durable detail, export, timeout, and local activity records are missing.<br>각 Host는 stdout·stderr·exit code/signal·소요 시간을 가진 구조화 결과를 사용하고 UI는 잘린 합산 출력과 exit/signal을 표시하지만 영속 상세·export·timeout·로컬 활동 기록은 없습니다. |
| CMD-007 | P0 | Implemented | Multi uses non-interactive command execution and does not broadcast raw terminal keystrokes.<br>Multi는 비대화형 명령 실행을 사용하며 raw terminal keystroke를 broadcast하지 않습니다. |

## Import, export, and sharing / 가져오기, 내보내기, 공유

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| IMP-001 | P1 | Implemented | OpenSSH config import handles concrete hosts, identity files, jump/proxy routes, and forwarding rules; wildcard entries are skipped with warnings and secrets are never imported.<br>OpenSSH config 가져오기는 구체 Host·identity file·jump/proxy route·forwarding 규칙을 처리하며 wildcard는 경고와 함께 건너뛰고 비밀정보를 가져오지 않습니다. |
| IMP-002 | P1 | Partial | Windows saved-session registry profiles are imported without credentials, including proxy and forwarding metadata. Real-machine format/version acceptance remains.<br>Windows 저장 세션 registry profile을 자격증명 없이 proxy·forwarding metadata와 함께 가져오지만 실제 PC의 형식·버전 인수 검증은 남아 있습니다. |
| IMP-003 | P1 | Partial | Legacy INI host profiles are parsed without credentials and deduplicated before saving. Broader product/version fixtures remain.<br>레거시 INI Host profile을 자격증명 없이 분석하고 저장 전 중복 제거하지만 더 넓은 제품·버전 fixture 검증은 남아 있습니다. |
| IMP-004 | P1 | Planned | Credential-free Sutty pack export, import preview, conflict handling, schema versioning, and per-user credential binding are unavailable. Export must reject secret material rather than trying to serialize it.<br>자격증명 없는 Sutty pack 내보내기, 가져오기 미리보기, 충돌 처리, schema version, 사용자별 자격증명 연결을 지원하지 않습니다. 내보내기는 secret을 직렬화하려 하지 말고 강제로 거부해야 합니다. |

## Host-key security / 호스트키 보안

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| SEC-HK-001 | P0 | Implemented | Unknown keys default to rejection and expose full endpoint, algorithm, SHA-256 fingerprint, Trust and save, Connect once, and Cancel.<br>알 수 없는 키는 기본 거부하며 전체 endpoint·algorithm·SHA-256 지문과 신뢰하고 저장·이번만 연결·취소를 제공합니다. |
| SEC-HK-002 | P0 | Implemented | Changed keys are blocked; the error retains trusted and presented algorithms/fingerprints and cannot be overridden by Trust once.<br>변경 키는 차단하며 오류에 기존·제시 algorithm/fingerprint가 있고 이번만 연결로 우회할 수 없습니다. |
| SEC-HK-003 | P1 | Planned | A saved-host strict-trust option cannot yet disable Connect once for that host.<br>저장 Host별 엄격 신뢰 옵션으로 해당 Host의 이번만 연결을 비활성화할 수 없습니다. |
| SEC-HK-004 | P1 | Planned | Known-host management, rotation workflow, and local security activity records are unavailable.<br>Known-host 관리·rotation 흐름·로컬 보안 활동 기록이 없습니다. |

## Support and evidence governance / 지원·증거 거버넌스

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| SUPPORT-001 | P0 | Implemented | [Supported environments](SUPPORTED_ENVIRONMENTS.md) defines the only four support states—**Implemented**, **Live Validated**, **Released**, and **Unsupported**—and maps Windows/architecture, server/authentication/key, route/forwarding, terminal, and SFTP boundaries without inventing live results. No current row is promoted beyond **Implemented** without a conforming bundle.<br>[지원 환경](SUPPORTED_ENVIRONMENTS.md)은 유일한 네 지원 상태인 **Implemented**, **Live Validated**, **Released**, **Unsupported**를 정의하고 가짜 실환경 결과 없이 Windows/architecture, server/authentication/key, route/forwarding, terminal, SFTP 경계를 연결합니다. 규격에 맞는 bundle 없이는 현재 행을 **Implemented**보다 높이지 않습니다. |
| EVID-001 | P0 | Implemented | [Evidence schema](evidence/EVIDENCE_SCHEMA.md) defines the exact flat `manifest.yml` allowlist, required redacted `summary.json`, immutable result handling, and fixture-validated rejection boundary. It creates no live `Pass` record; a human-reviewed real bundle is still required for every live claim.<br>[증거 스키마](evidence/EVIDENCE_SCHEMA.md)는 정확한 평면 `manifest.yml` allowlist, 필수 redacted `summary.json`, 불변 결과 처리, fixture 기반 거부 경계를 정의합니다. 실환경 `Pass` 기록을 만들지 않으며 모든 실환경 주장에는 사람이 검토한 실제 bundle이 필요합니다. |

## Non-functional requirements / 비기능 요구사항

| ID | Area / 영역 | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| NFR-001 | Startup / 시작 | Planned | No 1,000-host P95 cold-start benchmark exists.<br>1,000 Host 기준 P95 cold-start benchmark가 없습니다. |
| NFR-002 | Terminal latency / 터미널 지연 | Planned | No bridge/renderer P95 latency benchmark exists.<br>Bridge/renderer P95 지연 benchmark가 없습니다. |
| NFR-003 | Session scale / 세션 규모 | Partial | UI enforces a 16-session ceiling; one-hour 16-session/8-terminal load evidence is missing.<br>UI는 최대 16세션을 제한하지만 16세션·8터미널 1시간 부하 증거가 없습니다. |
| NFR-004 | Memory / 메모리 | Planned | Output queues are bounded, but the specified eight-session memory and leak target is unverified.<br>출력 queue는 제한되지만 명세의 8세션 메모리·누수 목표를 검증하지 않았습니다. |
| NFR-005 | File scale / 파일 규모 | Partial | Transfer sizes use 64-bit values and support cancellation; 100GB and 100,000-entry virtualization evidence is missing.<br>전송 크기는 64-bit이고 취소를 지원하지만 100GB·100,000 entry virtualization 증거가 없습니다. |
| NFR-006 | Availability / 가용성 | Partial | SSH/Terminal/SFTP states and failures are isolated; systematic network fault injection is missing.<br>SSH·Terminal·SFTP 상태와 실패는 분리되지만 체계적 network fault injection이 없습니다. |
| NFR-007 | Accessibility / 접근성 | Partial | Basic WinUI accessibility exists; the required checklist and automation do not.<br>기본 WinUI 접근성은 있지만 필수 checklist와 자동화가 없습니다. |
| NFR-008 | Localization / 지역화 | Partial | Korean/English localization infrastructure exists; hardcoded user-visible strings and complete language coverage still need review.<br>한국어/영어 지역화 구조는 있지만 사용자 표시 hardcoded 문자열과 완전한 언어 적용 범위를 점검해야 합니다. |
| NFR-009 | Supportability / 지원성 | Partial | Bounded in-memory connection diagnostics, structured severities, and per-session correlation metadata exist. A user-created redacted support bundle with a reviewed inclusion manifest is unavailable.<br>제한된 메모리 연결 진단, 구조화 severity, 세션별 correlation metadata가 있습니다. 검토된 포함 manifest를 사용하는 사용자 생성형 redacted support bundle은 아직 없습니다. |
| NFR-010 | Compatibility / 호환성 | Partial | The project targets Windows 11 24H2+ x64/ARM64; locked restore, build, publish, and package paths cover both architectures, while real-VM support evidence is not complete.<br>프로젝트는 Windows 11 24H2+ x64/ARM64를 대상으로 하고 두 아키텍처의 잠금 복원·빌드·게시·패키징 경로가 있지만 실제 VM 지원 증거는 완성되지 않았습니다. |

## Scope decisions / 범위 결정

| Capability / 기능 | Status | Decision / 결정 |
| --- | --- | --- |
| Mobile, macOS, Linux apps / 모바일, macOS, Linux 앱 | Drop | Windows-native quality is the product boundary.<br>Windows 네이티브 품질이 제품 경계입니다. |
| Cloud account and sync / 클라우드 계정과 동기화 | Drop | No backend or account system.<br>Backend와 계정 시스템을 만들지 않습니다. |
| Team Vault, RBAC, SSO, collaboration / Team Vault, RBAC, SSO, 협업 | Drop | Requires a separate server product and security model.<br>별도 서버 제품과 보안 모델이 필요합니다. |
| Terminal multiplayer / 터미널 공동 작업 | Drop | Session relay and authorization are outside scope.<br>세션 중계와 권한 부여는 범위 밖입니다. |
| FTP and FTPS | Drop | Sutty is SSH/SFTP only.<br>Sutty는 SSH/SFTP 전용입니다. |
| Telnet, Serial, RDP, VNC | Drop | Protocol and security expansion is outside scope.<br>프로토콜·보안 범위 확장은 제외합니다. |
| X11 forwarding / X11 포워딩 | Drop | Windows X-server dependency and low core value; dormant planned UI must not be treated as support.<br>Windows X server 의존과 낮은 핵심 가치 때문에 제외하며 비활성 계획 UI를 지원 기능으로 보면 안 됩니다. |
| Built-in IDE / 내장 IDE | Drop | External editors remain the integration boundary.<br>외부 editor를 연동 경계로 유지합니다. |
| AI command generation / AI 명령 생성 | Drop | Dropped for v1 because of security and correctness scope.<br>보안·정확성 범위 때문에 v1에서 제외합니다. |
| Plugin marketplace | Drop | API stability and supply-chain risk are out of scope.<br>API 안정성과 공급망 위험 때문에 제외합니다. |
| FIPS certification claim / FIPS 인증 주장 | Drop | No certification claim without formal validation.<br>공식 검증 없이 인증을 주장하지 않습니다. |
| Remote full-text search / 원격 전체 텍스트 검색 | Planned | Post-GA candidate.<br>GA 이후 후보입니다. |
| Directory comparison | Planned | P2 after transfer correctness.<br>전송 정확성 이후 P2입니다. |
| Portable signed archive / 서명 portable archive | Planned | P2 after signed MSIX/update maturity.<br>서명 MSIX·업데이트 안정화 이후 P2입니다. |

## Update rule / 갱신 규칙

Every feature PR should name its requirement ID, update the status only after source and tests land together, and preserve both English and Korean meaning. A release must not infer GA readiness from the number of **Implemented** rows alone. Compatibility claims and evidence promotion additionally follow [Supported environments](SUPPORTED_ENVIRONMENTS.md), the [evidence schema](evidence/EVIDENCE_SCHEMA.md), and the [Alpha 4 execution plan](ALPHA4_EXECUTION_PLAN.md).

모든 기능 PR은 requirement ID를 명시하고 소스와 테스트가 함께 반영된 뒤에만 상태를 바꾸며 영어·한국어의 의미를 같이 유지해야 합니다. 릴리스는 **Implemented** 행의 개수만으로 GA 준비 상태를 추정하면 안 됩니다. 호환성 주장과 증거 승격은 [지원 환경](SUPPORTED_ENVIRONMENTS.md), [증거 스키마](evidence/EVIDENCE_SCHEMA.md), [Alpha 4 실행 계획](ALPHA4_EXECUTION_PLAN.md)도 따라야 합니다.

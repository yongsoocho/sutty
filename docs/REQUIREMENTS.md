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

The evidence baseline is the source tree, not disabled UI or future-looking comments. Primary evidence areas are [SSH sessions](../src/sutty.Core/Sessions/SshNetSession.cs), [host-key security](../src/sutty.Core/Security), [terminal buffer](../src/sutty.UI/Helpers/VtScreenBuffer.cs), [terminal/REPL UI](../src/sutty.UI/Views/SessionView.xaml.cs), [SFTP services](../src/sutty.Core/Sftp), [Files UI](../src/sutty.UI/Views/FileTreePanel.xaml.cs), [local command/history storage](../src/sutty.Command), [Multi selection](../src/sutty.UI/Views/MultiSessionGrid.xaml.cs), and [focused self-tests](../tests).

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
| HOST-001 | P0 | Partial | Explicit Saved Hosts support create/update, delete, group, environment, tags, favorites, and opaque credential references; duplicate and bulk-management UX remain planned.<br>명시적 저장 호스트는 생성·수정·삭제·그룹·환경·태그·즐겨찾기·불투명 자격증명 참조를 지원하며 복제·일괄 관리 UX는 계획 상태입니다. |
| HOST-002 | P1 | Partial | Saved Hosts and append-only history are searched together; responsive cards, groups, environments, and favorites exist, while 1,000-host performance evidence is incomplete.<br>저장 호스트와 append-only 히스토리를 함께 검색하며 반응형 카드·그룹·환경·즐겨찾기가 있지만 1,000 Host 성능 증거는 미완성입니다. |
| HOST-003 | P0 | Implemented | Every completed connection attempt appends success, failure, or cancellation, a bounded diagnostic code, and duration without storing secrets.<br>완료된 모든 연결 시도는 비밀정보 없이 성공·실패·취소, 제한된 진단 코드, 소요 시간을 새 행으로 추가합니다. |
| HOST-004 | P0 | Partial | SSH, Terminal, and SFTP state are independent and SFTP failure preserves SSH; tunnel state is not implemented.<br>SSH·Terminal·SFTP 상태는 분리되고 SFTP 실패 시 SSH를 유지하지만 tunnel 상태는 없습니다. |
| HOST-005 | P1 | Planned | Workspace/tab restore is not implemented.<br>Workspace·탭 복원을 구현하지 않았습니다. |
| HOST-006 | P1 | Partial | Host canonicalization covers DNS, IPv4, IPv6, and nonstandard ports; the required connection matrix is unverified.<br>DNS·IPv4·IPv6·비표준 port를 정규화하지만 필수 연결 매트릭스를 검증하지 않았습니다. |
| HOST-007 | P1 | Planned | No command-line Saved Host opener exists.<br>명령줄 Saved Host 열기 기능이 없습니다. |

## SSH and authentication / SSH와 인증

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| SSH-001 | P0 | Partial | SSH-2 connect, cancellation, and gate-drained disconnect exist; timeout/fault compatibility acceptance is incomplete.<br>SSH-2 연결·취소·gate-drain 종료는 있지만 timeout·fault 호환성 인수가 미완성입니다. |
| SSH-002 | P0 | Partial | Password and private key work; password mode has a non-interactive fallback that answers password-like keyboard-interactive prompts, but OTP/multi-prompt UI is unavailable.<br>비밀번호·개인키는 동작하고 비밀번호 방식은 password 형태의 keyboard-interactive prompt에 비대화형 fallback으로 답하지만 OTP·다중 prompt UI는 없습니다. |
| SSH-003 | P1 | Planned | Windows OpenSSH Agent authentication is unavailable.<br>Windows OpenSSH Agent 인증을 지원하지 않습니다. |
| SSH-004 | P1 | Partial | SSH.NET loads supported OpenSSH/PEM keys; PPK v2/v3 import and a complete key-format matrix are missing.<br>SSH.NET 지원 OpenSSH/PEM 키는 읽지만 PPK v2/v3 가져오기와 전체 key-format 매트릭스가 없습니다. |
| SSH-005 | P0 | Implemented | Unknown/trusted/changed states fail closed, both SSH and SFTP verify, changed keys block, and focused security self-tests cover persistence and concurrency.<br>Unknown/trusted/changed 상태를 기본 차단으로 처리하고 SSH·SFTP 모두 검증하며 변경 키 차단과 저장·동시성 self-test가 있습니다. |
| SSH-006 | P1 | Planned | Jump Host is disabled and has no backend.<br>Jump Host는 비활성이고 backend가 없습니다. |
| SSH-007 | P1 | Partial | Direct, HTTP CONNECT, SOCKS4, and SOCKS5 routes create real SSH.NET connections and the same route is used for SFTP. Managed profiles, explicit proxy-DNS evidence, jump/audited adapters, and the enterprise matrix are incomplete.<br>Direct·HTTP CONNECT·SOCKS4·SOCKS5 경로가 실제 SSH.NET 연결을 만들고 SFTP도 같은 경로를 사용합니다. 관리형 프로필·명시적 proxy-DNS 증거·jump/감사 어댑터·기업용 매트릭스는 미완성입니다. |
| SSH-008 | P1 | Partial | Keepalive is applied per connection; automatic reconnect and replay safety UX are missing.<br>연결별 keepalive는 적용하지만 자동 재연결과 replay 안전 UX가 없습니다. |
| SSH-009 | P1 | Planned | Negotiated KEX/cipher/MAC/host-key information is not surfaced.<br>협상된 KEX/cipher/MAC/host-key 정보를 표시하지 않습니다. |
| SSH-010 | P0 | Planned | There is no explicit product policy or tested override model for legacy algorithms.<br>Legacy 알고리즘을 위한 명시적 제품 정책과 검증된 override 모델이 없습니다. |

## Credential security / 자격증명 보안

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| CRED-001 | P0 | Implemented | Credential storage is opt-in. A random AES-256 key is protected for the current Windows user, each record uses authenticated AES-GCM encryption, and SQLite stores only an opaque reference.<br>자격증명 저장은 선택형입니다. 무작위 AES-256 키를 현재 Windows 사용자에게 보호하고 각 레코드는 인증된 AES-GCM 암호화를 사용하며 SQLite에는 불투명 참조만 저장합니다. |
| CRED-002 | P0 | Partial | Passwords and key passphrases are excluded from settings, history, profile rows, and crash messages; the connection object is cleared after each attempt and tamper/plaintext self-tests exist. Broader memory-lifetime and UI-automation review remains.<br>비밀번호와 키 암호는 설정·히스토리·프로필 행·충돌 메시지에서 제외하며 연결 시도 뒤 객체 값을 지우고 변조·평문 self-test를 수행합니다. 더 넓은 메모리 수명·UI 자동화 검토는 남아 있습니다. |

## Terminal / 터미널

The native renderer is intentionally transitional. See [ADR 0001](adr/0001-terminal-renderer.md).

네이티브 렌더러는 의도적인 임시 단계입니다. [ADR 0001](adr/0001-terminal-renderer.md)을 확인하세요.

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| TERM-001 | P0 | Partial | A persistent real `ShellStream` PTY exists; the required vim/tmux/htop/sudo compatibility matrix has not passed.<br>실제 지속 `ShellStream` PTY가 있지만 필수 vim/tmux/htop/sudo 호환 매트릭스를 통과하지 않았습니다. |
| TERM-002 | P0 | Partial | Cursor, erase, scroll region, alternate screen, and device responses exist; SGR style/color, mouse, and broad VT compatibility do not.<br>커서·지우기·스크롤 영역·대체 화면·장치 응답은 있지만 SGR 색·스타일·마우스·폭넓은 VT 호환성이 없습니다. |
| TERM-003 | P0 | Partial | Control letters, Tab, arrows, navigation keys, DECCKM, and F1–F12 are sent. Global tab/navigation/settings shortcuts and Ctrl/Shift+Insert are implemented; exhaustive input validation is missing.<br>Control 문자·Tab·방향키·탐색키·DECCKM·F1–F12를 전송하며 전역 탭·내비게이션·설정 단축키와 Ctrl/Shift+Insert를 구현했습니다. 전체 입력 검증은 남아 있습니다. |
| TERM-004 | P0 | Partial | Runtime server-side resize uses SSH.NET's public `ChangeWindowSize` API; shell/TUI and resize-stress integration evidence remains incomplete.<br>실행 중 서버 측 크기 변경은 SSH.NET 공개 `ChangeWindowSize` API를 사용하지만 셸/TUI·resize stress 통합 증거는 아직 완성되지 않았습니다. |
| TERM-005 | P1 | Partial | Incremental UTF-8 input/output exists; Korean IME and wide/combining cell behavior are not acceptance-tested or complete.<br>점진적 UTF-8 입력·출력은 있지만 한글 IME와 넓은 문자·결합 문자 셀 동작이 완전하지 않고 인수 테스트가 없습니다. |
| TERM-006 | P1 | Partial | Bounded scrollback, selectable text, Ctrl+Insert copy, and Shift+Insert paste exist; terminal search, bracketed-paste mode detection, and 100,000-line evidence are missing.<br>제한된 scrollback·텍스트 선택·Ctrl+Insert 복사·Shift+Insert 붙여넣기가 있지만 터미널 검색·bracketed-paste 모드 감지·100,000줄 검증은 남아 있습니다. |
| TERM-007 | P1 | Planned | Opt-in transcript storage and retention are not implemented.<br>선택형 transcript 저장과 보존 정책을 구현하지 않았습니다. |
| TERM-008 | P1 | Implemented | REPL cells classify JSON/YAML syntax plus critical/error and warning text with bounded parsing. Recent and saved commands are suggested without execution and accepted with Right Arrow or optional Tab.<br>REPL 셀은 제한된 파싱으로 JSON/YAML 문법과 critical/error·warning 텍스트를 분류합니다. 최근·저장 명령은 실행 없이 제안되며 오른쪽 화살표 또는 선택형 Tab으로 적용합니다. |

## SFTP file system / SFTP 파일 시스템

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| SFTP-001 | P0 | Partial | Remote path navigation, refresh, and lazy loading exist; symlink-loop and broad permission/error testing are missing.<br>원격 경로 이동·새로고침·지연 로딩은 있지만 symlink loop와 폭넓은 권한·오류 테스트가 없습니다. |
| SFTP-002 | P0 | Partial | Single-file upload/download works; directory trees, empty-folder transfer, 100GB, and full Unicode/deep-path acceptance are missing.<br>단일 파일 업로드·다운로드는 동작하지만 디렉터리 트리·빈 폴더 전송·100GB·전체 Unicode/깊은 경로 인수가 없습니다. |
| SFTP-003 | P0 | Partial | Same-parent rename, file delete, empty-directory delete, and mkdir exist; cross-directory move, recursive delete, and complete conflict/error UX do not.<br>같은 상위 디렉터리 내 이름 변경·파일 삭제·빈 디렉터리 삭제·mkdir은 있지만 디렉터리 간 이동·재귀 삭제·전체 충돌·오류 UX가 없습니다. |
| SFTP-004 | P1 | Planned | Unix permission changes are unavailable.<br>Unix permission 변경을 지원하지 않습니다. |
| SFTP-005 | P0 | Partial | Ask/overwrite/skip and no-silent-overwrite safety exist for current file paths; rename/newer-only and folder policy do not.<br>현재 파일 경로에는 Ask/overwrite/skip과 조용한 덮어쓰기 방지가 있지만 rename/newer-only·폴더 정책은 없습니다. |
| SFTP-006 | P1 | Planned | Local/remote synchronized browsing is unavailable.<br>Local/remote 동기 탐색을 지원하지 않습니다. |
| SFTP-007 | P2 | Planned | Directory comparison is unavailable.<br>Directory comparison을 지원하지 않습니다. |

## Transfer manager / 전송 관리자

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| XFER-001 | P0 | Partial | A per-panel eight-job queue and a serialized transfer worker exist; global/per-host configurable concurrency and durable FIFO jobs do not.<br>패널별 최대 8개 queue와 직렬 transfer worker는 있지만 전역·Host별 설정 가능 동시성과 영속 FIFO job이 없습니다. |
| XFER-002 | P0 | Partial | Progress, speed, ETA, direction, and state are visible; refresh throttling and large-file evidence are missing.<br>진행률·속도·ETA·방향·상태는 표시하지만 갱신 제한과 대용량 파일 증거가 없습니다. |
| XFER-003 | P0 | Partial | Queued/running cancellation and temporary-file cleanup exist; pause and retry do not, and stalled synchronous network calls may wait for transport timeout.<br>대기·실행 취소와 임시 파일 정리는 있지만 일시정지·재시도가 없고 멈춘 동기 네트워크 호출은 transport timeout까지 기다릴 수 있습니다. |
| XFER-004 | P0 | Partial | Upload uses remote temp, backup/promotion, rollback, and no pre-delete; final size verification is missing.<br>업로드는 원격 temp·backup/promotion·rollback을 사용하고 선삭제하지 않지만 최종 크기 검증이 없습니다. |
| XFER-005 | P1 | Planned | Resume is unavailable.<br>Resume을 지원하지 않습니다. |
| XFER-006 | P1 | Planned | Transfer jobs are not restored after restart.<br>재시작 후 전송 job을 복원하지 않습니다. |
| XFER-007 | P1 | Planned | Final remote size and optional checksum verification are unavailable.<br>최종 원격 크기와 선택 checksum 검증을 지원하지 않습니다. |

## Port forwarding / 포트 포워딩

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| TUN-001 | P1 | Planned | Local, remote, and dynamic forwarding are unavailable.<br>Local·Remote·Dynamic forwarding을 지원하지 않습니다. |
| TUN-002 | P1 | Planned | Host auto-start forwarding rules are unavailable.<br>Host auto-start forwarding rule이 없습니다. |
| TUN-003 | P1 | Planned | External-bind warnings are unavailable because forwarding is not implemented.<br>Forwarding이 없으므로 외부 bind 경고도 없습니다. |
| TUN-004 | P2 | Planned | Temporary session-scoped tunnels are unavailable.<br>세션 범위 임시 tunnel이 없습니다. |

## REPL, snippets, and Multi / REPL, snippet, Multi

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| CMD-001 | P0 | Partial | Core results preserve stdout, stderr, exit status/signal, start, duration, and cancellation; full UI cancellation and regression coverage are incomplete.<br>Core 결과는 stdout·stderr·exit status/signal·시작·소요 시간·취소를 보존하지만 전체 UI 취소와 회귀 검증이 미완성입니다. |
| CMD-002 | P1 | Planned | REPL output is displayed after command completion, not streamed.<br>REPL 출력은 스트리밍이 아니라 명령 완료 뒤 표시됩니다. |
| CMD-003 | P1 | Partial | Positional `$1`/`$2` template substitution exists; named typed/validated/secret parameters do not.<br>위치형 `$1`/`$2` 치환은 있지만 이름형 typed/validated/secret parameter는 없습니다. |
| CMD-004 | P1 | Implemented | New sessions are unselected by default and only an explicit prior choice is preserved.<br>새 세션은 기본 미선택이며 사용자가 명시한 기존 선택만 유지합니다. |
| CMD-005 | P0 | Partial | PROD-tagged targets require a confirmation with target count and command preview; environment distribution, typed confirmation, policy, and audit are missing.<br>PROD 태그 대상은 대상 수·명령 미리보기 확인을 거치지만 환경 분포·확인 문구 입력·정책·audit이 없습니다. |
| CMD-006 | P1 | Partial | Each host uses a structured result with stdout, stderr, exit code/signal, and duration; the UI shows a truncated combined-output preview plus exit/signal, while durable detail, export, timeout, and audit are missing.<br>각 Host는 stdout·stderr·exit code/signal·소요 시간을 가진 구조화 결과를 사용하고 UI는 잘린 합산 출력과 exit/signal을 표시하지만 영속 상세·export·timeout·audit은 없습니다. |
| CMD-007 | P0 | Implemented | Multi uses non-interactive command execution and does not broadcast raw terminal keystrokes.<br>Multi는 비대화형 명령 실행을 사용하며 raw terminal keystroke를 broadcast하지 않습니다. |

## Import, export, and policy / 가져오기, 내보내기, 정책

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| IMP-001 | P1 | Planned | OpenSSH config import is unavailable.<br>OpenSSH config 가져오기를 지원하지 않습니다. |
| IMP-002 | P1 | Planned | Legacy SSH saved-session import is unavailable.<br>레거시 SSH 저장 세션 가져오기를 지원하지 않습니다. |
| IMP-003 | P1 | Planned | Legacy SFTP site-profile import is unavailable.<br>레거시 SFTP 사이트 프로필 가져오기를 지원하지 않습니다. |
| IMP-004 | P1 | Planned | Encrypted Sutty bundle export/import is unavailable.<br>암호화 Sutty bundle 내보내기·가져오기를 지원하지 않습니다. |
| POL-001 | P1 | Planned | HKLM policy precedence and locked-settings UI are unavailable.<br>HKLM 정책 우선순위와 잠긴 설정 UI가 없습니다. |
| POL-002 | P1 | Planned | Managed credential-free Host catalog is unavailable.<br>자격 증명 없는 관리형 Host catalog를 지원하지 않습니다. |

## Host-key security / 호스트키 보안

| ID | Priority | Status | Evidence and remaining gap / 증거와 남은 차이 |
| --- | --- | --- | --- |
| SEC-HK-001 | P0 | Implemented | Unknown keys default to rejection and expose full endpoint, algorithm, SHA-256 fingerprint, Trust and save, Connect once, and Cancel.<br>알 수 없는 키는 기본 거부하며 전체 endpoint·algorithm·SHA-256 지문과 신뢰하고 저장·이번만 연결·취소를 제공합니다. |
| SEC-HK-002 | P0 | Implemented | Changed keys are blocked; the error retains trusted and presented algorithms/fingerprints and cannot be overridden by Trust once.<br>변경 키는 차단하며 오류에 기존·제시 algorithm/fingerprint가 있고 이번만 연결로 우회할 수 없습니다. |
| SEC-HK-003 | P1 | Planned | Enterprise policy cannot disable Trust once.<br>기업 정책으로 이번만 연결을 비활성화할 수 없습니다. |
| SEC-HK-004 | P1 | Planned | Known-host management, rotation workflow, and audit events are unavailable.<br>Known-host 관리·rotation 흐름·audit event가 없습니다. |

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
| NFR-008 | Localization / 지역화 | Partial | Korean/English localization infrastructure exists; hardcoded user-visible strings and full parity remain to be audited.<br>한국어/영어 지역화 구조는 있지만 사용자 표시 hardcoded 문자열과 전체 동등성을 감사해야 합니다. |
| NFR-009 | Supportability / 지원성 | Planned | Structured diagnostic codes and correlation IDs are unavailable.<br>구조화된 진단 코드와 correlation ID가 없습니다. |
| NFR-010 | Compatibility / 호환성 | Partial | The project targets Windows 11 24H2+ x64/ARM64; CI and real-VM support evidence are not complete.<br>프로젝트는 Windows 11 24H2+ x64/ARM64를 대상으로 하지만 CI·실제 VM 지원 증거가 완성되지 않았습니다. |

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

Every feature PR should name its requirement ID, update the status only after source and tests land together, and preserve both English and Korean meaning. A release must not infer GA readiness from the number of **Implemented** rows alone.

모든 기능 PR은 requirement ID를 명시하고 소스와 테스트가 함께 반영된 뒤에만 상태를 바꾸며 영어·한국어의 의미를 같이 유지해야 합니다. 릴리스는 **Implemented** 행의 개수만으로 GA 준비 상태를 추정하면 안 됩니다.

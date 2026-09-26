# Sutty Roadmap / Sutty 로드맵

This roadmap follows reliability gates, not calendar promises. Each stage must ship as complete vertical slices with Core behavior, tests, UI, and honest documentation.

이 로드맵은 날짜 약속이 아니라 신뢰성 게이트를 따릅니다. 각 단계는 Core 동작, 테스트, UI, 정직한 문서를 갖춘 완결된 Vertical Slice로 제공해야 합니다.

The authoritative near-term order, dependencies, and exit criteria are in the [Alpha 4 execution plan](ALPHA4_EXECUTION_PLAN.md). Support promotion follows [Supported environments](SUPPORTED_ENVIRONMENTS.md) and never advances from implementation alone.

단기 실행 순서·의존성·종료 기준은 [Alpha 4 실행 계획](ALPHA4_EXECUTION_PLAN.md)이 기준입니다. 지원 상태 승격은 [지원 환경](SUPPORTED_ENVIRONMENTS.md)을 따르며 구현만으로 올리지 않습니다.

## Now — Daily-driver Alpha hardening / 일상 사용 Alpha 안정화

The current implementation baseline is `90a6e04e6a66898850f0a53b16bd3d43257d963d`.
Harden the existing workflow in this order: reproducible candidate build → connection/command
target safety → edit/transfer recovery → real Windows acceptance → release/document alignment.
The [16 acceptance scenarios](RELEASE_ACCEPTANCE.md#daily-workflow-review--일상-작업-점검)
supplement the existing release gates; source tests cannot replace them. Alpha 4 source changes
are not evidence about the previously published Alpha 3 ZIP.

현재 구현 기준은 위 커밋입니다. 후보 빌드 → 연결·명령 대상 안전성 → 편집·전송 복구 →
실제 Windows 인수 → 배포·문서 정합 순서로 기존 기능을 마무리합니다. 인수 시나리오 16개는
기존 출시 게이트에 추가되며 자동 테스트로 대체하지 않습니다. Alpha 4 소스와 공개 Alpha 3은 구분합니다.

### Milestone A — Authentication and Route Matrix / 인증·경로 매트릭스

- Record one independently reviewable live acceptance slice for password, each supported private-key format, Windows Agent, and repeated keyboard-interactive prompts.
- Record Direct, HTTP/SOCKS, Jump, ProxyCommand, and forwarding normal/failure/cancellation/shutdown slices with proof that no failed indirect route opens Direct.
- Record live acceptance for credential-free negotiated connection information and stable route failure codes.

- 비밀번호, 지원 개인키 형식별, Windows Agent, 반복 keyboard-interactive prompt를 독립적으로 리뷰 가능한 실환경 인수 Slice로 기록합니다.
- Direct, HTTP/SOCKS, Jump, ProxyCommand, forwarding의 정상·실패·취소·종료를 Slice로 검증하고 실패한 간접 경로가 Direct를 열지 않음을 증명합니다.
- 자격증명 없는 협상 연결 정보와 안정적인 경로 오류 코드의 실환경 인수 증거를 기록합니다.

### Milestone B — Terminal Compatibility Evidence / 터미널 호환성 증거

- Complete shell and TUI slices for PowerShell, bash/zsh, vim, tmux, and htop with resize, alternate-screen, mouse, and shutdown evidence.
- Complete Korean IME, CJK/emoji/combining text, clipboard, keyboard, search, and accessibility slices.
- Record bounded output, latency, reconnect, and long-running soak evidence.

- PowerShell, bash/zsh, vim, tmux, htop의 resize·alternate screen·mouse·종료 Slice를 완성합니다.
- 한글 IME, CJK·emoji·결합 문자, clipboard, keyboard, search, 접근성 Slice를 완성합니다.
- 제한된 출력, latency, 재연결, 장시간 soak 증거를 기록합니다.

### Milestone C — Known Host and Connection Diagnostics / Known Host·연결 진단

- Validate the implemented known-host list, inspect, remove, changed-key explanation, and deliberate rotation without weakening fail-closed behavior.
- Validate the implemented user-created redacted local support bundle and its explicit inclusion manifest and exclusion tests.
- Correlate local connection activity by session while excluding credentials, terminal transcripts, and command output.

- 구현된 Known Host 목록·확인·삭제·변경 Key 설명·명시적 rotation을 기본 차단 정책을 유지하며 검증합니다.
- 구현된 사용자 생성형 redaction 로컬 support bundle의 포함 항목 manifest와 제외 동작을 검증합니다.
- 자격증명·terminal transcript·command output을 제외하고 세션별 로컬 연결 활동을 연결합니다.

### Milestone D — Signed MSIX and Update Recovery / 서명 MSIX·업데이트 복구

- Produce and verify signed x64/ARM64 packages from reviewed source and retained build provenance.
- Record clean install, upgrade, failed update, rollback, uninstall, and local-data preservation slices.
- Publish support boundaries and reproducible release evidence before changing the GA status.

- 검토한 소스와 보존된 build provenance로 서명한 x64/ARM64 package를 생성·검증합니다.
- Clean install, upgrade, update 실패, rollback, uninstall, 로컬 데이터 보존 Slice를 기록합니다.
- GA 상태를 바꾸기 전에 지원 경계와 재현 가능한 release 증거를 공개합니다.

## Now — File and command reliability / 파일·명령 신뢰성

- Complete safe two-way file workflows, drag-and-drop target clarity, and a global bounded transfer manager.
- Prove retry, resume, checkpoint, safe promotion, and size/SHA-256 verification under disconnect, cancellation, restart, and disk-full faults.
- Validate the implemented external-editor round trip, content conflict checks, retained copies, and recovery actions.
- Keep the 3×3 Multi grid; preview all approved targets across pages, restrict commands to integrated SSH exec, preserve drafts on cancellation, and bound result waits without claiming remote termination.
- Begin representative acceptance with 1 GiB, 1,000 small files, and ten SSH sessions for one hour; separately verify 16 tabs and two-page selection. Larger-scale claims require corresponding evidence.

- 안전한 양방향 파일 작업, drag-and-drop 대상 명확성, 제한된 전역 Transfer Manager를 완성합니다.
- 네트워크 단절, 취소, 재실행, 디스크 부족에서 retry, resume, checkpoint, safe promotion, size/SHA-256 검증을 증명합니다.
- 구현된 외부 편집기의 내용 충돌 확인, 편집본 보존과 복구 동선을 검증합니다.
- 3×3 Multi를 유지하며 전체 페이지 승인 대상 확인, 통합 SSH exec 제한, 취소 시 초안 보존, 유한 대기를 적용합니다. 대기 종료를 원격 작업 종료로 표현하지 않습니다.
- 대표 인수는 1 GiB·작은 파일 1,000개·SSH 10세션 1시간으로 시작하고 16탭·2페이지 선택을 별도로 검증합니다. 더 큰 규모의 지원 주장은 해당 증거가 필요합니다.

## Now — Existing tunnels and sharing acceptance / 기존 터널·공유 인수

- The post-connect tunnel manager is implemented. Verify port conflicts, policy rejection, start/stop, non-loopback confirmation, and session-close cleanup on real servers.
- Credential-free JSON definition sharing, import preview, duplicate decisions, and authentication aliases are implemented. Verify another PC imports definitions and binds its own authentication without executing imported commands.
- Sharing omits stored credentials, private-key paths, and trust. User-authored command text and endpoint identifiers still require manual review.

- 연결 후 Tunnel Manager는 구현됐습니다. 실서버에서 포트 충돌·정책 거부·시작/중지·비루프백 확인·세션 종료 정리를 검증합니다.
- 비밀정보 없는 JSON 공유·가져오기 미리보기·중복 선택·인증 별칭은 구현됐습니다. 다른 PC에서 정의를 가져와 자신의 인증을 연결하고 명령을 자동 실행하지 않는지 검증합니다.
- 저장된 자격증명·개인키 경로·신뢰는 공유에서 제외하지만 사용자 명령 텍스트와 대상 식별자는 직접 검토해야 합니다.

## Later — Three small conveniences / 후속 편의 개선 세 가지

Promote these only after the reliability work and observed user need; do not rebuild existing features.

신뢰성 개선과 실제 불편 확인 이후에만 진행하며 이미 있는 기능을 다시 만들지 않습니다.

- Files focus/restore without a docking framework.
- Optional host-specific local/remote folder pairs without automatic synchronization.
- A retained-edit list with deliberate local-copy cleanup, without automatic upload.

- 도킹 프레임워크 없는 Files 집중 보기·복귀
- 자동 동기화 없는 호스트별 로컬·원격 폴더 쌍
- 자동 업로드 없는 보존 편집본 목록과 확인 후 로컬 복사본 정리

## Stage exit rule / 단계 종료 기준

A stage is complete only when its relevant normal, invalid-input, cancellation, timeout, disconnect, shutdown, migration, x64/ARM64, and real-environment paths are recorded. Anything less remains Partial or Experimental.

각 단계는 관련 정상, 잘못된 입력, 취소, timeout, 연결 종료, 앱 종료, migration, x64/ARM64, 실환경 경로의 증거가 기록돼야 완료입니다. 그보다 부족하면 Partial 또는 Experimental입니다.

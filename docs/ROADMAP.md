# Sutty Roadmap / Sutty 로드맵

This roadmap follows reliability gates, not calendar promises. Each stage must ship as complete vertical slices with Core behavior, tests, UI, and honest documentation.

이 로드맵은 날짜 약속이 아니라 신뢰성 게이트를 따릅니다. 각 단계는 Core 동작, 테스트, UI, 정직한 문서를 갖춘 완결된 Vertical Slice로 제공해야 합니다.

## Now — Daily-driver Alpha hardening / 일상 사용 Alpha 안정화

### Milestone A — Authentication and Route Matrix / 인증·경로 매트릭스

- Record one independently reviewable live acceptance slice for password, each supported private-key format, Windows Agent, and repeated keyboard-interactive prompts.
- Record Direct, HTTP/SOCKS, Jump, ProxyCommand, and forwarding normal/failure/cancellation/shutdown slices with proof that no failed indirect route opens Direct.
- Surface negotiated connection information and stable route failure codes without credentials.

- 비밀번호, 지원 개인키 형식별, Windows Agent, 반복 keyboard-interactive prompt를 독립적으로 리뷰 가능한 실환경 인수 Slice로 기록합니다.
- Direct, HTTP/SOCKS, Jump, ProxyCommand, forwarding의 정상·실패·취소·종료를 Slice로 검증하고 실패한 간접 경로가 Direct를 열지 않음을 증명합니다.
- 자격증명 없이 협상 연결 정보와 안정적인 경로 오류 코드를 표시합니다.

### Milestone B — Terminal Compatibility Evidence / 터미널 호환성 증거

- Complete shell and TUI slices for PowerShell, bash/zsh, vim, tmux, and htop with resize, alternate-screen, mouse, and shutdown evidence.
- Complete Korean IME, CJK/emoji/combining text, clipboard, keyboard, search, and accessibility slices.
- Record bounded output, latency, reconnect, and long-running soak evidence.

- PowerShell, bash/zsh, vim, tmux, htop의 resize·alternate screen·mouse·종료 Slice를 완성합니다.
- 한글 IME, CJK·emoji·결합 문자, clipboard, keyboard, search, 접근성 Slice를 완성합니다.
- 제한된 출력, latency, 재연결, 장시간 soak 증거를 기록합니다.

### Milestone C — Known Host and Connection Diagnostics / Known Host·연결 진단

- Add known-host list, inspect, remove, changed-key explanation, and deliberate rotation slices without weakening fail-closed behavior.
- Produce a user-created redacted local support bundle with an explicit inclusion manifest and exclusion tests.
- Correlate local connection activity by session while excluding credentials, terminal transcripts, and command output.

- 기본 차단 정책을 약화하지 않는 Known Host 목록·확인·삭제·변경 Key 설명·명시적 rotation Slice를 추가합니다.
- 포함 항목 manifest와 제외 테스트를 갖춘 사용자 생성형 redaction 로컬 support bundle을 만듭니다.
- 자격증명·terminal transcript·command output을 제외하고 세션별 로컬 연결 활동을 연결합니다.

### Milestone D — Signed MSIX and Update Recovery / 서명 MSIX·업데이트 복구

- Produce and verify signed x64/ARM64 packages from reviewed source and retained build provenance.
- Record clean install, upgrade, failed update, rollback, uninstall, and local-data preservation slices.
- Publish support boundaries and reproducible release evidence before changing the GA status.

- 검토한 소스와 보존된 build provenance로 서명한 x64/ARM64 package를 생성·검증합니다.
- Clean install, upgrade, update 실패, rollback, uninstall, 로컬 데이터 보존 Slice를 기록합니다.
- GA 상태를 바꾸기 전에 지원 경계와 재현 가능한 release 증거를 공개합니다.

## Next — SFTP workspace / SFTP 작업 공간

- Complete safe two-way file workflows, drag-and-drop target clarity, and a global bounded transfer manager.
- Prove retry, resume, checkpoint, safe promotion, and size/SHA-256 verification under disconnect, cancellation, restart, and disk-full faults.
- Add measured lazy/virtualized handling for deep trees, 100 GB files, and 100,000-file directories.
- Add external-editor round trips, synchronized navigation, and directory comparison only after transfer integrity gates pass.

- 안전한 양방향 파일 작업, drag-and-drop 대상 명확성, 제한된 전역 Transfer Manager를 완성합니다.
- 네트워크 단절, 취소, 재실행, 디스크 부족에서 retry, resume, checkpoint, safe promotion, size/SHA-256 검증을 증명합니다.
- 깊은 tree, 100GB 파일, 10만 파일 디렉터리를 lazy loading·virtualization과 측정 결과로 검증합니다.
- 전송 무결성 게이트를 통과한 뒤 외부 편집기 연동, 동기 탐색, 디렉터리 비교를 추가합니다.

## Later — Operations workspace / 운영 작업 공간

- Post-connect tunnel manager with explicit state, stop, conflict, disconnect, and restore behavior.
- Streaming REPL output, typed command parameters, timeouts, cancellation, and bounded history.
- Durable per-host Multi results, failed-target retry, preview/export, and production safeguards.
- Local command palette and activity history without terminal transcript or secret capture.

- 명시적 상태, 중지, 충돌, 연결 종료, 복원 동작을 가진 연결 후 Tunnel Manager
- Streaming REPL 출력, typed parameter, timeout, 취소, 제한된 history
- 영속 Host별 Multi 결과, 실패 대상 재시도, 미리보기·내보내기, 운영 보호 장치
- 터미널 transcript나 secret을 수집하지 않는 로컬 Command Palette와 Activity History

## Final planned stage — Credential-free small-team packs / 자격증명 없는 소규모 팀 Pack

This stage is planned, not implemented in the current Alpha. Definitions currently remain local to each user's installation.

이 단계는 계획 상태이며 현재 Alpha에는 구현되지 않았습니다. 현재 정의는 각 사용자의 로컬 설치에만 저장됩니다.

- Git-friendly host, group, tag, route, tunnel, and command-template export.
- Import preview, conflict decisions, schema versioning, and per-user local credential binding.
- Hard rejection of credential material in every shared package.

- Git 친화적인 Host, 그룹, 태그, 경로, 터널, 명령 템플릿 내보내기
- 가져오기 미리보기, 충돌 결정, schema version, 사용자별 로컬 자격증명 연결
- 모든 공유 package에서 자격증명 자료를 강제로 거부

## Stage exit rule / 단계 종료 기준

A stage is complete only when its relevant normal, invalid-input, cancellation, timeout, disconnect, shutdown, migration, x64/ARM64, and real-environment paths are recorded. Anything less remains Partial or Experimental.

각 단계는 관련 정상, 잘못된 입력, 취소, timeout, 연결 종료, 앱 종료, migration, x64/ARM64, 실환경 경로의 증거가 기록돼야 완료입니다. 그보다 부족하면 Partial 또는 Experimental입니다.

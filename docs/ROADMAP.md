# Sutty Roadmap / Sutty 로드맵

This roadmap follows reliability gates, not calendar promises. Each stage must ship as complete vertical slices with Core behavior, tests, UI, and honest documentation.

이 로드맵은 날짜 약속이 아니라 신뢰성 게이트를 따릅니다. 각 단계는 Core 동작, 테스트, UI, 정직한 문서를 갖춘 완결된 Vertical Slice로 제공해야 합니다.

## Now — Daily-driver Alpha hardening / 일상 사용 Alpha 안정화

- Complete and record live authentication coverage for password, private key formats, Windows Agent, and repeated keyboard-interactive prompts.
- Validate Direct, HTTP/SOCKS, Jump, ProxyCommand, and forwarding failure/shutdown paths without silent fallback.
- Add known-host management and changed-key recovery guidance without weakening fail-closed behavior.
- Finish terminal shell/TUI/Unicode/IME/resize/soak evidence and expose useful connection information.
- Produce a redacted local support bundle with an explicit inclusion manifest and exclusion tests.
- Complete signed x64/ARM64 MSIX, clean install, upgrade, rollback, and update evidence.

- 비밀번호, 개인키 형식, Windows Agent, 반복 keyboard-interactive prompt의 실환경 인증 증거를 완성합니다.
- Direct, HTTP/SOCKS, Jump, ProxyCommand, forwarding의 실패·종료 경로를 조용한 우회 없이 검증합니다.
- 기본 차단 정책을 약화하지 않는 Known Host 관리와 변경 Key 복구 안내를 추가합니다.
- Terminal shell/TUI/Unicode/IME/resize/soak 증거와 유용한 연결 정보를 완성합니다.
- 포함 항목 manifest와 제외 테스트를 갖춘 redaction된 로컬 support bundle을 만듭니다.
- x64/ARM64 서명 MSIX, clean install, upgrade, rollback, update 증거를 완성합니다.

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

- Git-friendly host, group, tag, route, tunnel, and command-template export.
- Import preview, conflict decisions, schema versioning, and per-user local credential binding.
- Hard rejection of credential material in every shared package.

- Git 친화적인 Host, 그룹, 태그, 경로, 터널, 명령 템플릿 내보내기
- 가져오기 미리보기, 충돌 결정, schema version, 사용자별 로컬 자격증명 연결
- 모든 공유 package에서 자격증명 자료를 강제로 거부

## Stage exit rule / 단계 종료 기준

A stage is complete only when its relevant normal, invalid-input, cancellation, timeout, disconnect, shutdown, migration, x64/ARM64, and real-environment paths are recorded. Anything less remains Partial or Experimental.

각 단계는 관련 정상, 잘못된 입력, 취소, timeout, 연결 종료, 앱 종료, migration, x64/ARM64, 실환경 경로의 증거가 기록돼야 완료입니다. 그보다 부족하면 Partial 또는 Experimental입니다.

# Development Playbook / 개발 실행 Playbook

This playbook turns [Product Scope](PRODUCT_SCOPE.md) into an implementation and review contract. It applies to Core, storage, UI, tests, packaging, and documentation.

이 문서는 [제품 범위](PRODUCT_SCOPE.md)를 구현·리뷰 계약으로 바꿉니다. Core, 저장소, UI, 테스트, 패키징, 문서에 모두 적용합니다.

## 1. Delivery order / 개발 순서

Use **Core → Test → UI → Live validation** for every vertical slice.

1. **Core:** define an explicit contract, state model, ownership, timeout, cancellation, and disposal path.
2. **Test:** prove normal, failure, cancellation, shutdown, and existing-data migration behavior without UI.
3. **UI:** expose only working states. Show actionable failures and never add placeholder controls.
4. **Live validation:** verify server-, shell-, architecture-, or network-dependent behavior and record the environment and result.

모든 vertical slice는 **Core → Test → UI → 실환경 검증** 순서를 사용합니다.

1. **Core:** 명시적 계약, 상태 모델, 소유권, timeout, 취소, dispose 경로를 정의합니다.
2. **Test:** UI 없이 정상·실패·취소·종료·기존 데이터 migration을 증명합니다.
3. **UI:** 실제 동작하는 상태만 노출하고 오류 원인과 복구 방법을 표시합니다.
4. **실환경:** 서버·shell·architecture·network 의존 동작의 환경과 결과를 기록합니다.

## 2. Vertical Slice rule / Vertical Slice 원칙

A slice solves one user problem end to end and remains independently testable, reviewable, and releasable. Do not mix unrelated refactors, visual redesigns, storage migrations, and transport changes in one slice. Shared extraction is acceptable when two implemented callers need it; speculative frameworks are not.

Slice 하나는 사용자 문제 하나를 끝까지 해결하며 독립적으로 테스트·리뷰·릴리스할 수 있어야 합니다. 관련 없는 refactoring, 시각 변경, 저장 migration, transport 변경을 한 Slice에 섞지 않습니다. 구현된 호출자 둘 이상이 필요한 공통화는 허용하지만 미래를 위한 framework는 만들지 않습니다.

## 3. State, cancellation, timeout, and disposal / 상태·취소·종료

- Every long operation accepts and observes a `CancellationToken`.
- Network and process waits have a documented finite timeout or an explicitly owned lifetime.
- Cancellation is not reported as failure and never promotes a partial transfer.
- Session close stops Terminal, SFTP, tunnels, transfers, callbacks, timers, streams, and child processes owned by that session.
- Event handlers and callbacks are detached before their owner is disposed.
- Cleanup is idempotent and safe after partial initialization.

- 장기 작업은 `CancellationToken`을 받고 실제로 확인합니다.
- 네트워크·프로세스 대기는 유한 timeout 또는 명시적으로 소유한 lifetime을 가집니다.
- 취소를 실패로 기록하지 않고 partial 전송을 최종 파일로 승격하지 않습니다.
- 세션 종료 시 해당 세션의 Terminal, SFTP, tunnel, transfer, callback, timer, stream, child process를 정리합니다.
- 소유자를 dispose하기 전에 event와 callback을 해제합니다.
- 일부 초기화 뒤에도 cleanup은 반복 호출에 안전해야 합니다.

## 4. Secrets and local data / 비밀정보와 로컬 데이터

- Passwords, passphrases, private-key contents, OTP answers, tokens, and terminal transcripts do not enter SQLite, settings, workspace snapshots, command templates, logs, fixtures, or exception details.
- Persist only credential-free intent and opaque credential references. The local encrypted vault is opt-in.
- Migrations are backward-compatible, bounded, and tested with real previous-field casing and malformed data.
- Read failures that could weaken trust, route, or overwrite policy fail closed and provide an explicit recovery action.
- Diagnostic correlation metadata stays local and excludes credentials.

- 비밀번호, passphrase, 개인키 내용, OTP 답변, token, terminal transcript를 SQLite, 설정, workspace snapshot, command template, log, fixture, exception detail에 넣지 않습니다.
- 자격증명 없는 의도와 불투명 credential 참조만 저장하며 로컬 암호화 Vault는 선택형입니다.
- Migration은 이전 필드의 실제 casing과 손상 데이터를 포함해 호환성과 경계를 테스트합니다.
- 신뢰·경로·덮어쓰기 정책을 약화할 수 있는 읽기 실패는 fail-closed로 처리하고 복구 행동을 안내합니다.
- 진단 상관관계 metadata는 로컬에만 두고 자격증명을 제외합니다.

## 5. SFTP integrity contract / SFTP 무결성 계약

Uploads and downloads follow **stage → verify → promote**:

1. Transfer to a deterministic temporary/partial path.
2. Persist a credential-free checkpoint with offset and target identity.
3. Retry only classified transient failures within the configured bound.
4. Verify final size and, when selected, checksum.
5. Promote atomically when the platform/server supports it; otherwise use a documented no-clobber sequence.
6. Preserve the existing destination on failure, cancellation, or verification mismatch.

Multi transfer additionally keeps per-target progress and result state, retries only failed/incomplete targets, and never infers targets from open sessions.

업로드·다운로드는 **staging → 검증 → 승격** 순서를 사용합니다. Offset·대상 identity를 자격증명 없이 checkpoint하고, 분류된 일시 오류만 설정 범위에서 재시도하며, 최종 size와 선택한 checksum을 검증합니다. 실패·취소·불일치 시 기존 대상을 보존합니다. Multi는 서버별 진행·결과를 유지하고 실패·미완료 대상만 재시도하며 열린 세션을 자동 선택하지 않습니다.

## 6. Multi safety / Multi 안전 기준

- Default target count is zero on every entry and restore path.
- The UI shows the exact target count and identities before execution.
- Production-tagged targets require an additional default-cancel confirmation.
- Commands use structured non-interactive execution; raw terminal keystrokes are not broadcast.
- Cancellation and one-host failure do not strand or hide the other host results.
- Retry targets only explicit failed/incomplete results from the same operation.

## 7. Definition of Done / 완료 정의

A slice is complete when all applicable items are true:

- User problem, scope, exclusions, and requirement IDs are recorded.
- State transitions and resource owner are explicit.
- Normal, failure, cancellation, shutdown, and migration tests pass.
- Secrets and existing files remain protected in every exit path.
- Korean and English user-visible text have equivalent meaning.
- Accessibility names and keyboard behavior exist for new interactive controls.
- Product-scope guard, x64 Debug/Release, ARM64 compile, and focused self-tests pass.
- Live-dependent claims include reproducible environment evidence; otherwise status remains Partial/Planned.
- Documentation describes what is implemented, not what a disabled control suggests.

## 8. Review questions / 코드 리뷰 질문

1. What user mistake or failure does this prevent?
2. Can any malformed/legacy state silently weaken security or data integrity?
3. Who owns each socket, stream, process, timer, callback, and cancellation source?
4. What happens on cancel, timeout, app shutdown, and partial initialization?
5. Can logs, persisted state, UI errors, or tests expose a secret?
6. Does Multi still start with zero targets and show the exact scope?
7. Does an SFTP failure preserve existing local/remote data?
8. Is the UI backed by tested behavior with a useful recovery action?
9. Which claim still needs live evidence?

## 9. Prohibited patterns / 금지 패턴

- Silent fallback from an invalid trust, route, authentication, or overwrite choice.
- Fire-and-forget work without an explicit owner and observed failure path.
- Unbounded queues, retries, recursion, output, or retained terminal history.
- Secrets in source, fixtures, command arguments, settings, SQLite, logs, or support data.
- Auto-selecting open sessions as Multi targets.
- Replacing an existing destination before staging and verification succeed.
- Catch-all exceptions that convert corruption into a usable default.
- UI placeholders, disabled promises, or completion claims without implementation evidence.
- Large horizontal layers that leave Core, tests, and UI disconnected.

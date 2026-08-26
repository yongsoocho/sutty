# Sutty Alpha 4 execution plan / Sutty Alpha 4 실행 계획

This is the authoritative Alpha 4 execution contract. It summarizes the approved backlog as ordered, reviewable slices; it does not copy the source planning document and does not mark any live gate complete before evidence exists.

이 문서는 Alpha 4의 기준 실행 계약입니다. 승인된 backlog를 순서가 있는 검토 가능한 Slice로 요약하며 원본 기획서를 그대로 복사하지 않고 증거가 생기기 전에 실환경 gate를 완료로 표시하지 않습니다.

## Non-negotiable rule / 변경할 수 없는 규칙

**Implemented**, **Live Validated**, **Released**, and **Unsupported** have the exact meanings in [Supported environments](SUPPORTED_ENVIRONMENTS.md). A source implementation, fixture, CI job, generated manifest, or successful local run is not live validation. A live issue closes only with a reviewed, redacted, immutable bundle conforming to [EVIDENCE_SCHEMA.md](evidence/EVIDENCE_SCHEMA.md). `Fail` and `Blocked` are honest evidence and keep the acceptance gap open.

**Implemented**, **Live Validated**, **Released**, **Unsupported**는 [지원 환경](SUPPORTED_ENVIRONMENTS.md)의 정확한 의미를 따릅니다. 소스 구현, fixture, CI job, 생성된 manifest, 성공한 로컬 실행은 실환경 검증이 아닙니다. 실환경 issue는 [증거 스키마](evidence/EVIDENCE_SCHEMA.md)에 맞는 검토·redaction·불변 bundle이 있어야 닫습니다. `Fail`과 `Blocked`는 정직한 증거이며 인수 차이는 열린 상태로 유지합니다.

## Ordered slices / 실행 순서

| Order | Slice | Deliverable | Depends on | Exit criteria |
| --- | --- | --- | --- | --- |
| 1 | **SUPPORT-001** | Strict support vocabulary and Windows/architecture/server/authentication/key/route/forwarding/terminal/SFTP claim matrix. | Product scope and current requirements. | Documentation uses only the four support states, identifies unclaimed combinations, and contains no fabricated live or release claim. |
| 2 | **EVID-001** | Exact `manifest.yml` contract, redacted `summary.json` bundle, validator fixtures, and generator path. | SUPPORT-001. | Valid synthetic fixtures pass; malformed, unknown-field, unsafe-path, secret-bearing, and inconsistent fixtures fail. No fake real `Pass` record is added. |
| 3 | **SSH-LIVE-001** | Direct-route password baseline on one approved real SSH server. | SUPPORT-001 and EVID-001. | Exact package, Windows build/architecture, sanitized server family/version, independently verified host identity, password success/rejection/cancellation, command, PTY, SFTP, disconnect, and cleanup produce a reviewed bundle. |
| 4 | **SSH-LIVE-002** | Public-key format matrix: OpenSSH, PEM, PKCS#8, PPK v2, and PPK v3, including approved encrypted/unencrypted variants. | SSH-LIVE-001. | Each materially different key format/encryption/algorithm combination has its own bundle; key material, paths, and passphrases are absent. One combination never validates another. |
| 5 | **SSH-LIVE-003** | Windows Agent authentication with a real service and test-owned key. | SSH-LIVE-001. | Success, unavailable service, rejection/cancellation, disconnect, and process/resource cleanup are recorded in Agent-only bundles. |
| 6 | **SSH-LIVE-004** | Repeated multi-prompt keyboard-interactive OTP/MFA. | SSH-LIVE-001. | Success, repeated prompts, cancellation, timeout, and no-secret persistence are recorded without prompt text or OTP values. |
| 7 | **ROUTE-LIVE-001** | HTTP CONNECT, SOCKS4, and SOCKS5 route matrix. | SSH-LIVE-001 and the separately validated authentication method used by the run. | Every route/authentication combination has a separate normal/failure/cancellation bundle, including proxy-DNS behavior and proof that failure does not fall back to Direct. |
| 8 | **ROUTE-LIVE-002** | SSH jump and external ProxyCommand route matrix. | SSH-LIVE-001 and relevant host-identity/authentication baselines. | Jump and target trust, subprocess/forwarding lifecycle, refusal/cancellation/shutdown, and no Direct fallback are recorded in separate route bundles without endpoints or expanded commands. |
| 9 | **TUN-LIVE-001** | Local, remote, and dynamic forwarding lifecycle. | SSH-LIVE-001; indirect-route cases also depend on the relevant ROUTE-LIVE slice. | Each forwarding mode has its own start/stop, bind/refusal failure, disconnect, shutdown, cleanup, and non-loopback-warning bundle. |
| 10 | **SSH-FAULT-001** | Unexpected primary-transport failure and transfer fault recovery. | SSH-LIVE-001; applicable route and forwarding baselines. | Original failure is retained, negotiated info clears, terminal/SFTP/forwardings/routes close, explicit disconnect does not race, and resumable SFTP proves a non-zero safe resume without promoting partial data. |
| 11 | **SSH-INFO-001** | SSH negotiated-information and no-exec acceptance. | SSH-LIVE-001 and independently provisioned expected host identity; an indirect case depends on its ROUTE-LIVE slice. | Displayed server/client identification, KEX, verified host-key algorithm/fingerprint, both cipher/MAC/compression directions, reconnect freshness, disconnect/fault clearing, and absence of automatic exec requests are reviewed. Raw fingerprint is excluded from evidence. |
| 12 | **PKG-001** | Exact x64 Candidate package/UI gate plus x64/arm64 Alpha release mapping. | EVID-001 plus every live gate declared in that release's scope. | Clean-package metadata/inventory/checksum validation passes; the unpacked physical tree matches the locked exact x64 ZIP by path, size, and SHA-256; the UI has a reviewed manual startup, silent `Alt+1`–`Alt+7`, and shutdown `Pass`; release notes disclose remaining **Implemented** rows; only identical accepted commit/SHA rows may become **Released**. |

The order is a dependency order, not permission to broaden scope. Different authentication methods, key formats, routes, forwarding modes, or fault scenarios must not be collapsed into one issue or one `Pass`. Work may run in parallel only after shared prerequisites are stable and every material combination emits a separate bundle.

이 순서는 의존성 순서이며 범위를 넓힐 권한이 아닙니다. 서로 다른 인증 방식, 키 형식, route, forwarding mode, fault scenario를 하나의 issue나 `Pass`로 합치지 않습니다. 공통 선행 조건이 안정된 뒤에만 병렬 실행할 수 있고 중요한 조합마다 별도 bundle을 만들어야 합니다.

## Alpha 4 exit criteria / Alpha 4 종료 기준

Alpha 4 is ready to tag only when:

1. SUPPORT-001 and EVID-001 source, documentation, and focused validator fixtures pass together.
2. Every matrix row claimed as **Live Validated** has one accepted bundle for the exact tuple; no table is promoted from prose, screenshots, or memory.
3. Every row claimed as **Released** maps to the identical immutable package SHA-256 and commit.
4. All unexecuted or incomplete combinations remain **Implemented** or unclaimed and are named as limitations in the release note.
5. `Fail` and `Blocked` results remain visible to reviewers and are not deleted, rewritten, or averaged into a `Pass`.
6. Product-scope, release-metadata, build, focused self-test, payload, checksum, and evidence-schema gates pass for the tag candidate.
7. No credential, endpoint, username, real fingerprint, local/remote path, transcript, command output, file content, or raw log enters a source commit or release asset.
8. Separate reviewed `PKG-001` and `SSH-LIVE-001` `Pass` bundles are bound to the same exact x64 candidate commit and ZIP SHA-256; each is a new post-run directory with `review.json`, and the candidate-to-acceptance Git range is append-only for every pre-existing bundle.
9. Active main/tag rulesets prevent direct main changes, force-pushes, and release-tag mutation, and the required Windows/Governance checks pass on the acceptance change.
10. Promotion publishes the exact candidate bytes plus `CANDIDATE-MANIFEST.json` and `RELEASE-ATTESTATION.json`, then downloads and verifies the exact five-asset immutable prerelease.

These criteria permit an honest Alpha release with clearly limited evidence; they do not permit a GA, broad compatibility, or universal “supported” claim.

이 기준은 증거 범위를 명확히 제한한 정직한 Alpha 출시를 허용하지만 GA, 넓은 호환성, 보편적인 “지원됨” 주장을 허용하지 않습니다.

The requirement state is tracked in [Requirements](REQUIREMENTS.md), current implementation evidence in [Implementation status](IMPLEMENTATION_STATUS.md), run procedures in [Release acceptance](RELEASE_ACCEPTANCE.md), and protected candidate-to-publication controls in [Release governance](RELEASE_GOVERNANCE.md).

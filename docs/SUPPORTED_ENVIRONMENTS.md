# Sutty supported environments / Sutty 지원 환경

This document is the authoritative compatibility-claim matrix for Sutty. It separates source implementation from real-environment validation and from an immutable released package. A row applies only to the exact combination that its evidence identifies; adjacent versions, authentication methods, routes, or workloads are never implied.

이 문서는 Sutty 호환성 주장의 기준표입니다. 소스 구현, 실제 환경 검증, 변경 불가능한 배포 패키지를 구분합니다. 각 행은 증거가 식별한 정확한 조합에만 적용되며 인접 버전·인증 방식·경로·작업까지 검증됐다고 추론하지 않습니다.

## Status vocabulary / 상태 용어

Only these four support states are valid:

| Status | Exact meaning | 정확한 의미 |
| --- | --- | --- |
| **Implemented** | A production code path and focused source-level checks exist. It is not a live compatibility claim. | 운영 코드 경로와 집중형 소스 검사가 있습니다. 실환경 호환성 주장이 아닙니다. |
| **Live Validated** | The exact matrix row passed on an approved real environment and has a reviewed evidence bundle conforming to [EVIDENCE_SCHEMA.md](evidence/EVIDENCE_SCHEMA.md). | 정확한 행을 승인된 실제 환경에서 통과했고 [증거 스키마](evidence/EVIDENCE_SCHEMA.md)에 맞는 검토 완료 bundle이 있습니다. |
| **Released** | A **Live Validated** row is tied by commit and SHA-256 to an immutable published package. It does not imply GA or validate neighboring rows. | **Live Validated** 행이 commit과 SHA-256으로 변경 불가능한 공개 패키지에 연결됐습니다. GA 또는 인접 행의 검증을 뜻하지 않습니다. |
| **Unsupported** | The environment or capability is deliberately outside the current product boundary. A failed or blocked run does not automatically create this state. | 환경이나 기능이 현재 제품 경계 밖으로 명시됐습니다. 실패·차단된 실행만으로 이 상태가 되지는 않습니다. |

An unlisted combination has **no support claim**. “Pending”, “tested”, “works”, and “supported” are not substitute states. `Pass`, `Fail`, and `Blocked` are evidence-run results, not support states. A `Pass` promotes nothing until its bundle is reviewed; `Fail` and `Blocked` remain evidence without promoting the row.

목록에 없는 조합에는 **지원 주장이 없습니다**. “대기”, “테스트됨”, “동작함”, “지원됨”을 상태 대신 사용하지 않습니다. `Pass`·`Fail`·`Blocked`는 증거 실행 결과이지 지원 상태가 아닙니다. `Pass`도 bundle 검토 전에는 상태를 올리지 않으며 `Fail`과 `Blocked`는 승격 없이 증거로 보존합니다.

## Current claim boundary / 현재 주장 경계

There are currently no repository-recorded **Live Validated** or **Released** rows. The matrices below therefore contain only source-backed **Implemented** claims and explicit **Unsupported** boundaries. Existing Alpha artifacts do not change a row to **Released** without a conforming live-evidence bundle.

현재 저장소에는 **Live Validated** 또는 **Released**로 기록된 행이 없습니다. 따라서 아래 표에는 소스 근거가 있는 **Implemented**와 명시적인 **Unsupported** 경계만 있습니다. 기존 Alpha 산출물이 존재하더라도 규격에 맞는 실환경 증거 bundle 없이는 행을 **Released**로 바꾸지 않습니다.

### Windows and architecture / Windows와 아키텍처

| Environment | Architecture | Current status | Boundary and required promotion evidence |
| --- | --- | --- | --- |
| Windows 11 24H2 or later | x64 | **Implemented** | Target, locked restore, build, publish, and packaging paths exist. Clean-machine startup and the declared scenario must be recorded before promotion. |
| Windows 11 24H2 or later | arm64 | **Implemented** | Target, locked restore, build, publish, and packaging paths exist. A real arm64 Windows run is still required before promotion. |
| Windows earlier than Windows 11 24H2 | any | **Unsupported** | Below the declared minimum operating-system boundary. |
| Windows x86 | x86 | **Unsupported** | No x86 target or package is provided. |
| macOS or Linux desktop application | any | **Unsupported** | Sutty is a Windows-native desktop product. Remote SSH servers may run another OS, but that does not create a Sutty desktop-app claim. |

### SSH server, authentication, and key formats / SSH 서버·인증·키 형식

Every promoted row must name a sanitized server family and exact server version in its manifest. “SSH-2 compatible” is not a wildcard support claim.

| Dimension | Variant | Current status | What remains before promotion |
| --- | --- | --- | --- |
| Server transport | OpenSSH-family SSH-2 server | **Implemented** | Record the exact server family/version and Windows build for each approved scenario. Other server families remain unclaimed until listed. |
| Authentication | Password | **Implemented** | Live success, rejection, cancellation, timeout, and no-secret evidence. |
| Authentication | Public key | **Implemented** | Live format/algorithm/encryption combinations must be separate rows. |
| Authentication | Windows Agent | **Implemented** | Real service/key, unavailable-service, cancellation, and shutdown evidence. |
| Authentication | Keyboard-interactive | **Implemented** | Repeated multi-prompt OTP/MFA success, cancellation, and timeout evidence. |
| Private-key format | OpenSSH, PEM, or PKCS#8 | **Implemented** | Record encrypted and unencrypted variants separately where applicable. |
| Private-key format | PPK v2 or PPK v3 | **Implemented** | Record each version and encrypted/unencrypted variant separately. |

An evidence manifest records the top-level authentication category only. Key format, encryption, algorithm, prompt sequence, and cancellation case belong in the redacted `summary.json`; none may contain key material, passphrases, prompts containing secrets, usernames, or endpoint identifiers.

증거 manifest에는 상위 인증 범주만 기록합니다. 키 형식·암호화·알고리즘·prompt 순서·취소 사례는 redaction한 `summary.json`에 기록하며 키 자료, passphrase, 비밀값이 포함된 prompt, 사용자 이름, endpoint 식별자는 넣지 않습니다.

### Routes and forwarding / 경로와 포워딩

| Capability | Manifest value | Current status | Required live coverage |
| --- | --- | --- | --- |
| Direct | `Direct` | **Implemented** | Normal, rejected host key, cancellation, timeout, disconnect, and shutdown. |
| HTTP CONNECT | `HttpConnect` | **Implemented** | Proxy DNS behavior, authentication where approved, refusal, and proof that failure does not fall back to Direct. |
| SOCKS4 | `Socks4` | **Implemented** | Resolution behavior, refusal, cancellation, and no Direct fallback. |
| SOCKS5 | `Socks5` | **Implemented** | Resolution/authentication behavior, refusal, cancellation, and no Direct fallback. |
| SSH jump | `SshJump` | **Implemented** | Jump and target host-key checks, forwarding lifecycle, target failure, and cleanup. |
| External ProxyCommand | `ExternalProxyCommand` | **Implemented** | Reviewed command expansion, process-tree cleanup, refusal, cancellation, and no Direct fallback. |
| Local forwarding | route value for the owning connection | **Implemented** | Start/stop, bind conflict, non-loopback warning, disconnect, and shutdown. |
| Remote forwarding | route value for the owning connection | **Implemented** | Start/stop, server refusal, disconnect, and shutdown. |
| Dynamic forwarding | route value for the owning connection | **Implemented** | Start/stop, bind conflict, disconnect, and shutdown. |
| X11 forwarding | not applicable | **Unsupported** | Explicitly outside the product scope. |

Forwarding mode is not a `manifest.yml` field in schema version 1. It must be a bounded, credential-free check in `summary.json`, while `route` identifies how the owning SSH connection was established.

Schema version 1의 `manifest.yml`에는 forwarding 전용 필드가 없습니다. Forwarding mode는 `summary.json`의 제한된 비밀정보 없는 검사로 기록하고, `route`는 이를 소유한 SSH 연결의 성립 경로를 식별합니다.

### Terminal matrix / 터미널 매트릭스

| Scenario | Current status | Implemented boundary | Required live evidence |
| --- | --- | --- | --- |
| Local Windows PowerShell through ConPTY | **Implemented** | Process creation, resize, input/output bridge, and process-tree cleanup exist. | Exact Windows build, architecture, profile setting, resize, Unicode/input, long output, and shutdown. |
| Remote PowerShell, bash, or zsh through SSH PTY | **Implemented** | One persistent PTY and the package-local renderer path exist. | Record each shell/version separately with resize, color, keyboard, clipboard, Unicode, latency, and clean exit. |
| vim, tmux, or htop | **Implemented** | Alternate-screen, mouse/input modes, and renderer primitives exist; this is not a per-tool compatibility claim. | Record each tool/version separately, including mode changes, mouse, paste, resize, detach/exit, and cleanup. |
| Korean IME, CJK, emoji, and combining text | **Implemented** | Renderer and input paths exist with focused checks. | Composition, candidate selection, backspace, mixed-width cells, paste, remote round trip, and accessibility on the exact Windows build. |
| Shared or collaborative terminal | **Unsupported** | Session relay and multi-user authorization are outside scope. | None; a scope decision is required before implementation. |

### SFTP matrix / SFTP 매트릭스

| Scenario | Current status | Implemented boundary | Required live evidence |
| --- | --- | --- | --- |
| Connect, browse, lazy tree, and filename search | **Implemented** | Session-bound SFTP state, bounded enumeration, and symlink non-traversal exist. | Server/version-specific Unicode, permission, deep-path, error, cancellation, and disconnect runs. |
| Upload/download, recursive directories, and safe promotion | **Implemented** | Temporary staging, collision policies, recursive operations, and final verification exist. | Empty folders, destination preservation, disk-full/permission failures, cancellation, and server-specific promotion behavior. |
| Retry, resume, pause, and restart checkpoint | **Implemented** | Deterministic partial files, non-secret checkpoints, retry, and restart queue exist. | Real transport loss with a non-zero resume offset and proof that incomplete data is never promoted. |
| Multi 1→N and N→1 | **Implemented** | Explicit target selection, isolated results, deterministic destination folders, and failed-only retry exist. | Sixteen approved targets, one-target failure isolation, name collisions, restart, and failed-only retry. |
| 100 GB / 100,000-file scale and one-hour soak | **Implemented** | Parameterized harness paths and 64-bit transfer accounting exist; no performance claim follows. | Approved-server measurement with bounded memory, duration, throughput, cancellation, cleanup, and a reviewed evidence bundle. |

## Matrix identity and promotion / 행 식별과 승격

A live matrix row is the complete tuple of:

`commit + package SHA-256 + Windows build + architecture + server family/version + route + authentication + declared terminal/SFTP/forwarding scenario`.

The key format, server OS, algorithms, proxy topology, forwarding mode, terminal/tool versions, SFTP workload, and expected checks belong in the redacted summary. Changing any material dimension requires another run; results are not combined across bundles to manufacture a broader row.

실환경 행은 다음 전체 tuple입니다.

`commit + package SHA-256 + Windows build + architecture + server family/version + route + authentication + 선언한 terminal/SFTP/forwarding scenario`.

키 형식, 서버 OS, 알고리즘, proxy topology, forwarding mode, terminal/tool 버전, SFTP workload, 예상 검사는 redaction한 summary에 둡니다. 중요한 차원이 바뀌면 다시 실행해야 하며 서로 다른 bundle 결과를 합쳐 더 넓은 행을 만들지 않습니다.

Promotion rules:

1. **Implemented → Live Validated** requires a real approved run followed by a separate write-once reviewed bundle with `result: Pass`, `redaction_reviewed: true`, a valid `review.json`, every referenced file present, and review against the gate's exit criteria.
2. **Live Validated → Released** requires the identical commit and package SHA-256 in an immutable published release plus the five-asset promotion and `RELEASE-ATTESTATION.json` mapping.
3. `Fail` or `Blocked` never promotes a row and must not be rewritten as `Pass`; rerun evidence is a new immutable bundle.
4. A later failure or security regression can remove a claim. Historical evidence remains preserved and clearly superseded.

The execution order and gate dependencies are defined in [Alpha 4 execution plan](ALPHA4_EXECUTION_PLAN.md). Release evidence requirements are defined in [Release acceptance](RELEASE_ACCEPTANCE.md).

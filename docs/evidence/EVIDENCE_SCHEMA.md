# Sutty live-evidence schema / Sutty 실환경 증거 스키마

Schema version 1 defines a small, strict, credential-free bundle for a real-environment or package gate. It records **what was run**, not a broad product-support conclusion. Support promotion uses [Supported environments](../SUPPORTED_ENVIRONMENTS.md); a manifest `result` is never a support status by itself.

Schema version 1은 실환경 또는 패키지 gate를 위한 작고 엄격한 비밀정보 없는 bundle을 정의합니다. 이는 **무엇을 실행했는지** 기록하며 넓은 제품 지원 결론을 만들지 않습니다. 지원 상태 승격은 [지원 환경](../SUPPORTED_ENVIRONMENTS.md)을 따르고 manifest의 `result`만으로 지원 상태가 되지 않습니다.

## Canonical bundle / 표준 bundle

Every bundle is one directory with these two required files:

```text
<bundle>/
├── manifest.yml
└── summary.json
```

`manifest.yml` is strict UTF-8 and at most 128 KiB. In a committed path, both `<scope>` and `<bundle>` are 1–64 lowercase ASCII letters, digits, or hyphens matching `[a-z0-9][a-z0-9-]{0,63}`. The canonical writer emits the narrower 1–64-character bundle form `[a-z0-9]+(?:-[a-z0-9]+)+`. Filename and extension casing is exact.

`manifest.yml`은 strict UTF-8이며 최대 128 KiB입니다. Committed path의 `<scope>`와 `<bundle>`은 각각 `[a-z0-9][a-z0-9-]{0,63}`에 맞는 1–64자의 소문자 ASCII letter·digit·hyphen입니다. 표준 writer는 이보다 좁은 1–64자 형식 `[a-z0-9]+(?:-[a-z0-9]+)+`의 bundle 이름을 만듭니다. 파일명과 확장자 대소문자는 정확히 일치해야 합니다.

Additional files are allowed only as validated `.json`, strict UTF-8 `.txt`, or structurally constrained `.png` files that are redacted, explicitly referenced by `evidence_files`, and needed to review the declared gate. Raw or unbounded logs, recordings, and other binary formats are not accepted. `manifest.yml` must list `summary.json`; it must not list itself. Every non-manifest file in the directory tree must be declared.

추가 파일은 redaction했고 `evidence_files`에서 명시적으로 참조하며 gate 검토에 필요한 검증된 `.json`, strict UTF-8 `.txt`, 구조를 제한한 `.png`만 허용합니다. 원본 또는 제한 없는 log, 녹화, 그 밖의 binary 형식은 허용하지 않습니다. `manifest.yml`은 `summary.json`을 반드시 나열하고 자기 자신은 나열하지 않습니다. 디렉터리 tree의 manifest 외 모든 파일은 선언해야 합니다.

Reviewed bundles committed for Alpha 4 use exactly one of these roots and add one immutable bundle directory below it. The root validator permits only `EVIDENCE_SCHEMA.md`, the Alpha tracker, the five approved scope trackers, and complete declared bundle trees; orphan files, unknown scopes, empty directories, and undeclared files are rejected.

```text
docs/evidence/alpha4/ssh-auth/<bundle>/
docs/evidence/alpha4/ssh-routes/<bundle>/
docs/evidence/alpha4/ssh-transport/<bundle>/
docs/evidence/alpha4/connection-info/<bundle>/
docs/evidence/alpha4/package/<bundle>/
```

The scope trackers in [alpha4](alpha4/README.md) are not evidence bundles. A committed manifest outside `docs/evidence/alpha<integer>/<scope>/<bundle>/manifest.yml` is rejected. Generated unreviewed output must remain outside these committed roots until review.

[alpha4](alpha4/README.md)의 범위 추적 문서는 증거 bundle이 아닙니다. Root validator는 `EVIDENCE_SCHEMA.md`, Alpha tracker, 승인된 다섯 scope tracker, 완전하게 선언한 bundle tree만 허용하며 orphan file, 알 수 없는 scope, 빈 directory, 선언하지 않은 file을 거부합니다. `docs/evidence/alpha<integer>/<scope>/<bundle>/manifest.yml` 밖의 committed manifest는 거부합니다. 검토 전 생성물은 검토가 끝날 때까지 이 committed root 밖에 둡니다.

Generated bundles remain CI or local artifacts until a human performs the redaction review. A generator must not set `redaction_reviewed: true` merely because an automated pattern scan passed. Do not add invented `Pass` records, copied sample runs, or placeholder evidence to the repository.

생성된 bundle은 사람이 redaction 검토를 마칠 때까지 CI 또는 로컬 산출물입니다. 자동 pattern 검사만 통과했다는 이유로 generator가 `redaction_reviewed: true`를 설정하면 안 됩니다. 꾸며낸 `Pass` 기록, 복사한 예시 실행, placeholder 증거를 저장소에 추가하지 않습니다.

The partial `connection-info`, `smoke`, `fault`, `scale`, and `soak` modes in `tests/sutty.LiveServer.SelfTest` deliberately cannot create a full-gate `Pass`. Their successful automated subsets record `result: Blocked`, with `blocking_category: ManualGateCoverageRequired`. The distinct `direct-password-gate` mode may create an `SSH-LIVE-001` `Pass` only after every declared check succeeds: exact ZIP SHA-256 and safe-entry validation, byte identity between the running and packaged root `sutty.Core.dll`, pinned host identity, password success and rejection, host-key mismatch rejection, command, PTY, SFTP round trip, disconnect cleanup, fresh-session negotiated snapshot, server-side session-category audit, cancellation, and bounded blackhole timeout. A failed run records `Fail`. Never edit a `Blocked` candidate into `Pass`; rerun the complete gate instead.

`tests/sutty.LiveServer.SelfTest`의 부분 `connection-info`, `smoke`, `fault`, `scale`, `soak` mode는 의도적으로 전체 gate `Pass`를 만들 수 없습니다. 성공한 자동 부분 검사는 `blocking_category: ManualGateCoverageRequired`와 함께 `result: Blocked`를 기록합니다. 별도 `direct-password-gate` mode만 모든 선언 check가 성공했을 때 `SSH-LIVE-001` `Pass`를 만들 수 있습니다. 여기에는 정확한 ZIP SHA-256·안전 entry 검사, 실행 중인 `sutty.Core.dll`과 ZIP root entry의 byte 동일성, 고정된 host identity, password 성공·거부, host-key 불일치 거부, command·PTY·SFTP 왕복, disconnect cleanup, 새 session의 협상 snapshot, 서버 측 session-category 감사, 취소, 제한된 blackhole timeout이 모두 포함됩니다. 실행 실패는 `Fail`입니다. `Blocked` 후보를 `Pass`로 편집하지 말고 전체 gate를 다시 실행합니다.

## Candidate-writer activation / Candidate writer 활성화

Evidence output is off by default. Activating it requires all mandatory values below; a partial or invalid configuration fails closed before an SSH connection is attempted. Connection credentials and endpoints are separate runtime inputs and must never be copied into these fields, examples, or evidence.

증거 출력은 기본적으로 꺼져 있습니다. 활성화하려면 아래 필수 값을 모두 제공해야 하며 일부만 있거나 잘못된 설정은 SSH 연결 전에 fail-closed됩니다. 접속 자격증명과 endpoint는 별도 runtime 입력이며 이 필드·예시·증거에 복사하면 안 됩니다.

| Environment variable | Required contract |
| --- | --- |
| `SUTTY_EVIDENCE_OUTPUT_DIR` | Absolute, non-filesystem-root directory outside the committed `docs/evidence` tree. Generate and review candidates there first. |
| `SUTTY_EVIDENCE_APPROVED` | Exactly `1`, as an explicit opt-in to candidate generation. |
| `SUTTY_EVIDENCE_GATE_ID` | 1–64-character gate identifier matching this schema and the single selected mode. |
| `SUTTY_EVIDENCE_COMMIT` | Exact nonzero 40-character lowercase commit that supplied the product and harness code under test. |
| `SUTTY_EVIDENCE_PACKAGE_SHA256` | Exact nonzero lowercase SHA-256 of the identical package under test. An operator must independently calculate and compare it; never substitute a checksum-list digest. |
| `SUTTY_EVIDENCE_SERVER_FAMILY` | Sanitized 1–32-character family label matching the manifest contract. |
| `SUTTY_EVIDENCE_SERVER_VERSION` | Sanitized 1–32-character version label matching the manifest contract. |

The writer does not accept `SUTTY_EVIDENCE_REDACTION_REVIEWED`; supplying it fails closed. Every generated manifest and summary has `redaction_reviewed: false` regardless of the result.

Writer는 `SUTTY_EVIDENCE_REDACTION_REVIEWED`를 받지 않으며 이 값을 제공하면 fail-closed됩니다. 생성한 모든 manifest와 summary는 결과와 관계없이 `redaction_reviewed: false`입니다.

`direct-password-gate` additionally requires an absolute `SUTTY_TEST_PACKAGE_PATH` to the exact x64 ZIP, `SUTTY_TEST_BLACKHOLE_HOST` and `SUTTY_TEST_BLACKHOLE_PORT` for the test-owned silent transport, and `SUTTY_TEST_SERVER_AUDIT_COMMAND=sutty-lab-audit-summary`. It requires Password authentication, a nonempty runtime-only password, an independently provisioned `SUTTY_TEST_HOST_KEY_SHA256`, and forbids trust-new. The disposable approved audit lab is defined under `tests/live-server/openssh`; its runtime password and host keys are never committed.

`direct-password-gate`에는 정확한 x64 ZIP의 절대 경로인 `SUTTY_TEST_PACKAGE_PATH`, test-owned 무응답 transport용 `SUTTY_TEST_BLACKHOLE_HOST`·`SUTTY_TEST_BLACKHOLE_PORT`, `SUTTY_TEST_SERVER_AUDIT_COMMAND=sutty-lab-audit-summary`가 추가로 필요합니다. Password 인증, runtime 전용 password, 독립적으로 준비한 `SUTTY_TEST_HOST_KEY_SHA256`가 필수이고 trust-new는 금지합니다. 승인된 일회용 audit lab은 `tests/live-server/openssh`에 정의하며 runtime password와 host key는 commit하지 않습니다.

SSH evidence generation requires exactly one harness mode. The SSH writer is Direct-only: `direct-password-gate` maps exclusively to the complete `SSH-LIVE-001` gate; `connection-info` maps to `SSH-INFO-001`, `fault` to `SSH-FAULT-001`, and the partial `smoke` mode maps by authentication to `SSH-LIVE-001` through `SSH-LIVE-004` but remains `Blocked`. It cannot generate `ROUTE-LIVE-*`, `TUN-LIVE-001`, or package evidence. `scale` and `soak` execution remains available, but the first 12 Alpha 4 slices approve no evidence activation mapping for those modes.

SSH 증거 생성 시 harness mode는 정확히 하나여야 합니다. SSH writer는 Direct 전용입니다. `direct-password-gate`는 완전한 `SSH-LIVE-001`에만 연결하고, `connection-info`는 `SSH-INFO-001`, `fault`는 `SSH-FAULT-001`, 부분 `smoke`는 인증 방식에 따라 `SSH-LIVE-001`–`SSH-LIVE-004`에 연결하지만 계속 `Blocked`입니다. `ROUTE-LIVE-*`, `TUN-LIVE-001`, package 증거는 만들 수 없습니다. `scale`과 `soak` 실행은 가능하지만 첫 Alpha 4 12개 Slice에 승인된 evidence mapping은 없습니다.

Current CI exercises the writers and validators only with synthetic fixtures; it does not perform UI observations, pass live activation values, or upload a candidate bundle. In `direct-password-gate`, the SSH writer independently hashes the named ZIP, rejects duplicate/unsafe entries, and compares its root `sutty.Core.dll` bytes with the assembly executing the SSH gate. This binds `SSH-LIVE-001` to the tested Core bytes, but it does not prove UI startup.

현재 CI는 synthetic fixture로 writer와 validator만 검사하며 UI를 관찰하거나 live 활성화 값을 전달하거나 candidate bundle을 업로드하지 않습니다. `direct-password-gate`는 지정 ZIP을 독립 hash하고 중복·unsafe entry를 거부하며 ZIP root `sutty.Core.dll`과 SSH gate를 실행하는 assembly의 byte 동일성을 비교합니다. 이는 `SSH-LIVE-001`을 검사한 Core byte에 묶지만 UI 시작을 증명하지는 않습니다.

### `PKG-001` manual recorder / 수동 기록기

After downloading and unpacking the exact sealed x64 Candidate, start the real `sutty.UI.exe`, observe one complete `Alt+1` through `Alt+7` navigation pass with no Windows system sound, and close the UI cleanly. Then record only the observed outcomes:

```powershell
.\.github\scripts\Write-PackageEvidence.ps1 `
  -PackagePath <absolute\Sutty-v0.1.0-alpha.4-win-x64.zip> `
  -ObservedUiPath <absolute\unpacked\sutty.UI.exe> `
  -Tag v0.1.0-alpha.4 `
  -Commit <candidate-commit> `
  -EvidenceOutputRoot <absolute-unreviewed-root> `
  -StartedAtUtc <RFC3339-UTC> `
  -DurationSeconds <positive-integer> `
  -UiStartupResult Pass `
  -AltNavigationSilentResult Pass `
  -AltNavigationShortcutCount 7 `
  -UiShutdownResult Pass
```

The recorder never starts or controls the UI and cannot infer a manual result. It validates Windows x64/build, the exact Alpha filename, safe bounded ZIP entries, root `sutty.UI.exe`, the exact six-line `BUILDINFO.txt` tag/commit/x64 identity, and calculates the locked ZIP SHA-256 itself. `ObservedUiPath` is the operator's declaration of the unpacked executable they observed and selects its parent as the observation root. After the UI is closed, the recorder requires that root's complete physical tree to match the locked ZIP inventory exactly by case-sensitive relative path, file size, and SHA-256; extra, missing, mutated, non-portable, file/directory-colliding, symbolic-link, and reparse-point content is rejected. This full-tree identity is not process-launch provenance, and local paths and per-file hashes are not serialized. The shortcut count is the number actually attempted: a silent `Pass` requires exactly seven, a `Fail` requires at least one, and a startup failure requires zero with later checks `Blocked`. It writes a fresh `redaction_reviewed: false` bundle outside `docs/evidence`. `Fail` and `Blocked` remain honest reviewable records and never satisfy release promotion.

봉인된 exact x64 Candidate를 내려받아 압축 해제한 뒤 실제 `sutty.UI.exe`를 시작하고 `Alt+1`부터 `Alt+7`까지 한 차례 전환할 때 Windows 시스템음이 없는지 관찰한 다음 UI를 정상 종료합니다. 기록기는 UI를 실행·제어하거나 수동 결과를 추론하지 않습니다. Windows x64/build, 정확한 Alpha 파일명, 안전하고 크기를 제한한 ZIP entry, root `sutty.UI.exe`, 정확한 6줄 `BUILDINFO.txt` tag/commit/x64 identity를 검증하고 잠근 ZIP의 SHA-256을 직접 계산합니다. `ObservedUiPath`는 운영자가 관찰했다고 선언한 압축 해제 실행 파일이며 그 parent가 관찰 root입니다. UI 종료 후 기록기는 이 root의 전체 physical tree가 ZIP inventory와 대소문자를 포함한 상대 경로, 파일 크기, SHA-256까지 정확히 같아야 한다고 요구합니다. 추가·누락·변조·비이식 경로·file/directory 충돌·symbolic link·reparse point는 거부합니다. 이 full-tree identity도 process 실행 provenance를 증명하지 않으며 로컬 경로와 파일별 hash는 기록하지 않습니다. Shortcut count는 실제 시도 횟수이며 무음 `Pass`는 정확히 7, `Fail`은 1 이상, startup 실패는 0과 이후 check `Blocked`를 요구합니다. 그 뒤 `docs/evidence` 밖에 새 `redaction_reviewed: false` bundle을 만듭니다. `Fail`과 `Blocked`는 정직한 검토 가능 기록으로 남지만 release promotion을 만족하지 않습니다.

## Post-run human review / 실행 후 사람 검토

Review is a separate, write-once transformation. Inspect the complete unreviewed source bundle first, then run:

```powershell
.\.github\scripts\Review-LiveEvidence.ps1 `
  -SourceManifestPath <candidate\manifest.yml> `
  -DestinationRoot <fresh-reviewed-root> `
  -ReviewerId github-<public-actor> `
  -ReviewedAtUtc <RFC3339-UTC> `
  -PrivacyReview Confirmed `
  -ManualObservationReview Confirmed `
  -ExpectedCommit <40-lower-hex> `
  -ExpectedPackageSha256 <64-lower-hex> `
  -RequiredGateId PKG-001 `
  -RequiredResult Pass
```

Use `PKG-001` for the manual package recorder above and `SSH-LIVE-001` for the complete direct-password SSH gate. `-ManualObservationReview Confirmed` is mandatory only for `PKG-001` and records that the reviewer checked the declared manual startup, shortcut-silence, shutdown, attempted-count, and full package-tree identity assertions; omit it for every other gate. The command refuses an already reviewed source, validates the source bundle, copies it to a new non-existing directory, changes only the manifest/summary review boolean, declares a new `review.json`, validates the result, and atomically publishes the new reviewed directory. It never edits the source bundle or an existing destination. `ReviewerId` is the public GitHub actor (`github-` plus a valid 1–39-character username), not a display name, email address, machine account, or secret.

검토는 별도 write-once 변환입니다. 먼저 검토 전 source bundle 전체를 사람이 확인하고 위 명령을 실행합니다. 위 package 기록기에는 `PKG-001`, 완전한 direct-password SSH gate에는 `SSH-LIVE-001`을 사용합니다. `-ManualObservationReview Confirmed`는 `PKG-001`에서만 필수이며 reviewer가 수동 startup, shortcut 무음, shutdown, 실제 시도 횟수, 전체 package-tree identity assertion을 확인했음을 기록합니다. 다른 gate에서는 이 parameter를 생략합니다. 명령은 이미 reviewed인 source를 거부하고 source bundle을 검증한 뒤 존재하지 않는 새 directory로 복사하며, manifest/summary의 review boolean 변경과 새 `review.json` 선언만 수행합니다. 결과를 다시 검증한 뒤 새 directory를 원자적으로 게시하고 source bundle이나 기존 destination은 수정하지 않습니다. `ReviewerId`는 공개 GitHub actor(`github-` + 유효한 1–39자 username)이며 display name, email, machine account, secret이 아닙니다.

`review.json` is strict UTF-8 JSON and contains exactly:

| Property | Exact contract |
| --- | --- |
| `schema_version` | JSON integer `1`. |
| `reviewer_id` | `github-` plus a canonical 1–39-character GitHub username. |
| `reviewed_at_utc` | RFC 3339 UTC timestamp ending in `Z`. |
| `source_bundle_sha256` | Lowercase SHA-256 of the canonical source-file record lines. |
| `source_files` | Ordinally sorted exact records `{name, sha256, size_bytes}` for the original `manifest.yml` and every originally declared evidence file; `review.json` is excluded. |
| `review_scope` | Exactly `["privacy-redaction", "bundle-integrity"]`. |
| `manual_observation_confirmed` | Required only for `PKG-001` and exactly JSON boolean `true`; forbidden for every other gate. It is the reviewer's explicit confirmation of the manual observation assertions, not automated process provenance. |

Each source line is UTF-8 `<sha256> <size_bytes> <name>\n` with one ASCII space between fields. The aggregate hash and every record are immutable review attestations; they are not permission to recover, attach, or publish secret source material.

각 source line은 field 사이에 ASCII space 하나를 둔 UTF-8 `<sha256> <size_bytes> <name>\n`입니다. Aggregate hash와 각 record는 변경 불가능한 검토 attestation이며 secret source 자료를 복원·첨부·공개할 권한이 아닙니다.

## `manifest.yml` exact contract / 정확한 계약

The top level is a flat mapping. Schema version 1 permits exactly the following keys; unknown, duplicate, nested, merged, aliased, tagged, or flow-style YAML is rejected. Key spelling and enum casing are normative.

최상위는 평면 mapping입니다. Schema version 1은 아래 key만 허용하며 알 수 없는 key, 중복, 중첩, merge, alias, tag, flow-style YAML을 거부합니다. Key 철자와 enum 대소문자는 계약의 일부입니다.

| Key | Required type and value | Meaning and safety rule |
| --- | --- | --- |
| `schema_version` | Integer `1` | This contract version. |
| `gate_id` | 1–64 characters, with uppercase/digit segments matching `[A-Z0-9]+(?:-[A-Z0-9]+)+` | Stable hyphenated gate identifier from the reviewed execution plan or acceptance checklist. It must not contain a hostname, username, ticket secret, or customer name. |
| `commit` | Exactly 40 lowercase hexadecimal characters, not all zeroes | Git commit whose product and harness code ran. |
| `package_sha256` | Exactly 64 lowercase hexadecimal characters, not all zeroes | SHA-256 of the exact package under test, not `SHA256SUMS.txt` and not a locally edited replacement. |
| `windows_build` | Numeric build `#####`, optional `10.0.` prefix, and optional 1–6 digit revision | Sanitized Windows build such as `10.0.26100.0`; exclude machine name, installation ID, tenant, and user. |
| `architecture` | `x64` or `arm64` | `RuntimeInformation.ProcessArchitecture` of the harness or recorder. `PKG-001` additionally requires the matching exact x64 filename and `BUILDINFO.txt`; this field alone is not process-launch provenance. OS architecture alone is not package evidence. |
| `server_family` | 1–32 characters matching an ASCII letter followed by letters, digits, `_`, `+`, or `-` | Sanitized implementation family only; never a DNS name, IP address, inventory name, or provider/customer identifier. Exactly `NotApplicable` is reserved for `PKG-001`. |
| `server_version` | 1–32 characters using ASCII letters, digits, `.`, `_`, `+`, `~`, or `-`; first character alphanumeric | Sanitized server software version/build only. Exactly `NotApplicable` is reserved for `PKG-001`. |
| `route` | `Direct`, `HttpConnect`, `Socks4`, `Socks5`, `SshJump`, `ExternalProxyCommand`, or package-only `NotApplicable` | Route used by the tested primary SSH connection. Do not include proxy/jump endpoints or expanded commands. |
| `authentication` | `Password`, `PublicKey`, `Agent`, `KeyboardInteractive`, or package-only `NotApplicable` | Top-level authentication category. Key format or prompt details belong only in the redacted summary. |
| `expected_host_fingerprint` | Exactly `SHA256:[redacted]` or `NotRecorded` | Use the redacted marker when an independently provisioned fingerprint was compared. `NotRecorded` is allowed only for explicitly isolated trust-new fixtures or the server-free `PKG-001` tuple; it is not acceptable for an approved persistent server gate. |
| `result` | `Pass`, `Fail`, or `Blocked` | Outcome of this run only. `Pass` requires every assertion in the declared gate; partial success is `Fail` or `Blocked`, never `Pass`. |
| `started_at_utc` | RFC 3339 UTC timestamp ending in `Z` | Use `YYYY-MM-DDTHH:mm:ssZ` or 1–7 fractional second digits. It identifies the run, not a local time zone. |
| `duration_seconds` | Nonnegative integer | Monotonic elapsed duration rounded up to whole seconds. There is intentionally no `completed_at` field in version 1. |
| `evidence_files` | Non-empty YAML sequence of `.json`, `.txt`, or `.png` path strings | Relative paths inside the bundle. `summary.json` is mandatory exactly once. Paths must be unique case-insensitively, use `/`, resolve inside the bundle, exist as files, use portable ASCII path segments, and contain no drive, root, reserved name, `.` or `..` segment. |
| `redaction_reviewed` | Canonical YAML boolean `true` or `false` | Human redaction-review state. The committed-evidence validator requires `true`; generated `false` bundles remain unaccepted outside the committed roots. |

No other manifest fields are allowed. `schema_version`, `duration_seconds`, and `redaction_reviewed` are canonical unquoted YAML integer/boolean scalars; string fields and `evidence_files` items may be canonical plain scalars or JSON-style double-quoted strings. Comments, tabs, single-quoted strings, YAML anchors, aliases, tags, merges, block scalars, and list items outside `evidence_files` are also rejected. In particular, `status`, `scenario`, `completed_at`, `notes`, `host`, `user`, `path`, `command`, `output`, and `transcript` are not schema-version-1 fields. Scenario detail belongs in the bounded redacted summary; support status belongs in the supported-environment matrix.

다른 manifest 필드는 허용하지 않습니다. `schema_version`, `duration_seconds`, `redaction_reviewed`는 인용하지 않은 표준 YAML integer/boolean scalar여야 하며 string field와 `evidence_files` item은 표준 plain scalar 또는 JSON 방식 double-quoted string을 사용할 수 있습니다. 주석, tab, single-quoted string, YAML anchor·alias·tag·merge, block scalar, `evidence_files` 밖의 list item도 거부합니다. 특히 `status`, `scenario`, `completed_at`, `notes`, `host`, `user`, `path`, `command`, `output`, `transcript`는 schema version 1 필드가 아닙니다. Scenario 상세는 제한된 redacted summary에, 지원 상태는 지원 환경 표에 둡니다.

## `summary.json` review contract / 검토 계약

`summary.json` must be one valid JSON object intended for human review. Schema version 1 fixes the following required envelope as a compatibility contract:

| Property | Exact contract |
| --- | --- |
| `schema_version` | JSON integer `1`. |
| `gate_id` | JSON string exactly equal to the manifest value. |
| `result` | JSON string exactly equal to the manifest `Pass`, `Fail`, or `Blocked` value. |
| `started_at_utc` | JSON string exactly equal to the manifest timestamp. |
| `duration_seconds` | JSON integer exactly equal to the manifest nonnegative integer. |
| `redaction_reviewed` | JSON boolean exactly equal to the manifest boolean. |
| `privacy_notice` | Exactly `Connection identifiers, credentials, filesystem locations, session content, and cryptographic material are excluded.` |
| `checks` | Array of 1–64 JSON objects. Every item contains exactly one string `id` matching `[a-z0-9][a-z0-9-]{0,63}` and exactly one string `result` from `Pass`, `Fail`, or `Blocked`; IDs are unique with ordinal comparison. |

Manifest and check results must agree semantically:

- `Pass` requires every check to be `Pass`.
- `Fail` requires at least one `Fail` check.
- `Blocked` requires at least one `Blocked` check, or exactly one string `blocking_category` matching `[A-Za-z0-9][A-Za-z0-9_-]{0,63}`.

The canonical writer preserves progress on a failed full gate: completed checks are `Pass`, the exact failing check is `Fail`, and later checks are `Blocked`; `failed_check_id` identifies that single failing check. It does not rewrite earlier successful assertions as failures. Bounded `measurements` contain only safe counts and asserted aggregate values, never endpoint or session content.

Exact duplicate property names are rejected recursively in the root, check objects, scenario objects, and every other nested JSON object. The required envelope and result semantics are stable; additional bounded, non-identifying scenario fields are extensible and are not a compatibility API. Such fields may state the harness mode or package operation and sanitized categories such as key format, forwarding mode, terminal/tool version, SFTP workload, or failure-injection method. They remain subject to duplicate-property, forbidden-property, identifying-text, and redaction checks, must not conflict with the envelope, and must leave the summary useful without optional attachments.

`summary.json`은 사람이 검토할 하나의 유효한 JSON object여야 합니다. Schema version 1은 위 필수 envelope를 호환성 계약으로 고정합니다. `schema_version`은 JSON integer `1`이고 `gate_id`·`result`·`started_at_utc`·`duration_seconds`·`redaction_reviewed`는 JSON type과 값이 manifest와 정확히 같아야 하며 canonical `privacy_notice` 문장도 정확히 일치해야 합니다. `checks`는 1–64개이고 각 항목에 고유한 1–64자 소문자 ASCII/hyphen `id`와 `Pass|Fail|Blocked` `result`가 정확히 하나씩 있어야 합니다.

- Manifest `Pass`이면 모든 check가 `Pass`여야 합니다.
- Manifest `Fail`이면 `Fail` check가 하나 이상 있어야 합니다.
- Manifest `Blocked`이면 `Blocked` check가 하나 이상 있거나 `[A-Za-z0-9][A-Za-z0-9_-]{0,63}` 형식의 string `blocking_category`가 정확히 하나 있어야 합니다.

표준 writer는 전체 gate 실패 시 진행 상태를 보존합니다. 완료한 check는 `Pass`, 정확한 실패 지점은 `Fail`, 이후 실행하지 않은 check는 `Blocked`이며 `failed_check_id`는 그 단일 실패 check를 가리킵니다. 앞서 성공한 assertion을 실패로 다시 쓰지 않습니다. 제한된 `measurements`에는 안전한 count와 assertion을 거친 aggregate 값만 포함하며 endpoint나 session 내용은 넣지 않습니다.

Root와 모든 중첩 JSON object에서 정확히 같은 property 이름의 중복을 재귀적으로 거부합니다. 필수 envelope와 결과 의미만 안정된 계약입니다. 그 밖의 제한되고 식별 정보 없는 scenario field는 확장 가능하며 호환성 API가 아닙니다. 추가 field도 중복·금지 property·식별 text·redaction 검사를 통과하고 envelope와 충돌하지 않아야 하며 선택 attachment 없이도 summary 자체로 검토할 수 있어야 합니다.

### `SSH-LIVE-001` Pass profile / Pass 프로필

An `SSH-LIVE-001` `Pass` is stricter than a generic summary, even during a whole-root scan. Its `checks` array must contain these 12 entries exactly once, in writer execution order, and every result must be `Pass`:

`package-sha256`, `package-commit-identity`, `package-core-identity`, `authentication-success`, `command-pty-sftp`, `remote-local-cleanup`, `negotiated-reconnect`, `server-session-audit`, `authentication-rejection`, `host-key-rejection`, `connection-cancellation`, `transport-timeout`.

Its `measurements` object contains exactly 25 fields. The following 14 booleans must be JSON `true`: `package_sha256_verified`, `package_commit_identity_verified`, `package_core_identity_verified`, `authentication_success_verified`, `sftp_checksum_verified`, `command_pty_sftp_verified`, `remote_cleanup_verified`, `local_cleanup_verified`, `reconnect_verified`, `server_audit_verified`, `authentication_rejection_verified`, `host_key_rejection_verified`, `cancellation_verified`, and `timeout_verified`.

The canonical nonnegative integer values are `check_count=12`, `passed_count=12`, `failed_count=0`, `blocked_count=0`, `sftp_bytes=65536`, `audit_exec_count=4`, `audit_shell_count=1`, `audit_sftp_count=2`, and `audit_other_count=0`. `cancellation_elapsed_milliseconds` is at least 100 and below 10,000; `timeout_elapsed_milliseconds` is at least 12,000 and below 30,000. Decimal, exponent, negative, string, missing, or extra values are rejected. `Fail` and `Blocked` bundles may retain partial measurements; they never satisfy the release Pass profile.

`SSH-LIVE-001` `Pass`는 전체 root scan에서도 일반 summary보다 엄격합니다. `checks`에는 위 12개 ID가 writer 실행 순서대로 정확히 한 번씩 있고 모두 `Pass`여야 합니다. `measurements`에는 정확히 25개 field만 허용합니다. 위 14개 검증 boolean은 JSON `true`, count·byte·audit 값은 명시한 canonical nonnegative integer여야 하며 cancellation은 100ms 이상 10,000ms 미만, timeout은 12,000ms 이상 30,000ms 미만이어야 합니다. Decimal·지수·음수·string·누락·추가 값은 거부합니다. `Fail`과 `Blocked` bundle은 부분 측정치를 보존할 수 있지만 release Pass profile을 만족하지 않습니다.

### `PKG-001` Pass profile / Pass 프로필

A `PKG-001` `Pass` is x64-only and requires Windows build 26100 or newer. Its server family/version, route, and authentication are exactly `NotApplicable`; those values are rejected for every other gate. Its server-free fingerprint value is exactly `NotRecorded`. Its `checks` are exactly, in order:

`package-sha256`, `package-commit-identity`, `package-tree-identity`, `ui-startup`, `alt-navigation-silent`, `ui-shutdown`.

All six results are `Pass`. Its `measurements` object contains exactly eleven properties: JSON boolean `true` values for `package_sha256_verified`, `package_commit_identity_verified`, `package_tree_identity_verified`, `ui_startup_verified`, `alt_navigation_silent_verified`, and `ui_shutdown_verified`; canonical JSON integers `check_count=6`, `passed_count=6`, `failed_count=0`, `blocked_count=0`, and `alt_navigation_shortcut_count=7`. Missing, extra, reordered, false, string, decimal, or exponent values are rejected. This profile records a person's observations; fixtures and automation cannot create a real `Pass`.

`PKG-001` `Pass`는 x64 전용이며 Windows build 26100 이상이 필요합니다. Server family/version, route, authentication은 정확히 `NotApplicable`이고 이 값은 다른 gate에서 거부됩니다. Server가 없으므로 `expected_host_fingerprint`는 정확히 `NotRecorded`입니다. Check는 위 여섯 개가 정해진 순서로 한 번씩 모두 `Pass`여야 합니다. `measurements`는 명시한 여섯 JSON boolean `true`와 `check_count=6`, `passed_count=6`, `failed_count=0`, `blocked_count=0`, `alt_navigation_shortcut_count=7`의 canonical JSON integer, 총 11개 property만 포함합니다. 누락·추가·순서 변경·false·string·decimal·지수 값은 거부합니다. 이 profile은 사람의 관찰을 기록하며 fixture와 자동화는 실제 `Pass`를 만들 수 없습니다.

## Attachment validation / Attachment 검증

Every attachment remains subject to the forbidden-content rules and human review below. File-format validation reduces parsing and metadata risk; it does not prove that visible text or pixels are safely redacted.

모든 attachment에는 아래 금지 내용 규칙과 사람 검토가 그대로 적용됩니다. 파일 형식 검증은 parsing·metadata 위험을 줄이지만 보이는 글자나 pixel이 안전하게 redaction됐음을 증명하지 않습니다.

| Extension | Automated boundary | Human-review boundary |
| --- | --- | --- |
| `.json` | At most 1 MiB, strict UTF-8, one valid JSON object, recursive forbidden property/value and identifying-text scan. | Check that bounded names, outcomes, counts, and aggregate values cannot identify an endpoint or expose operational content. |
| `.txt` | At most 1 MiB, strict UTF-8, no NUL or ASCII control character other than tab/CR/LF, and the same secret/identity/path/transcript pattern scan. | Accept only a short redacted extract needed for the gate. Raw, complete, streaming, or unbounded logs remain forbidden. |
| `.png` | At most 5 MiB; valid PNG signature and IHDR bit-depth/color/compression/filter/interlace fields; dimensions from 1×1 through 16384×16384 and at most 67,108,864 pixels; at most 4,096 chunks; valid required chunks, lengths, order, and CRC; only `IHDR`, `PLTE`, `IDAT`, `IEND`, `tRNS`, `sRGB`, `gAMA`, `cHRM`, and `pHYs` chunks. Unknown/text/time/profile metadata chunks and bytes after `IEND` are rejected. | Inspect every pixel for endpoints, usernames, machine names, fingerprints, paths, command/output text, notifications, window chrome, and other identifying context; crop or redact before acceptance. |

PNG ancillary chunks are single-instance and must precede `IDAT`; `sRGB`, `gAMA`, and `cHRM` must also precede `PLTE`. `sRGB` is exactly one byte with intent 0–3. `gAMA` is exactly four bytes with a value from 1 through 1,000,000. `cHRM` is exactly 32 bytes; each coordinate and each x+y pair must be at most 100,000. `pHYs` is exactly nine bytes with nonzero x/y values no greater than 1,000,000 and unit 0 or 1. `PLTE` is forbidden for grayscale color types, precedes `IDAT`, and contains 1–256 three-byte entries; indexed color requires it. `tRNS` is single-instance, precedes `IDAT`, and its length must match the PNG color type and palette size. `IDAT` chunks must be consecutive.

PNG ancillary chunk는 한 번만 나타나고 `IDAT`보다 앞서야 하며 `sRGB`, `gAMA`, `cHRM`은 `PLTE`보다도 앞서야 합니다. `sRGB`는 정확히 1 byte이고 intent는 0–3입니다. `gAMA`는 정확히 4 byte이며 값은 1–1,000,000입니다. `cHRM`은 정확히 32 byte이고 각 좌표와 각 x+y 쌍은 100,000 이하여야 합니다. `pHYs`는 정확히 9 byte이고 0이 아닌 x/y 값은 1,000,000 이하, unit은 0 또는 1입니다. `PLTE`는 grayscale color type에서 금지하고 `IDAT`보다 앞서며 3 byte entry 1–256개를 포함하고 indexed color에서는 필수입니다. `tRNS`는 한 번만 나타나고 `IDAT`보다 앞서며 길이는 PNG color type과 palette 크기에 맞아야 합니다. `IDAT` chunk는 연속돼야 합니다.

All formats must be regular files, not symbolic links or reparse points. The evidence root, bundle, and traversed directory ancestry must also be physical directories. Automated validation cannot set `redaction_reviewed: true`; that decision remains human.

모든 형식은 symbolic link나 reparse point가 아닌 일반 파일이어야 하며 evidence root, bundle, 탐색하는 directory ancestry도 실제 디렉터리여야 합니다. 자동 검증은 `redaction_reviewed: true`를 설정할 수 없고 이 결정은 사람에게 남습니다.

## Redaction rules / Redaction 규칙

The following data is forbidden from every manifest, summary, filename, directory name, attachment, screenshot, and archive:

- passwords, passphrases, private keys, raw public/host keys, tokens, OTP values, cookies, vault material, and environment-secret values;
- DNS names, IP addresses, usernames, email addresses, machine names, tenant/customer/provider inventory identifiers, and production aliases;
- actual host-key fingerprints; record only `SHA256:[redacted]` or the narrowly allowed `NotRecorded` marker;
- local or remote paths, filenames originating from a real system, proxy/jump endpoints, and expanded ProxyCommand strings;
- terminal transcripts, command text/output, SFTP file content, directory listings, exception messages, stack traces, packet captures, and unbounded logs;
- screenshots or recordings containing any forbidden value, even if the main text files are clean.

다음 데이터는 manifest, summary, 파일명, 디렉터리명, attachment, screenshot, archive 어디에도 넣지 않습니다.

- password, passphrase, private key, raw public/host key, token, OTP 값, cookie, vault 자료, 환경 secret 값
- DNS 이름, IP 주소, 사용자 이름, email, machine 이름, tenant/customer/provider inventory 식별자, 운영 alias
- 실제 host-key 지문. `SHA256:[redacted]` 또는 제한적으로 허용한 `NotRecorded`만 기록
- 로컬·원격 경로, 실제 시스템에서 온 파일명, proxy/jump endpoint, 확장된 ProxyCommand 문자열
- terminal transcript, command text/output, SFTP 파일 내용, directory listing, exception message, stack trace, packet capture, 제한 없는 log
- 본문 파일이 안전해도 금지 값이 보이는 screenshot 또는 녹화

Allowed evidence is deliberately narrow: the exact commit and package hash, Windows build and architecture, sanitized server family/version, enum route/authentication, redacted fingerprint marker, duration, bounded check identifiers/outcomes, counts, sizes, and aggregate performance values that cannot identify a system.

허용 증거는 의도적으로 제한합니다. 정확한 commit·package hash, Windows build·architecture, 정제한 server family/version, route/authentication enum, redacted fingerprint marker, duration, 제한된 검사 식별자·결과, 시스템을 식별할 수 없는 count·size·aggregate 성능 값만 기록합니다.

## Acceptance and immutability / 인수와 불변성

1. Automated validation checks structure and obvious forbidden patterns; it does not replace human redaction review.
2. `result: Pass` with `redaction_reviewed: false` is an unaccepted generated artifact, not **Live Validated** evidence.
3. `Fail` and `Blocked` bundles may be retained after redaction to explain gaps, but they never promote a support row.
4. An accepted bundle is immutable. Correct a mistake or rerun a gate by creating a new bundle; do not rewrite a prior `Pass`, `Fail`, or `Blocked` result.
5. `Assert-EvidenceHistory.ps1` compares a Git base and head and permits only new bundle directories. Any file change, deletion, rename, or addition below a bundle that existed at the base is rejected; a correction is another bundle plus a tracker reference.
6. **Released** additionally requires an immutable published artifact whose commit and SHA-256 equal both the accepted `SSH-LIVE-001` and `PKG-001` bundles. `RELEASE-ATTESTATION.json` binds the candidate workflow artifact, four sealed candidate files, acceptance commit, both reviewed manifest/review hashes, each declared reviewer/time and gate, and the promotion run. The promotion workflow separately byte-verifies the attestation itself and the exact five-asset public inventory.
7. Evidence is scoped to one declared gate and exact matrix tuple. Multiple partial bundles cannot be combined into a synthetic `Pass`.

Validation and execution order are defined by [Alpha 4 execution plan](../ALPHA4_EXECUTION_PLAN.md). Current claims are listed in [Supported environments](../SUPPORTED_ENVIRONMENTS.md), and release gates are listed in [Release acceptance](../RELEASE_ACCEPTANCE.md).

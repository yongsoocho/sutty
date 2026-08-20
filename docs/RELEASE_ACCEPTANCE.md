# Sutty release acceptance / 출시 인수 기준

This checklist separates implemented code from release evidence. A checked source-level item is not a GA claim until the corresponding live or packaging gate has a dated result.

이 체크리스트는 구현 코드와 출시 증거를 구분합니다. 소스 수준 항목이 완료되어도 해당 실서버·패키징 검증 결과가 날짜와 함께 남기 전에는 GA 완료로 간주하지 않습니다.

Compatibility states and exact matrix boundaries are authoritative in [Supported environments](SUPPORTED_ENVIRONMENTS.md). Every real-environment or package result must use the strict, redacted, immutable bundle in [EVIDENCE_SCHEMA.md](evidence/EVIDENCE_SCHEMA.md), following the dependency order in the [Alpha 4 execution plan](ALPHA4_EXECUTION_PLAN.md).

호환성 상태와 정확한 matrix 경계는 [지원 환경](SUPPORTED_ENVIRONMENTS.md)을 기준으로 합니다. 모든 실환경·패키지 결과는 [증거 스키마](evidence/EVIDENCE_SCHEMA.md)의 엄격하고 redaction한 불변 bundle을 사용하고 [Alpha 4 실행 계획](ALPHA4_EXECUTION_PLAN.md)의 의존성 순서를 따라야 합니다.

## P0 transfer gates / P0 전송 게이트

| Gate | Current state | Required release evidence |
| --- | --- | --- |
| Multi SFTP 1→N | Implemented | 16 live targets, isolated progress/result, and failed-target-only retry record |
| Multi SFTP N→1 | Implemented | Same-name files from multiple servers remain isolated in deterministic server folders |
| Recursive directories | Implemented | Empty folders, Unicode paths, deep paths, and 100,000-file live run |
| Retry, resume, checkpoint | Implemented | Transport loss during transfer resumes from a non-zero offset |
| Final verification | Implemented | Safe mode rejects size/SHA-256 mismatches before final promotion; fast mode verifies final size only and must be selected explicitly |
| Queue restoration | Implemented | Kill and restart the app; completed targets remain completed and only incomplete targets are offered |
| Multi default selection | Implemented | Opening Multi selects zero sessions; restored jobs also require explicit session checks |

## P1 connection gates / P1 연결 게이트

Run against each supported server family and record the server version, route, authentication method, and result.

- Windows SSH Agent with a real service and key.
- Repeated keyboard-interactive prompts, including OTP/MFA cancellation and timeout.
- Encrypted and unencrypted PPK v2/v3 fixtures owned by the test environment.
- SSH jump, ProxyCommand, direct, HTTP CONNECT, SOCKS4, and SOCKS5 routes.
- Local, remote, and dynamic forwarding lifecycle, bind failure, and disconnect cleanup.
- Saved-host route and tunnel reload without plaintext credentials in JSON or SQLite.
- OpenSSH config, Windows saved-session registry, and legacy INI import with duplicate suppression and no secret import.

## Credentialed live harness / 자격증명 실서버 테스트

The project `tests/sutty.LiveServer.SelfTest` uses only environment-provided credentials. It fails closed unless an expected SHA-256 host-key fingerprint is supplied or new-host trust is explicitly enabled for an isolated test server.

Modes:

- `direct-password-gate`: the complete `SSH-LIVE-001` Direct+Password gate for one approved test-owned audit lab; it verifies exact ZIP/Core identity, pinned host identity, success/rejection/cancellation/timeout, command, PTY, basic SFTP round trip, reconnect snapshot, server audit counts, and cleanup.
- `connection-info`: connects without exec, PTY, or SFTP file operations and checks negotiated fields, disconnect clearing, and reconnect freshness; the session may still initialize its SFTP subsystem.
- `smoke`: SSH command, PTY UTF-8, tool availability, recursive SFTP, tree enumeration, and checksum.
- `fault`: disconnects transport during upload, reconnects, and requires a non-zero resume offset.
- `scale`: defaults to a real 100 GB transfer and 100,000-file directory test.
- `soak`: defaults to 16 sessions for 60 minutes with repeated commands.

When evidence output is enabled, select exactly one mode. Successful partial modes remain `Blocked` with `ManualGateCoverageRequired`. Only `direct-password-gate` may emit `SSH-LIVE-001` `Pass`, and only when the pinned Password target, test-owned blackhole and canonical server audit are configured, the exact absolute ZIP hash matches the manifest input, the running Core is byte-identical to the package root entry, and every gate check succeeds. This does not replace the separate exact-package UI startup required by `PKG-001`. In particular, `connection-info` remains a partial `SSH-INFO-001` check and does not replace the server-side no-exec audit, unexpected-fault clearing, or approved indirect-route run below.

증거 출력을 켤 때는 mode를 정확히 하나만 선택합니다. 성공한 부분 mode는 계속 `ManualGateCoverageRequired`인 `Blocked`입니다. `direct-password-gate`만 `SSH-LIVE-001` `Pass`를 만들 수 있으며, 고정된 Password target, test-owned blackhole, canonical server audit, manifest 입력과 일치하는 exact absolute ZIP hash, package root entry와 byte-identical한 실행 Core, 모든 gate check가 필요합니다. 이는 별도 `PKG-001` exact-package UI 시작 검사를 대신하지 않습니다. 특히 `connection-info`는 부분 `SSH-INFO-001` 검사이며 아래 서버 측 no-exec 감사, 예기치 않은 fault 뒤 정보 제거, 승인된 간접 route 실행을 대신하지 않습니다.

Run locally after setting the `SUTTY_TEST_SSH_HOST`, `SUTTY_TEST_SSH_USER`, authentication, and `SUTTY_TEST_HOST_KEY_SHA256` environment variables:

```powershell
$env:SUTTY_TEST_MODES = 'smoke,fault'
dotnet run --project .\tests\sutty.LiveServer.SelfTest\sutty.LiveServer.SelfTest.csproj --configuration Release
```

The same harness is available through the manually dispatched Windows CI workflow. Repository secrets carry credentials; repository variables control session count, soak minutes, transfer GB, file count, and fault payload size. Do not enable `scale` or `soak` on an unapproved server.

### Pending SSH negotiation-information acceptance / SSH 협상 정보 인수 대기 항목

This slice is not complete live evidence until it is run against an approved SSH server and the result is recorded below. Use an account/server whose SSH exec requests can be inspected when possible:

1. Clear or mark the server-side SSH command audit, then connect in Sutty without opening REPL, Files, or an interactive terminal.
2. Open **SSH connection information** and verify server/client identification, KEX, host-key algorithm and SHA-256 fingerprint, both cipher/MAC directions, and compression. Compare the fingerprint with the independently provisioned expected value.
3. Disconnect and confirm the connection-information action is unavailable; reconnect and confirm every displayed value belongs to the new handshake rather than the prior snapshot. Separately interrupt the primary transport and confirm the action is disabled, owned terminal/SFTP/forwarding resources close, and the session reaches `Failed` with the original error retained.
4. Confirm the connection alone issued no exec request, including `uname -a`, `pwd`, or home-directory discovery commands.
5. Repeat for Direct and at least one approved indirect route. Record only the sanitized server family/version, route, authentication category, Windows build/architecture, `SHA256:[redacted]` comparison marker, and redacted result; do not record endpoints, credentials, usernames, actual fingerprints, raw host keys, paths, transcripts, or command output.

승인된 SSH 서버에서 실행하고 아래 결과를 기록하기 전에는 이 Slice를 실환경 완료 증거로 보지 않습니다. 가능하면 SSH exec 요청을 확인할 수 있는 계정·서버를 사용합니다.

1. 서버 측 SSH 명령 감사 기록을 비우거나 시작 지점을 표시한 뒤 REPL·Files·대화형 터미널을 열지 않고 Sutty로 연결합니다.
2. **SSH 연결 정보**를 열어 서버·클라이언트 식별, KEX, 호스트 키 알고리즘과 SHA-256 지문, 양방향 cipher·MAC, 압축을 확인하고 지문을 독립적으로 준비한 예상값과 비교합니다.
3. 연결을 끊으면 연결 정보 동작이 비활성화되는지 확인하고, 다시 연결해 모든 표시값이 이전 snapshot이 아니라 새 handshake에서 왔는지 확인합니다. 별도로 주 transport를 중단해 연결 정보가 비활성화되고 소유한 terminal·SFTP·forwarding resource가 닫히며 원래 오류를 보존한 `Failed` 상태에 도달하는지 확인합니다.
4. 연결만으로 `uname -a`, `pwd`, 홈 디렉터리 탐색을 포함한 exec 요청이 발생하지 않았는지 확인합니다.
5. Direct와 승인된 간접 route 하나 이상에서 반복합니다. 정제한 server family/version, route, 인증 범주, Windows build/architecture, `SHA256:[redacted]` 비교 marker, redaction한 결과만 기록하며 endpoint·자격증명·사용자 이름·실제 지문·raw host key·경로·transcript·command output은 기록하지 않습니다.

## Terminal and desktop manual matrix / 터미널·데스크톱 수동 매트릭스

- vim: insert/normal/visual mode, colors, cursor, mouse, paste, and resize.
- tmux: attach/detach, pane split, status line, mouse, alternate screen, and resize.
- htop: color, function keys, scrolling, mouse, and clean exit.
- Korean IME: composition, candidate selection, backspace, mixed-width text, paste, and remote UTF-8 round trip.
- Sixteen tabs: one-hour activity, close/disconnect cleanup, memory trend, and SFTP isolation.
- Multi: zero default selections, explicit target checks, one failed target, retry only failed, and restored transfer selection safety.

## Signed MSIX, update, and rollback / 서명 MSIX·업데이트·롤백

The manual `Signed MSIX release` workflow requires these GitHub secrets:

- `SUTTY_MSIX_CERT_BASE64`: Base64-encoded production code-signing PFX.
- `SUTTY_MSIX_CERT_PASSWORD`: PFX password.

It patches the package publisher to the certificate subject, builds separate x64 and ARM64 MSIX packages, signs and verifies each package, and emits `Sutty-x64.appinstaller` and `Sutty-arm64.appinstaller`. `package_version` controls the installed package. `appinstaller_version` is deployment metadata and must always increase, including rollback releases. Publish each generated MSIX with its matching descriptor at the HTTPS directory supplied to the workflow.

Rollback procedure:

1. Select a previously accepted package build and assign its package version to the rollback package.
2. Dispatch a new release with a higher `appinstaller_version` than every prior descriptor.
3. Publish both generated files atomically, test one canary machine, then expand rollout.
4. Keep the failed package and logs quarantined; never replace evidence silently.

The descriptor opts into updates on launch and background checks, and permits a lower package version for controlled rollback. A real release remains blocked until the production certificate, HTTPS hosting, clean install, upgrade, rollback, uninstall, and canary results are recorded.

Schema references: [Create an App Installer file manually](https://learn.microsoft.com/en-us/windows/msix/app-installer/how-to-create-appinstaller-file) and [ForceUpdateFromAnyVersion](https://learn.microsoft.com/en-us/uwp/schemas/appinstallerschema/element-s4-forceupdatefromanyversion).

## Evidence record / 증거 기록

No accepted live or package evidence bundle is recorded in the current tree. This statement is deliberately not a placeholder `Pass`.

현재 작업 트리에는 인수 완료된 실환경 또는 패키지 증거 bundle이 없습니다. 이 문장은 placeholder `Pass`가 아닙니다.

For every executed gate, retain one canonical directory containing `manifest.yml`, required `summary.json`, and only explicitly listed redacted attachments. `Pass`, `Fail`, and `Blocked` are preserved as run results. A support row becomes **Live Validated** only after a real `Pass` bundle has `redaction_reviewed: true` and passes human review; it becomes **Released** only when the identical commit and package SHA-256 map to an immutable published artifact.

실행한 각 gate에는 `manifest.yml`, 필수 `summary.json`, 명시적으로 나열한 redacted attachment만 있는 표준 디렉터리 하나를 보존합니다. `Pass`·`Fail`·`Blocked`는 실행 결과로 보존합니다. 실제 `Pass` bundle이 `redaction_reviewed: true`이고 사람 검토를 통과해야 지원 행을 **Live Validated**로 바꿀 수 있으며, 동일한 commit과 package SHA-256이 변경 불가능한 공개 산출물에 연결돼야 **Released**로 바꿀 수 있습니다.

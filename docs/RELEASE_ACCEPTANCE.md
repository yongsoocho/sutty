# Sutty release acceptance / 출시 인수 기준

This checklist separates implemented code from release evidence. A checked source-level item is not a GA claim until the corresponding live or packaging gate has a dated result.

이 체크리스트는 구현 코드와 출시 증거를 구분합니다. 소스 수준 항목이 완료되어도 해당 실서버·패키징 검증 결과가 날짜와 함께 남기 전에는 GA 완료로 간주하지 않습니다.

## P0 transfer gates / P0 전송 게이트

| Gate | Current state | Required release evidence |
| --- | --- | --- |
| Multi SFTP 1→N | Implemented | 16 live targets, isolated progress/result, and failed-target-only retry record |
| Multi SFTP N→1 | Implemented | Same-name files from multiple servers remain isolated in deterministic server folders |
| Recursive directories | Implemented | Empty folders, Unicode paths, deep paths, and 100,000-file live run |
| Retry, resume, checkpoint | Implemented | Transport loss during transfer resumes from a non-zero offset |
| Final size and SHA-256 | Implemented | Upload and download both reject mismatches before final promotion |
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

- `smoke`: SSH command, PTY UTF-8, tool availability, recursive SFTP, tree enumeration, and checksum.
- `fault`: disconnects transport during upload, reconnects, and requires a non-zero resume offset.
- `scale`: defaults to a real 100 GB transfer and 100,000-file directory test.
- `soak`: defaults to 16 sessions for 60 minutes with repeated commands.

Run locally after setting the `SUTTY_TEST_SSH_HOST`, `SUTTY_TEST_SSH_USER`, authentication, and `SUTTY_TEST_HOST_KEY_SHA256` environment variables:

```powershell
$env:SUTTY_TEST_MODES = 'smoke,fault'
dotnet run --project .\tests\sutty.LiveServer.SelfTest\sutty.LiveServer.SelfTest.csproj --configuration Release
```

The same harness is available through the manually dispatched Windows CI workflow. Repository secrets carry credentials; repository variables control session count, soak minutes, transfer GB, file count, and fault payload size. Do not enable `scale` or `soak` on an unapproved server.

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

It patches the package publisher to the certificate subject, builds one x64 MSIX, signs and verifies it, and emits `Sutty.appinstaller`. `package_version` controls the installed package. `appinstaller_version` is deployment metadata and must always increase, including rollback releases. Publish the generated MSIX and descriptor together at the HTTPS directory supplied to the workflow.

Rollback procedure:

1. Select a previously accepted package build and assign its package version to the rollback package.
2. Dispatch a new release with a higher `appinstaller_version` than every prior descriptor.
3. Publish both generated files atomically, test one canary machine, then expand rollout.
4. Keep the failed package and logs quarantined; never replace evidence silently.

The descriptor opts into updates on launch and background checks, and permits a lower package version for controlled rollback. A real release remains blocked until the production certificate, HTTPS hosting, clean install, upgrade, rollback, uninstall, and canary results are recorded.

Schema references: [Create an App Installer file manually](https://learn.microsoft.com/en-us/windows/msix/app-installer/how-to-create-appinstaller-file) and [ForceUpdateFromAnyVersion](https://learn.microsoft.com/en-us/uwp/schemas/appinstallerschema/element-s4-forceupdatefromanyversion).

## Evidence record / 증거 기록

| Date | Commit | Build/package | Server/Windows image | Gate | Result | Evidence location |
| --- | --- | --- | --- | --- | --- | --- |
| _pending_ | _pending_ | _pending_ | _pending_ | Live and packaging matrix | Not run | _attach logs without secrets_ |

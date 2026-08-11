# Security / 보안

[English](#english) · [한국어](#한국어)

> Sutty is Alpha software, not a GA security product. It has meaningful host-key and transfer-safety controls, but it has not completed an independent security review, signed release pipeline, encrypted credential Vault, or the specification's full security test matrix.

> Sutty는 Alpha 소프트웨어이며 GA 보안 제품이 아닙니다. 호스트키와 전송 안전 제어가 있지만 독립 보안 검토, 서명 릴리스 파이프라인, 암호화 자격 증명 Vault, 명세의 전체 보안 테스트 매트릭스를 완료하지 않았습니다.

## English

### Supported security status

Only the latest source on the repository's active development branch receives best-effort Alpha fixes. There is no currently supported GA release or long-term security support window. Do not infer security support from the package version in the application manifest.

### Reporting a vulnerability

Do not include credentials, private keys, passphrases, production hostnames, customer data, terminal output, or unredacted logs in a public issue.

1. Prefer [GitHub private vulnerability reporting](https://github.com/yongsoocho/sutty/security/advisories/new) when it is available for this repository.
2. If the private form is unavailable, open a minimal public issue asking the repository owner for a private contact channel. Do not describe the vulnerability until a private channel is established.
3. Include the affected commit/version, Windows build and architecture, reproduction steps, impact, and whether the issue requires a malicious SSH/SFTP server, local file, or same-user process.
4. Use synthetic hosts and credentials. Remove secrets and identifying paths from screenshots and diagnostics.
5. Allow the maintainer time to reproduce and coordinate a fix before public disclosure.

There is no bug bounty or guaranteed response time at the Alpha stage.

### Threat boundary

Sutty is a local-first Windows desktop client. It has no account, cloud synchronization, team service, or telemetry backend. Its process directly handles untrusted network data from SSH/SFTP servers and user-selected local files.

Current controls are intended to reduce:

- SSH man-in-the-middle risk through endpoint-scoped host-key verification.
- Accidental replacement of an existing file through explicit collision decisions and temporary-file promotion.
- Cross-session SFTP mistakes through session/version checks and serialized access to each SSH.NET SFTP client.
- Accidental Multi execution through zero default targets and additional confirmation for PROD-tagged sessions.

Current controls do **not** protect against:

- Malware, debuggers, administrators, or another process already running with the same Windows user authority.
- Theft or modification of an unlocked private-key file referenced by path.
- Secrets present briefly in process memory, Windows controls, SSH.NET objects, page files, or a process dump.
- A user approving the wrong unknown host-key fingerprint.
- A hostile server causing resource pressure, parser bugs, misleading filenames/output, or application-level SSH/SFTP behavior.
- Compromise of the development machine, unsigned build output, NuGet supply chain, or distribution channel.
- Data disclosure when a user shares `%LOCALAPPDATA%\sutty` without review.

Sutty makes no FIPS, certification, hardened-enterprise, or complete terminal-isolation claim.

### Credential rules

- The enabled Alpha authentication methods are Password and supported OpenSSH/PEM private-key files. Password mode includes a non-interactive fallback that answers password-like keyboard-interactive prompts.
- Passwords and private-key passphrases are not written to `settings.json`, `sutty.db`, or `known-hosts.json`.
- Sutty has no encrypted credential Vault today. A password or passphrase must be entered again and exists as managed strings during authentication; complete memory zeroing cannot be guaranteed.
- `settings.json` may contain recent private-key **paths** and tags.
- `sutty.db` may contain host, alias, username, port, authentication method, private-key path, tags, command templates, usage data, history, and pins. These are plaintext operational metadata.
- Sutty reads the selected private-key file in place. It does not copy the key into its database, but protection of that file and its ACL remains the user's responsibility.
- SSH agent and OTP/multi-prompt keyboard-interactive UI are unavailable; the password fallback is not a general interactive or MFA flow. Disabled controls must not be treated as security fallbacks.
- Do not place passwords, tokens, or private keys in command templates, tags, host display names, or diagnostic text.

### Known-host rules

- Trust identity is canonicalized as `[host]:port`, including the default port and normalized DNS/IP forms.
- Both the SSH client and the optional SFTP client verify host keys.
- An unknown key is rejected during the first handshake. The UI then shows endpoint, algorithm, and SHA-256 fingerprint; a positive choice retries with a new client.
- **Connect once** is limited to one logical connection attempt and is not persisted.
- **Trust and save** atomically writes the public key to `%LOCALAPPDATA%\sutty\known-hosts.json`.
- A changed saved key is blocked. It cannot be silently replaced or bypassed with **Connect once**.
- Corrupt or unreadable known-host storage fails closed.
- Known-host management, rotation approval, OpenSSH import/export, enterprise policy, and security audit events are not implemented yet.

Verify an unknown fingerprint through an independent channel controlled by the server owner. Do not trust a fingerprint copied from the same possibly compromised connection path.

### Terminal and remote-file data

The current terminal uses a bounded native parser, not WebView2. Remote output is still untrusted. The parser can contain correctness or denial-of-service defects, and it is not a complete terminal isolation boundary. See [ADR 0001](docs/adr/0001-terminal-renderer.md).

Remote names, paths, metadata, symlinks, and file contents are server-controlled. Current Files operations do not support recursive directory delete, but file deletion and overwrite are destructive after confirmation. Review the target host and path before approval.

### Logs and diagnostics

- `crash.log` stores unhandled exception text and stack details locally. It is **not guaranteed to be redacted** and may contain hosts, usernames, paths, or command-related context.
- There is no production audit log, transcript system, support bundle, crash upload, or telemetry upload today.
- Before sharing a log, search for hosts, IP addresses, usernames, local/remote paths, commands, tokens, key headers, and customer data. Prefer a minimal reproduction over a full data-directory archive.

### Safe use during Alpha

- Build from reviewed source and verify the commit you are testing.
- Use non-production systems and least-privilege accounts where possible.
- Back up remote and local files before testing overwrite, rename, or delete.
- Treat host-key changes as a possible incident; verify out of band instead of deleting the trust file reflexively.
- Keep Windows, .NET, Windows App SDK, and dependencies patched.
- Do not distribute an unsigned Alpha build as an approved enterprise release.

---

## 한국어

### 지원 보안 상태

저장소의 활성 개발 branch에 있는 최신 소스만 best-effort Alpha 수정 대상입니다. 현재 지원되는 GA 릴리스나 장기 보안 지원 기간은 없습니다. 앱 manifest의 package version만 보고 보안 지원을 추정하면 안 됩니다.

### 취약점 신고

공개 issue에 자격 증명, 개인키, passphrase, production hostname, 고객 데이터, 터미널 출력, redaction되지 않은 로그를 포함하지 마세요.

1. 이 저장소에서 사용할 수 있다면 [GitHub 비공개 취약점 신고](https://github.com/yongsoocho/sutty/security/advisories/new)를 우선 사용하세요.
2. 비공개 양식을 사용할 수 없다면 저장소 소유자에게 비공개 연락 경로를 요청하는 최소한의 공개 issue만 작성하세요. 비공개 경로가 생기기 전에는 취약점 내용을 설명하지 마세요.
3. 영향받는 commit/version, Windows build와 architecture, 재현 절차, 영향, 악성 SSH/SFTP 서버·로컬 파일·동일 사용자 프로세스가 필요한지 포함하세요.
4. 합성 Host와 자격 증명을 사용하고 screenshot·진단 정보에서 비밀과 식별 가능한 경로를 제거하세요.
5. 공개 전에 유지관리자가 재현하고 수정 공개를 조정할 시간을 주세요.

Alpha 단계에는 bug bounty와 보장된 응답 시간이 없습니다.

### 위협 경계

Sutty는 로컬 우선 Windows 데스크톱 클라이언트입니다. 계정, 클라우드 동기화, 팀 서비스, telemetry backend가 없습니다. 프로세스는 SSH/SFTP 서버의 신뢰하지 않는 네트워크 데이터와 사용자가 선택한 로컬 파일을 직접 처리합니다.

현재 제어는 다음 위험을 줄이기 위한 것입니다.

- endpoint 범위 호스트키 검증을 통한 SSH 중간자 공격 위험
- 명시적 충돌 결정과 임시 파일 승격을 통한 기존 파일의 우발적 교체
- 세션/version 검사와 SSH.NET SFTP client별 직렬 접근을 통한 세션 간 SFTP 오작업
- 기본 대상 0개와 PROD 태그 세션 추가 확인을 통한 우발적 Multi 실행

현재 제어는 다음을 **방어하지 못합니다**.

- 같은 Windows 사용자 권한으로 이미 실행 중인 malware·debugger·관리자·다른 프로세스
- 경로로 참조하는 잠금 해제 개인키 파일의 탈취·변조
- 인증 중 process memory·Windows control·SSH.NET object·page file·process dump에 잠시 존재하는 비밀정보
- 사용자가 잘못된 알 수 없는 호스트키 지문을 승인하는 경우
- 악성 서버가 유발하는 리소스 압박, parser bug, 오해를 부르는 파일명·출력, 앱 수준 SSH/SFTP 동작
- 개발 PC, 서명되지 않은 build output, NuGet 공급망, 배포 경로의 침해
- 사용자가 `%LOCALAPPDATA%\sutty`를 검토 없이 공유할 때의 데이터 노출

Sutty는 FIPS, 보안 인증, hardened enterprise, 완전한 터미널 격리를 주장하지 않습니다.

### 자격 증명 규칙

- Alpha에서 활성화된 인증 방식은 Password와 지원되는 OpenSSH/PEM 개인키 파일입니다. 비밀번호 방식에는 password 형태의 keyboard-interactive prompt에 답하는 비대화형 fallback이 있습니다.
- 비밀번호와 개인키 passphrase는 `settings.json`, `sutty.db`, `known-hosts.json`에 쓰지 않습니다.
- 현재 암호화 자격 증명 Vault가 없습니다. 비밀번호·passphrase는 다시 입력해야 하고 인증 중 managed string으로 존재하므로 완전한 memory zeroing을 보장할 수 없습니다.
- `settings.json`에는 최근 개인키 **경로**와 태그가 있을 수 있습니다.
- `sutty.db`에는 host, alias, username, port, 인증 방식, 개인키 경로, 태그, 명령 템플릿, 사용 정보, 히스토리, pin이 있을 수 있습니다. 모두 평문 운영 메타데이터입니다.
- Sutty는 선택한 개인키 파일을 그 위치에서 읽습니다. 키를 DB에 복사하지 않지만 파일과 ACL 보호는 사용자의 책임입니다.
- SSH agent와 OTP·다중 prompt keyboard-interactive UI는 지원하지 않습니다. 비밀번호 fallback은 일반 interactive 또는 MFA 흐름이 아닙니다. 비활성 컨트롤을 보안 fallback으로 취급하면 안 됩니다.
- 명령 템플릿, 태그, Host 표시 이름, 진단 텍스트에 비밀번호·token·개인키를 넣지 마세요.

### Known-host 규칙

- 신뢰 identity는 기본 port를 포함하고 DNS/IP를 정규화한 `[host]:port` 형식입니다.
- SSH client와 선택 SFTP client가 모두 호스트키를 검증합니다.
- 알 수 없는 키는 첫 handshake에서 거부합니다. 이후 UI가 endpoint·algorithm·SHA-256 지문을 표시하고 승인 시 새 client로 재시도합니다.
- **이번만 연결**은 한 논리 연결 시도에만 적용하며 저장하지 않습니다.
- **신뢰하고 저장**은 공개키를 `%LOCALAPPDATA%\sutty\known-hosts.json`에 원자적으로 저장합니다.
- 변경된 저장 키는 차단합니다. 조용히 교체하거나 **이번만 연결**로 우회할 수 없습니다.
- 손상되거나 읽을 수 없는 known-host 저장소는 기본 차단합니다.
- Known-host 관리, rotation 승인, OpenSSH 가져오기·내보내기, 기업 정책, 보안 audit event는 아직 구현하지 않았습니다.

알 수 없는 지문은 서버 소유자가 통제하는 독립 경로로 확인하세요. 동일하게 침해되었을 수 있는 연결 경로에서 복사한 지문을 신뢰하면 안 됩니다.

### 터미널과 원격 파일 데이터

현재 터미널은 WebView2가 아니라 제한된 네이티브 parser를 사용합니다. 그래도 원격 출력은 신뢰하지 않는 데이터입니다. Parser에는 정확성·서비스 거부 결함이 있을 수 있으며 완전한 터미널 격리 경계가 아닙니다. [ADR 0001](docs/adr/0001-terminal-renderer.md)을 확인하세요.

원격 이름·경로·메타데이터·symlink·파일 내용은 서버가 제어합니다. 현재 Files 작업은 재귀 디렉터리 삭제를 지원하지 않지만 확인 뒤의 파일 삭제·덮어쓰기는 파괴적입니다. 승인 전에 대상 Host와 경로를 확인하세요.

### 로그와 진단

- `crash.log`는 미처리 예외 텍스트와 stack 상세를 로컬에 저장합니다. **Redaction을 보장하지 않으며** host·username·경로·명령 관련 내용이 포함될 수 있습니다.
- 현재 production audit log, transcript 시스템, support bundle, crash upload, telemetry upload가 없습니다.
- 로그 공유 전에 host, IP 주소, username, local/remote 경로, 명령, token, key header, 고객 데이터를 검색하세요. 전체 데이터 디렉터리보다 최소 재현 정보를 우선하세요.

### Alpha 안전 사용

- 검토한 소스에서 빌드하고 테스트하는 commit을 확인하세요.
- 가능하면 non-production 시스템과 최소 권한 계정을 사용하세요.
- overwrite·rename·delete 테스트 전에 원격·로컬 파일을 백업하세요.
- 호스트키 변경은 사고 가능성으로 취급하고 trust 파일을 반사적으로 지우는 대신 독립 경로로 확인하세요.
- Windows, .NET, Windows App SDK, 의존성을 최신 보안 patch로 유지하세요.
- 서명되지 않은 Alpha 빌드를 승인된 기업 릴리스로 배포하지 마세요.

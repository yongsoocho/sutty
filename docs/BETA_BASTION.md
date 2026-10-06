# Bastion Beta / Bastion 베타

This feature adds saved-bastion reuse to the existing single-hop SSH Jump connection in the Windows app. It is a feature-level Beta in the current development build, not a new application release or a change to the published version. See [requirements](REQUIREMENTS.md#ssh-and-authentication--ssh와-인증).

Windows 앱의 기존 단일 경유 SSH Jump에 저장된 Bastion 재사용을 추가하는 기능입니다. 현재 개발 빌드의 기능 단위 Beta이며 앱 전체의 신규 릴리스나 공개 버전 변경을 뜻하지 않습니다. 상태는 [요구사항 추적표](REQUIREMENTS.md#ssh-and-authentication--ssh와-인증)에 있습니다.

## 한국어

연결 경로는 `내 Windows PC의 Sutty -> 중앙 SSH 서버 -> 목적지 SSH 서버`입니다. 중앙 서버에서 목적지의 SSH 포트에 접근할 수 있어야 하고 해당 계정의 SSH 포워딩이 허용되어야 합니다. 목적지가 자리의 Windows 데스크톱이면 그 PC에서 SSH 서버가 실행 중이어야 합니다.

1. Home의 연결 폼 상단에는 **목적지** 주소, 사용자, 인증을 입력합니다.
2. **고급 연결 옵션 -> 연결 경로 -> SSH Jump**를 선택합니다.
3. **저장된 Bastion · Beta**에서 중앙 서버를 선택하거나 **직접 입력**합니다. 목록에는 직접 연결하는 저장 SSH Host 중 Password, 개인키, Windows SSH Agent 인증 프로필이 표시됩니다.
4. 중앙 서버의 주소, 포트, 사용자, 인증을 확인합니다. 프로필 선택은 목적지 입력을 바꾸지 않으며 **엄격 경로**를 켭니다. 연결 전 경로 표시에서 중앙 서버와 목적지를 확인한 뒤 연결합니다.
5. 연결된 세션의 경로 표시를 확인합니다. Terminal과 Files는 같은 중앙 서버를 거쳐 **목적지**에 연결하며 중앙 서버와 목적지의 호스트 키를 각각 검증합니다. 경유 연결 실패 시 직접 연결로 자동 전환하지 않습니다.

**경유 서버를 새 프로필로 저장** 아이콘은 직접 입력한 중앙 서버를 독립된 새 Host로 저장합니다. 주소, 사용자, 인증 방식, 해당하는 로컬 키 경로를 저장하고 비밀번호, 키 암호, 자격증명 참조는 저장하지 않습니다. 기존 프로필을 덮어쓰지 않습니다.

프로필 선택 시 해당 시점의 설정을 현재 연결에 복사합니다. 현재 연결의 수정은 원본 프로필에 반영되지 않고, 이후 원본 프로필을 수정해도 이미 복사한 경로나 저장된 목적지의 경로가 자동으로 바뀌지 않습니다. 새 설정을 쓰려면 다시 선택하고 목적지 프로필도 명시적으로 저장합니다.

사용자가 프로필을 선택했을 때만 그 프로필에 이미 연결된 로컬 암호화 Vault의 기본 로그인 비밀번호 또는 개인키 암호를 현재 경유 인증 입력에 복사합니다. 별도의 Vault 저장 동의 없이 새 비밀값을 저장하지 않습니다. Vault 값을 읽지 못하면 직접 입력이 필요합니다. 경유 서버의 주소, 사용자, 인증을 바꾸면 앞서 불러온 비밀값을 지웁니다.

범위는 중앙 서버 **한 대**를 경유하는 연결입니다. 다단계 Jump, 자리 PC의 상시 역방향 접속 대기 서비스, 자동 재연결, 절전 후 터널 자동 복구는 이번 기능에 포함하지 않습니다. 중앙 서버에서 자리 PC로 접근할 수 없는 구성은 이 기능만으로 연결할 수 없습니다.

실제 서버의 Password/개인키/Agent 호환성, 포워딩 거절, 중앙 서버 중단, 전송 중 취소와 재연결 검증은 남아 있습니다. 특히 실행 중 중앙 서버나 포워딩 오류가 목적지의 SSH handshake 또는 SFTP 오류로 표시될 수 있어, 원인 단계가 항상 정확히 구분되는 상태는 아닙니다.

## English

The path is `Sutty on your Windows PC -> central SSH server -> destination SSH server`. The central server must reach the destination SSH port and permit SSH forwarding for the account. A Windows desktop used as the destination needs a running SSH server.

1. Enter the **destination** address, username, and authentication in Home's main connection form.
2. Select **Advanced connection options -> Connection route -> SSH Jump**.
3. Choose a central server under **Saved Bastion · Beta**, or select **Enter manually**. The picker accepts saved Direct SSH hosts using Password, private-key, or Windows SSH Agent authentication.
4. Review the bastion address, port, username, and authentication. Selection leaves the destination unchanged and enables **Strict route**. Check the displayed path before connecting.
5. Check the connected session's path. Terminal and Files reach the **destination** through the same central server; both bastion and destination host keys are verified independently. Route failure never automatically falls back to Direct.

The **Save bastion as a new profile** icon saves the entered central server as a separate new Host. It stores address, username, authentication method, and the applicable local key path, without passwords, key passphrases, or credential references. It does not overwrite an existing profile.

Selecting a profile copies its current settings into the connection. Editing that connection does not update the source profile. Later source-profile edits do not propagate to an existing route snapshot or a saved destination's route. Select the profile again and explicitly save the destination to adopt changes.

Only an explicit profile selection reads its already-associated local encrypted Vault credential and copies the primary login password or private-key passphrase into the bastion authentication fields. Selection does not create a new saved secret. An unreadable Vault credential requires manual input. Changing bastion identity or authentication clears previously loaded secrets.

This feature covers **one** bastion hop. Multi-hop chains, an always-on reverse connection service on the destination PC, automatic reconnect, and tunnel recovery after sleep are outside this increment. It cannot by itself reach a desktop that the bastion cannot access.

Live Password/key/Agent compatibility, forwarding refusal, bastion interruption, transfer cancellation, and reconnect acceptance remain pending. A runtime bastion or forwarding failure can currently appear as a destination SSH handshake or SFTP failure; causal route-stage attribution is incomplete.

## Implementation / 구현

- [Saved-host selection and credential isolation](../src/sutty.Command/BastionConnectionService.cs)
- [Home connection controls](../src/sutty.UI/Views/HomePanel.Bastion.cs)
- [Shared SSH/SFTP route and cleanup](../src/sutty.Core/Sessions/SshNetSession.TransportFeatures.cs)
- [Route credential lifetime checks](../tests/sutty.Core.Security.SelfTest/RouteCredentialLifetimeTests.cs)

These links describe source behavior and focused checks, not live-server acceptance evidence. The existing disposable Direct SSH lab disables TCP forwarding and cannot serve as a bastion without a dedicated test configuration.

위 링크는 소스 동작과 집중 검사를 설명하며 실서버 인수 증거를 뜻하지 않습니다. 기존 임시 Direct SSH 테스트 환경은 TCP 포워딩을 차단하므로 Bastion 검증에는 별도 테스트 구성이 필요합니다.

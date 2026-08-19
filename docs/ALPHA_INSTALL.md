# Sutty Alpha installation / Sutty Alpha 설치

## English

Sutty Alpha ZIPs are unsigned evaluation releases for Windows 11 24H2 or later. Use them first with development, test, or staging systems and keep an existing recovery path for important servers.

1. Open the [official GitHub Releases page](https://github.com/yongsoocho/sutty/releases) and select the Alpha release you intend to install.
2. Download the `Sutty-*-win-x64.zip` or `Sutty-*-win-arm64.zip` asset for the computer architecture. Keep only that ZIP in the download folder while following the commands below.
3. Download `SHA256SUMS.txt` from the same Release.
4. Calculate the digest of the actual downloaded archive and compare both its filename and SHA-256 value with the corresponding line in `SHA256SUMS.txt`:

```powershell
$archives = @(Get-ChildItem -File .\Sutty-*-win-*.zip)
if ($archives.Count -ne 1) { throw 'Keep exactly one Sutty Alpha ZIP in this folder.' }
Get-FileHash -LiteralPath $archives[0].FullName -Algorithm SHA256
```

5. Extract the ZIP into a new folder and run `sutty.UI.exe`.

The Alpha ZIP is self-contained and does not require a separate .NET installation. It is not code-signed. Windows may show an unknown-publisher warning; continue only when the file came from the official Release and its SHA-256 digest matches.

Application data is stored separately under `%LOCALAPPDATA%\sutty`. Removing the extracted application folder does not remove Saved Hosts, history, settings, host keys, transfer checkpoints, or an encrypted local vault.

To confirm the running build, open **Settings → About** or launch:

```powershell
.\sutty.UI.exe --version
```

## 한국어

Sutty Alpha ZIP은 Windows 11 24H2 이상을 위한 서명되지 않은 시험 배포판입니다. 개발·테스트·스테이징 환경에서 먼저 사용하고, 중요한 서버에는 기존 복구 수단을 함께 유지하세요.

1. [공식 GitHub Releases 페이지](https://github.com/yongsoocho/sutty/releases)를 열고 설치할 Alpha Release를 선택합니다.
2. 컴퓨터 아키텍처에 맞는 `Sutty-*-win-x64.zip` 또는 `Sutty-*-win-arm64.zip` asset을 받습니다. 아래 명령을 실행할 때는 다운로드 폴더에 확인할 ZIP 하나만 두세요.
3. 같은 Release에서 `SHA256SUMS.txt`를 받습니다.
4. 아래 명령으로 실제 다운로드한 ZIP의 해시를 계산한 뒤 파일명과 SHA-256 값을 모두 `SHA256SUMS.txt`의 해당 줄과 비교합니다.

```powershell
$archives = @(Get-ChildItem -File .\Sutty-*-win-*.zip)
if ($archives.Count -ne 1) { throw 'Keep exactly one Sutty Alpha ZIP in this folder.' }
Get-FileHash -LiteralPath $archives[0].FullName -Algorithm SHA256
```

5. ZIP을 새 폴더에 풀고 `sutty.UI.exe`를 실행합니다.

Alpha ZIP은 self-contained이므로 별도 .NET 설치가 필요하지 않습니다. 아직 코드 서명되지 않았기 때문에 Windows에서 알 수 없는 게시자 경고가 표시될 수 있습니다. 반드시 공식 Release에서 받은 파일이고 SHA-256이 일치할 때만 계속하세요.

앱 데이터는 `%LOCALAPPDATA%\sutty`에 별도로 저장됩니다. 압축을 푼 앱 폴더를 삭제해도 저장 Host, 접속 기록, 설정, Host key, 전송 checkpoint, 암호화 로컬 Vault는 삭제되지 않습니다.

실행 중인 버전은 **설정 → 정보** 또는 다음 명령으로 확인할 수 있습니다.

```powershell
.\sutty.UI.exe --version
```

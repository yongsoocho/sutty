# Sutty 일상 작업 안내

개인과 10인 이하 팀이 Windows에서 SSH 접속, 터미널, SFTP 파일 작업과 터널을 하나의 앱으로 처리하는 개발 빌드입니다. 기존 .NET 10 / WinUI 3 / SSH.NET / xterm.js 기반을 확장했습니다. 계정 가입이나 Sutty 서버는 필요하지 않습니다. 이 문서는 2026-09-06 개발 작업의 사용법이며, 정식 출시 인증은 아닙니다.

## 접속과 파일 탐색

1. Home에서 빠른 연결을 하거나 Hosts에서 저장 호스트를 엽니다. 처음 보는 SSH 호스트 키는 지문을 확인합니다.
2. 서버 탭 안의 Files를 엽니다. 왼쪽은 이 PC, 오른쪽은 선택한 서버입니다.
3. 각 패널의 경로 입력, 뒤로/앞으로, 상위 폴더, 새로고침으로 이동합니다. 이름·크기·수정일 정렬, 역순, 숨김 항목 표시를 선택할 수 있습니다. 폴더가 먼저 표시됩니다.
4. 원격 즐겨찾기 메뉴에서 현재 경로를 저장하거나 삭제합니다. 즐겨찾기는 저장 호스트별로 구분되며 로컬에만 보관됩니다.
5. 파일·폴더를 선택하고 반대쪽 패널 또는 폴더로 드래그합니다. 업로드·다운로드 버튼과 Windows 탐색기에서 원격 패널로 드롭하는 방법도 사용할 수 있습니다. 드롭은 복사이며 원본을 삭제하지 않습니다.

전송은 기존 충돌 확인·대기열·부분 파일·검증·최종 반영 절차를 거칩니다. 다른 탭으로 이동해도 전송 대상 서버와 경로는 고정됩니다. 보통 전송의 일시정지·재개·재시도는 Transfers에서 처리합니다. 연결되지 않은 서버에는 실행할 수 없는 동작을 활성화하지 않습니다.

## 원격 파일 편집

1. Settings → Connection의 원격 파일 편집기에서 원하는 `.exe`를 선택합니다. 비워 두면 Windows 메모장을 사용합니다. VS Code 등의 인수에는 `{file}`을 포함합니다. 예: `--reuse-window {file}`. 앱은 이 자리에 인용 처리한 로컬 파일 경로를 넣습니다.
2. Files에서 원격 일반 파일의 메뉴 → **외부 편집기로 열기**를 선택합니다. 8 MiB 이하 텍스트 파일을 지원하며, 심볼릭 링크·폴더·바이너리는 일반 다운로드를 사용합니다.
3. 외부 편집기에서 저장합니다. Files의 **편집본** 영역에 로컬 변경 상태가 표시됩니다.
4. **서버에 반영**에서 호스트·환경·원격 경로를 확인합니다. 원격 크기나 수정시각이 달라졌거나 비교할 수 없다면 덮어쓰기, 다른 절대 경로로 저장, 다시 내려받기 중 선택합니다. 다른 이름은 기존 파일을 덮어쓰지 않습니다.
5. **이번 파일만 저장 시 자동 반영**을 켜면 안정된 저장 내용을 감지해 업로드합니다. 원격 충돌·오류·연결 종료 시 자동 반영은 멈춥니다. 서버 명령, 서비스 재시작이나 배포 작업은 실행하지 않습니다.
6. 작업이 끝나면 외부 편집기를 저장·닫은 뒤 **편집 종료**를 선택합니다.

업로드마다 별도 고정 복사본을 만들어 편집기의 다음 저장이 진행 중인 전송을 바꾸지 않게 합니다. SHA-256 검증과 기존 전송 큐를 사용합니다. 크기·수정시각 비교는 동시 편집 잠금이 아니며, 다른 프로그램이 같은 시각·크기로 바꾸거나 확인 직후 수정하는 경쟁까지 방지하지 못합니다.

외부 편집기의 아직 저장하지 않은 내용은 감지할 수 없습니다. 앱·세션 종료는 열린 편집본과 진행 중인 전송을 먼저 알립니다. 실패나 종료 뒤에도 `%LOCALAPPDATA%\sutty\edits`에 원본 편집본과 업로드 사본을 보관합니다. Settings 또는 Files → 편집본의 **보관 폴더**에서 찾을 수 있으며, `RECOVER.txt`에 서버·원격 경로가 기록됩니다. 민감한 파일도 남을 수 있으므로 작업이 끝나면 필요한 사본을 옮기고 보관 폴더를 직접 정리하세요.

앱 재시작 후 편집 세션은 자동 복원하지 않습니다. 서버의 현재 파일을 다시 확인하고 보관한 로컬 변경을 적용하세요. 편집 전송 기록은 일반 큐의 재시도로 실행할 수 없습니다. 이전 원격 상태에 대한 승인을 재사용하지 않기 위한 동작입니다.

## 터미널과 터널

Files의 **터미널에서 열기**는 안전하게 인용한 `cd` 명령을 준비하고 복사합니다. 터미널에 입력하거나 실행하지 않습니다. 사용자는 정상 셸 프롬프트인지 확인한 뒤 직접 붙여넣고 실행합니다. 터미널 화면의 **파일 경로 열기**에 절대 경로를 입력하면 같은 서버의 Files로 이동합니다. 서버 출력을 자동으로 읽거나 주기적으로 `pwd`를 실행하지 않습니다.

서버 탭의 Tunnels에서 Local·Remote·Dynamic 규칙의 시작·중지·실패 상태를 확인합니다. **추가**는 이번 세션에 중지 상태로 규칙을 만들고 **시작**으로 실행합니다. 기본 수신 주소는 `127.0.0.1`이며 외부 주소는 노출 경고를 확인해야 합니다. 포트 충돌이나 목적지 연결 오류는 해당 터널에 표시되고 정상 SSH 연결은 유지됩니다. 세션 종료 시 수신 포트를 정리합니다. 실행 중 추가한 규칙은 이번 세션에만 적용되며, 계속 사용할 규칙은 저장 호스트의 연결 설정에서 구성합니다.

## 호스트 정리와 공유

저장 호스트 메뉴의 **복제**로 비슷한 서버를 추가할 수 있습니다. 복제는 새 프로필을 만들고 저장된 비밀번호 참조를 복사하지 않습니다. **인증 별칭**은 각 PC에서 연결할 계정·키를 설명하는 이름입니다. 별칭만으로 비밀번호를 공유하거나 자동 연결하지 않습니다.

기존 설정 가져오기는 Settings의 OpenSSH, Windows 저장 세션, INI, SFTP Site Manager XML에서 시작합니다. 적용 전에 추가·변경·중복·오류를 보고 항목별 추가·건너뛰기·복제·갱신을 선택합니다. **새 항목 선택**으로 새 프로필만 한 번에 선택할 수 있습니다. 호환되는 기존 호스트·인증·연결 경로를 갱신할 때만 이 PC의 인증 연결을 유지합니다.

Hosts → **공유 / 가져오기**에서 공유할 호스트와 명령을 직접 선택하고 실제 JSON을 검토한 뒤 저장합니다. 받는 사람은 같은 메뉴에서 JSON을 가져와 선택 적용하고 자신의 키·계정을 연결합니다. schemaVersion 1, 최대 4 MiB, 1,000개 정의를 지원하며 더 새 버전은 거절합니다.

공유 파일에는 비밀번호·OTP·개인키 내용·로컬 키 경로·금고 ID·호스트 키 신뢰·실행 기록을 넣지 않습니다. 호스트명·사용자명·서버 경로와 사용자가 명령에 직접 넣은 토큰은 민감할 수 있으므로 공유 전에 내용을 확인하세요. 외부 ProxyCommand 본문은 내보내지 않으며 해당 프로필의 가져오기는 차단합니다. 각 PC에서 직접 구성해야 합니다. 잘못된 간접 연결을 직접 연결로 바꾸어 적용하지 않습니다.

## 확인 범위

소스의 기능 구현과 자동 검사 결과는 [구현 상태](IMPLEMENTATION_STATUS.md)를 참고하세요. 이 개발 작업에서는 실서버 접속·GUI 드래그·편집기 저장·깨끗한 PC 설치를 완료했다고 주장하지 않습니다. Docker Desktop 엔진이 실행되지 않아 로컬 OpenSSH 시험 서버를 시작할 수 없었고, Windows 화면 자동 확인은 사용자의 Esc 입력으로 중단되었습니다. 서명된 설치본과 장시간 터미널·서버 호환성 확인은 남아 있습니다.

10인 기준은 대상 사용자 규모이며 호스트 수나 서버 수를 10개로 제한하는 값이 아닙니다. 현재의 16개 탭, 세션별 열린 편집본 8개, 세션별 터널 32개 한도는 각각 앱의 자원 관리 기준입니다.

## English

This development build adds folder navigation history, sorting, hidden-file controls, per-host remote bookmarks and copy-only pane-to-pane drag and drop. Remote text editing uses a local executable, explicit upload by default, an optional per-file save-upload switch, immutable upload copies, metadata conflict checks and the existing verified transfer queue. Files remain recoverable under `%LOCALAPPDATA%\sutty\edits`; close the external editor and remove sensitive copies yourself when finished. Generic queue retry cannot bypass edit review after failure/restart. Edited files are limited to 8 MiB, with eight open copies per session.

Files → Terminal prepares/copies a quoted command without injecting or executing it. Open file path navigates the same session's Files explicitly. Tunnels provides session-local add/start/stop and real listener/error states with loopback defaults and explicit external-bind consent. Host menus provide duplication and authentication aliases; Settings imports and Hosts JSON sharing provide schema validation and per-item preview/selection. Export omits credentials, local key paths, trust records and history. External ProxyCommand profiles must be configured manually on the receiving PC.

Use the existing Windows Alpha build/publish commands. This is still an Alpha implementation: real-server/editor/drag-and-drop acceptance, the terminal compatibility matrix and production-signed installation remain unverified for these changes. Windows UI automation was stopped by the user's Escape input, and the local Docker engine was unavailable. No new account service, cloud backend or central administration has been introduced.

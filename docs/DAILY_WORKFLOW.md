# Sutty 일상 작업 안내

개인과 10인 이하 팀이 Windows에서 SSH 접속, 터미널, SFTP 파일 작업과 터널을 하나의 앱으로 처리하는 개발 빌드입니다. 기존 .NET 10 / WinUI 3 / SSH.NET / xterm.js 기반을 확장했습니다. 계정 가입이나 Sutty 서버는 필요하지 않습니다. 이 문서는 현재 개발 작업의 사용법이며, 정식 출시 인증은 아닙니다.

## 중앙 셸과 보조 패널

앱을 새로 시작하면 중앙에 PowerShell이 열립니다. 상단 `+` 또는 `Ctrl+T`에서 **로컬 PowerShell**이나 **로컬 CMD**를 선택해 셸 탭을 추가합니다. 마지막 탭을 닫으면 새 PowerShell 탭을 열어 중앙을 셸 화면으로 유지합니다. 작업 공간 복원을 켜면 로컬 탭별 PowerShell/CMD 선택을 복원하며, 셸 정보가 없는 예전 항목은 PowerShell로 엽니다.

왼쪽의 Home·Hosts·Transfers·Commands는 터미널을 유지하면서 넓은 창에서는 오른쪽, 창 너비가 논리 픽셀 1100 미만이면 아래에 엽니다. **Settings는 셸을 가리는 전체 페이지**이며 열린 세션은 계속 실행됩니다. SSH 탭의 Files·Commands·Tunnels도 같은 서버의 터미널 옆이나 좁은 화면의 아래에 열립니다. 셸 탭을 바꿔도 선택한 보조 패널은 유지되고 카드·도구 모음·파일 패널은 너비에 맞춰 재배치됩니다. `Alt+6`으로 설정을 나와 터미널로 돌아가고 `Alt+7`로 선택한 셸의 Files를 엽니다. PowerShell과 CMD 모두 실제 Windows ConPTY를 사용합니다. 이 배치와 CMD 입력·크기 변경의 수동 GUI 인수 검증은 아직 완료되지 않았습니다.

`CONPTY 235×79` 같은 표시는 터미널의 문자 열·행 크기입니다. 바로 옆 **마지막 출력 복사** 아이콘을 누르면 마지막 명령의 표시 출력을 오류와 함께 복사합니다. PowerShell/CMD는 실행 중 프롬프트 표시자를 사용하며 프로필 파일을 수정하지 않습니다. 복사한 텍스트는 탭·제어 문자·줄바꿈을 PTY가 처리한 결과이며 원본 stdout/stderr 바이트의 복제는 아닙니다. 사용자 정의 또는 표시자가 없는 SSH 셸의 명령 경계는 최선 방식으로 판별합니다.

Settings → Appearance에서 [36개 앱 테마](THEMES.md)를 고를 수 있습니다. VS Code Dark+/Light+·Dracula·Monokai·Nord·Tokyo Night·Catppuccin 등에서도 그라데이션 강조가 유지됩니다. Terminal의 **앱 테마에 맞춤**은 같은 테마의 배경·전경·ANSI 색상을 사용하며 터미널 팔레트를 별도로 고를 수도 있습니다.

글꼴 크기 등 숫자 설정은 증감 버튼 없이 직접 입력합니다. Settings → About 맨 아래의 **설정 초기화**와 **SQLite 초기화**는 서로 별개이며 기본 선택이 No인 Yes/No 확인 뒤에만 실행합니다. 설정 초기화는 기본값을 즉시 저장·반영합니다. SQLite 초기화는 저장 호스트·접속 기록·명령·로컬 연결 즐겨찾기와 최근 기록 등 DB 사용자 데이터를 지우며 스키마와 마이그레이션 정보를 보존합니다. 열린 세션, 설정, 키 신뢰, 암호화 금고, 전송 복구 파일은 유지됩니다. 열린 세션에서 나중에 새로 발생한 활동은 다시 기록될 수 있습니다.

**Multi Command**는 왼쪽 메뉴나 `Alt+8`에서 독립적으로 엽니다. 이 화면에서는 중앙 셸을 가리고 **3×3 세션 그리드**를 표시하며, 열린 셸은 계속 실행됩니다. 각 칸에 열린 SSH·PowerShell·CMD 세션 하나를 표시하고, 최대 16개 세션을 9개씩 나누어 이전/다음 페이지로 이동합니다. 좁은 화면에서도 세 열을 유지하며 스크롤할 수 있습니다.

오른쪽은 기존 **명령 모음집**을 재사용하므로 검색·추가·삭제·`$1`, `$2` 값 입력을 그대로 사용할 수 있습니다. 위쪽에 직접 입력한 명령과 저장 명령 모두 **체크한 세션에만** 전송합니다. **전체 선택 / 전체 해제**는 현재 페이지뿐 아니라 모든 페이지에 적용하며, 다른 페이지에서 체크한 세션도 방송 대상에 포함됩니다. 새로 연 세션은 항상 미선택이고 기본 대상은 0개입니다. 페이지나 셸 탭·메뉴를 바꾸어도 같은 열린 세션의 선택·마지막 결과·진행 상태를 유지합니다. 각 칸에서 세션 상태와 마지막 결과를 확인하고 최대 16,384자의 출력 미리보기를 스크롤해 읽을 수 있습니다. PROD 태그가 있는 대상은 추가 확인을 거칩니다.

## 접속과 파일 탐색

Transfers나 `Alt+7`의 파일 탐색은 현재 선택한 셸을 따릅니다. SSH에서는 같은 세션의 Local/Remote Files를, PowerShell/CMD에서는 프롬프트가 알린 현재 작업 폴더를 표시합니다. 다른 서버를 대신 선택하지 않으며 셸이나 경로를 확인할 수 없으면 안내 상태를 표시합니다. 로컬 탐색기의 폴더 이동은 셸의 디렉터리를 바꾸지 않고 **현재 셸 폴더**로 다시 따라갈 수 있습니다. 직접 실행한 SSH/Multipass 프로그램은 로컬 폴더를 보고하지 않을 수 있습니다.

1. Home 패널에서 빠른 연결을 하거나 Hosts에서 저장 호스트를 엽니다. 처음 보는 SSH 호스트 키는 지문을 확인합니다.
2. 서버 탭의 Files를 터미널 옆이나 아래에 엽니다. 넓은 Files 화면에서는 왼쪽이 이 PC, 오른쪽이 선택한 서버이며, 좁아지면 두 패널을 위아래로 배치합니다.
3. 각 패널의 경로 입력, 뒤로/앞으로, 상위 폴더, 새로고침으로 이동합니다. 이름·크기·수정일 정렬, 역순, 숨김 항목 표시를 선택할 수 있습니다. 폴더가 먼저 표시됩니다.
4. 원격 즐겨찾기 메뉴에서 현재 경로를 저장하거나 삭제합니다. 즐겨찾기는 저장 호스트별로 구분되며 로컬에만 보관됩니다.
5. 파일·폴더를 선택하고 반대쪽 패널 또는 폴더로 드래그합니다. 업로드·다운로드 버튼과 Windows 탐색기에서 원격 패널로 드롭하는 방법도 사용할 수 있습니다. 드롭은 복사이며 원본을 삭제하지 않습니다.

전송은 기존 충돌 확인·대기열·부분 파일·검증·최종 반영 절차를 거칩니다. 다른 탭으로 이동해도 전송 대상 서버와 경로는 고정됩니다. 보통 전송의 일시정지·재개·재시도는 Transfers에서 처리합니다. 연결되지 않은 서버에는 실행할 수 없는 동작을 활성화하지 않습니다.

## 한 줄 로컬 연결

Home의 **한 줄 연결**에 `ssh worker1` 또는 `multipass connect master`처럼 평소 쓰는 로컬 연결 명령을 입력하고 실행합니다. Sutty는 Windows의 직접 실행 파일을 새 로컬 터미널에 열므로 `ssh worker1`은 현재 PC의 `.ssh/config`, `IdentityFile`, Windows SSH Agent와 `PATH` 설정을 그대로 사용합니다. `multipass connect master`도 설치된 Multipass 실행 파일을 같은 방식으로 엽니다.

명령은 `cmd.exe`나 PowerShell을 거치지 않는 하나의 직접 실행 파일과 인수로만 실행합니다. 셸 연결·리디렉션 문법, 실행 파일 경로, 명령 인터프리터는 허용하지 않습니다. SSH 바로가기에는 대화형 호스트/별칭만 넣을 수 있으며 원격 명령은 넣을 수 없습니다.

검증된 명령은 **★ 저장**으로 즐겨찾기에 저장하고, 즐겨찾기나 최근 기록을 한 번 눌러 다시 엽니다. 오른쪽 클릭 메뉴로 항목을 제거합니다. 두 목록은 이 PC의 Sutty 데이터베이스에만 저장되며 즐겨찾기는 최대 100개, 최근 기록은 최대 250개입니다. 해석된 실행 파일 경로와 터미널 출력은 저장하지 않고, 비밀번호·토큰·passphrase 형태의 인수는 저장을 거부합니다. 임의의 문자열이 비밀값인지 모두 식별할 수 없으므로 비밀값은 명령에 직접 넣지 마세요. 종료한 명령은 탭 재진입이나 앱 재시작으로 자동 실행되지 않습니다. 이 탭은 로컬 프로그램의 터미널이며 SFTP 파일 패널은 별도 SSH 연결을 사용합니다. 실제 SSH Agent·키 파일·Multipass·UI 상호작용 검증은 아직 완료되지 않았습니다.

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

소스의 기능 구현과 자동 검사 결과는 [구현 상태](IMPLEMENTATION_STATUS.md)를 참고하세요. 이 개발 작업에서는 실서버 접속·GUI 드래그·편집기 저장·깨끗한 PC 설치를 완료했다고 주장하지 않습니다. 이전 실서버 점검은 Docker Desktop 엔진이 실행되지 않아 시작할 수 없었습니다. **2026-09-22 Windows 화면 자동 확인은 사용자의 Escape 입력으로 중단**되었으며, 다시 빌드한 화면의 인수 검증과 수동 출력 복사 확인은 미완료입니다. 서명된 설치본과 장시간 터미널·서버 호환성 확인도 남아 있습니다.

10인 기준은 대상 사용자 규모이며 호스트 수나 서버 수를 10개로 제한하는 값이 아닙니다. 현재의 16개 탭, 세션별 열린 편집본 8개, 세션별 터널 32개 한도는 각각 앱의 자원 관리 기준입니다.

## English

### Central shell and supporting panes

A fresh start opens PowerShell in the center. Use `+` or `Ctrl+T` to add **Local PowerShell** or **Local CMD** tabs. Closing the last tab opens a replacement PowerShell tab. Optional workspace restoration preserves each local tab's shell choice; older entries without a shell value restore PowerShell.

Global Home, Hosts, Transfers, and Commands keep the terminal visible, opening on the right in wide windows and below it under 1100 logical pixels. **Settings fills the workspace and covers the shell**, while existing sessions keep running. An SSH tab's Files, Commands, and Tunnels open beside or, in narrow layouts, below its terminal for the same host. Switching shell tabs preserves the selected supporting pane. Cards, toolbars, and file panes adapt to their available width. `Alt+6` leaves Settings and returns to the terminal; `Alt+7` opens the selected shell's Files. Both PowerShell and CMD use Windows ConPTY. Manual GUI acceptance for this layout and CMD input/resize remains unverified.

The `CONPTY` / `PTY` dimensions show terminal character columns and rows. The adjacent **Copy last output** icon copies the latest rendered command output, including errors. PowerShell/CMD use runtime prompt markers without editing profile files. Tabs, control sequences, and newlines have already been processed by the PTY; this is rendered terminal text, not original stdout/stderr bytes. Command boundaries for custom or unmarked SSH shells are best effort.

Settings → Appearance offers [36 app themes](THEMES.md), including VS Code Dark+/Light+, Dracula, Monokai, Nord, Tokyo Night, and Catppuccin, while retaining gradient accents. The terminal's **Follow application theme** choice follows the named background, foreground, and ANSI palette; an independent terminal palette is also available.

Numeric settings use direct input without spin buttons. Settings → About provides separate **Reset settings** and **Reset SQLite data** actions, each requiring a Yes/No dialog defaulting to No. Settings reset immediately saves and applies defaults. SQLite reset deletes database user data, including saved hosts, history, commands, and launcher favorites/history, while preserving schema and migration metadata. Open sessions and separate settings, trusted keys, vault, and transfer-recovery files remain. Later activity from an open session can create new records.

Open **Multi Command** from the left navigation or `Alt+8`. It covers the central shell with a **3×3 session grid** while open shells keep running. Each occupied cell represents one open SSH, PowerShell, or CMD session. Up to 16 sessions are shown nine per page, with previous/next controls. Narrow windows retain three columns with scrolling.

The right side reuses the existing **command library**, including search, add/delete, and `$1`, `$2` parameter inputs. Both free-form commands entered above it and saved commands run **only on checked sessions**. **Select all / Clear all** applies to every page, and checked sessions on other pages are also broadcast targets. New sessions always start unchecked, with zero targets selected initially. Page, shell-tab, and navigation changes preserve selection, last results, and running state for the same open sessions. Each cell shows session state and the last result, with a scrollable output preview of up to 16,384 characters. PROD-tagged targets require an additional confirmation.

Transfers and `Alt+7` follow the selected shell: the same SSH session's Local/Remote Files or the current directory reported by a PowerShell/CMD prompt. Missing shells or unknown paths show an explanation without choosing another server. Browsing locally does not change the shell directory; **Current shell folder** resumes following it. Directly launched SSH/Multipass programs may not report a local directory.

### One-line local connections

On Home, enter a usual local connection command such as `ssh worker1` or `multipass connect master` in **One-line connection**. Sutty opens the direct Windows executable in a new local terminal, so `ssh worker1` continues to use the current PC's `.ssh/config`, `IdentityFile`, Windows SSH Agent, and `PATH`; Multipass uses its installed executable the same way. The launcher sends one executable plus arguments directly, never through `cmd.exe` or PowerShell. Shell chaining/redirection, executable paths, and command interpreters are rejected. SSH shortcuts are interactive host or configuration aliases only; they cannot include a remote command.

Use **★ Save** to keep a validated command, then select a favorite or recent entry to reopen it. Right-click an entry to remove it. Both lists are local to this PC and bounded to 100 favorites and 250 history entries. Sutty stores neither the resolved executable path nor terminal output, and rejects common credential-shaped arguments from storage. Arbitrary positional secrets cannot all be recognized; keep secrets out of command text. Finished commands are never replayed by switching tabs or restarting the app. These tabs host local programs; SFTP uses a separate SSH connection. Live SSH Agent/key/config, Multipass, and UI-interaction validation remains unverified.

This development build adds folder navigation history, sorting, hidden-file controls, per-host remote bookmarks and copy-only pane-to-pane drag and drop. Remote text editing uses a local executable, explicit upload by default, an optional per-file save-upload switch, immutable upload copies, metadata conflict checks and the existing verified transfer queue. Files remain recoverable under `%LOCALAPPDATA%\sutty\edits`; close the external editor and remove sensitive copies yourself when finished. Generic queue retry cannot bypass edit review after failure/restart. Edited files are limited to 8 MiB, with eight open copies per session.

Files → Terminal prepares/copies a quoted command without injecting or executing it. Open file path navigates the same session's Files explicitly. Tunnels provides session-local add/start/stop and real listener/error states with loopback defaults and explicit external-bind consent. Host menus provide duplication and authentication aliases; Settings imports and Hosts JSON sharing provide schema validation and per-item preview/selection. Export omits credentials, local key paths, trust records and history. External ProxyCommand profiles must be configured manually on the receiving PC.

Use the existing Windows Alpha build/publish commands. This is still an Alpha implementation: real-server/editor/drag-and-drop acceptance, the terminal compatibility matrix and production-signed installation remain unverified for these changes. **Windows UI automation on 2026-09-22 was stopped by the user's Escape input**, before acceptance of the rebuilt layout and manual clipboard interaction. The earlier live-server attempt was blocked by the unavailable local Docker engine. No new account service, cloud backend or central administration has been introduced.

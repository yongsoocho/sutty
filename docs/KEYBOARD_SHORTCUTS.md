# Keyboard shortcuts / 키보드 단축키

| Shortcut | 한국어 | English |
| --- | --- | --- |
| `Ctrl+1` … `Ctrl+9` | 1–9번째 열린 탭으로 전환 | Switch to open tab 1–9 |
| `Ctrl+T` | 새 탭 `+` 메뉴 열기 | Open the new-tab `+` menu |
| `Alt+1` | Home 보조 패널 열기 | Open the Home supporting pane |
| `Alt+2` | Hosts 보조 패널 열기 | Open the Hosts supporting pane |
| `Alt+3` | Transfers 보조 패널 열기 | Open the Transfers supporting pane |
| `Alt+4` | Commands 보조 패널 열기 | Open the Commands supporting pane |
| `Alt+5` | 전체 페이지 Settings 열기 | Open full-page Settings |
| `Alt+6` | 선택한 로컬/SSH 터미널로 돌아가기 | Return to the selected local/SSH terminal |
| `Alt+7` | 선택한 셸 옆이나 아래에 Files 열기 | Open Files beside or below the selected shell |
| `Alt+8` | 독립 Multi Command 탭 열기 | Open the separate Multi Command tab |
| `Ctrl+,` | 전체 페이지 Settings 열기 | Open full-page Settings |
| `Ctrl+Insert` | 선택한 입력/터미널 텍스트 복사 | Copy selected input or terminal text |
| `Shift+Insert` | Commands 입력 또는 현재 터미널에 붙여넣기 | Paste into Commands input or the active terminal |
| `Shift+Enter` | Commands 입력에서 줄바꿈 | Insert a new line in Commands input |
| `Enter` (Quick Connect) | 입력한 SSH 연결 시작 | Start the entered SSH connection |
| `Enter` (Commands) | 명령 실행 | Run the command |
| `Right Arrow` | 입력 끝에서 보이는 제안 적용 | Accept a visible suggestion at the input end |
| `Tab` | 설정된 경우 보이는 제안 적용 | Accept a visible suggestion when enabled |

상단 `+` 버튼과 `Ctrl+T`는 동일한 메뉴를 엽니다. 기본 강조 항목은 **New SSH
connection**이며, **Open saved host**, **Local PowerShell**, **Local CMD**, **Import hosts**를 함께
제공합니다.

The top `+` button and `Ctrl+T` open the same menu. **New SSH connection** is the default
emphasized action, followed by **Open saved host**, **Local PowerShell**, **Local CMD**, and **Import hosts**.

새로 시작하면 PowerShell을 열고 마지막 탭을 닫으면 새 PowerShell 탭을 엽니다. Home·Hosts·
Transfers·Commands는 넓은 창에서 오른쪽, 논리 픽셀 1100 미만 창에서 아래 패널에 엽니다.
SSH Files·Commands·Tunnels도 좁은 화면에서는 아래에 배치하며 터미널은 계속 보입니다.
Settings는 전체 페이지로 셸을 가리지만 세션은 계속 실행됩니다. `Alt+6`으로 설정을 나와
터미널로 돌아갑니다. 셸 탭을 바꿔도 선택한 보조 패널은 유지됩니다. 작업 공간 복원은 각 로컬 탭의 PowerShell/CMD 선택을 기억합니다.

A fresh start opens PowerShell, and closing the last tab opens a replacement PowerShell tab.
Home/Hosts/Transfers/Commands open on the right in wide windows and below the terminal under
1100 logical pixels. SSH Files/Commands/Tunnels also move below the terminal in narrow layouts.
Settings covers the shell as a full page while sessions keep running; `Alt+6` returns to the
terminal. Switching shell tabs preserves the selected supporting pane. Workspace restoration preserves each local tab's PowerShell/CMD choice.

`CONPTY` / `PTY` 크기 옆의 클립보드 아이콘은 **마지막 출력 복사**입니다. 오류를 포함한
마지막 명령의 표시 출력을 복사하며 `Ctrl+Insert`의 선택 영역 복사와는 별도 동작입니다.
PowerShell/CMD는 프로필 파일 수정 없이 실행 중 프롬프트 표시자를 사용합니다. 탭·제어
문자·줄바꿈은 PTY가 처리한 상태이며 사용자 정의 또는 표시자가 없는 SSH 명령 경계는
최선 방식으로 판별합니다. 테마는 Settings → Appearance의 [36개 팔레트](THEMES.md)에서 고릅니다.

The clipboard icon beside `CONPTY` / `PTY` dimensions is **Copy last output**. It copies the
latest rendered command output, including errors; `Ctrl+Insert` continues to copy the selection.
PowerShell/CMD use runtime prompt markers without changing profile files. The text reflects
PTY processing of tabs, control sequences, and newlines; custom or unmarked SSH command boundaries
are best effort. Choose among [36 palettes](THEMES.md) in Settings → Appearance.

`Alt+1` … `Alt+8`은 문자 키 위의 상단 숫자열을 사용합니다. 숫자 키패드의 숫자는 이
단축키에 포함되지 않습니다. `Alt+7`은 SSH에서는 같은 세션의 Files, PowerShell/CMD에서는
프롬프트가 알린 현재 로컬 폴더를 엽니다. 선택한 셸이나 경로가 없으면 안내를 표시하며
다른 서버를 대신 선택하지 않습니다. `Alt+6`은 선택한 로컬 터미널 탭으로도 돌아갑니다.

`Alt+1` … `Alt+8` use the top number row above the letter keys; numeric-keypad digits are not
included. `Alt+7` opens the same SSH session's Files or the current local directory reported by
a PowerShell/CMD prompt. Missing shells or unknown paths show an explanation without selecting
another server. `Alt+6` can also return to the selected local-terminal tab.

사용자에게 보이는 이름은 **Commands**입니다. 기존 설정과 작업 공간 복원 호환성을 위해
내부 저장값 `Repl`은 유지되며, 사용자가 변경할 필요는 없습니다. Commands에서 현재 줄이
Bash의 `\` 또는 PowerShell의 backtick으로 끝나면 Enter는 명령을 보내지 않고 다음 줄을
추가합니다. Terminal은 Tab과 방향키를 원격 PTY에 그대로 전달합니다.

The visible product label is **Commands**. Its internal persisted value remains `Repl` so existing
settings and restored workspaces remain compatible; users do not need to change it. In Commands,
Enter adds another line instead of executing when the current line ends in Bash `\` or a PowerShell
backtick. Terminal sends Tab and arrow keys directly to the remote PTY, preserving remote-shell
completion.

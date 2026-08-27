# Keyboard shortcuts / 키보드 단축키

| Shortcut | 한국어 | English |
| --- | --- | --- |
| `Ctrl+1` … `Ctrl+9` | 1–9번째 열린 탭으로 전환 | Switch to open tab 1–9 |
| `Ctrl+T` | 새 탭 `+` 메뉴 열기 | Open the new-tab `+` menu |
| `Alt+1` | Home으로 이동 | Open Home |
| `Alt+2` | Hosts로 이동 | Open Hosts |
| `Alt+3` | Transfers로 이동 | Open Transfers |
| `Alt+4` | Commands로 이동 | Open Commands |
| `Alt+5` | Settings로 이동 | Open Settings |
| `Alt+6` | 선택한 SSH 세션의 Terminal로 이동 | Open Terminal for the selected SSH session |
| `Alt+7` | 선택한 SSH 세션의 Files로 이동 | Open Files for the selected SSH session |
| `Ctrl+,` | 설정 창 열기 | Open Settings |
| `Ctrl+Insert` | 선택한 입력/터미널 텍스트 복사 | Copy selected input or terminal text |
| `Shift+Insert` | Commands 입력 또는 현재 터미널에 붙여넣기 | Paste into Commands input or the active terminal |
| `Shift+Enter` | Commands 입력에서 줄바꿈 | Insert a new line in Commands input |
| `Enter` (Quick Connect) | 입력한 SSH 연결 시작 | Start the entered SSH connection |
| `Enter` (Commands) | 명령 실행 | Run the command |
| `Right Arrow` | 입력 끝에서 보이는 제안 적용 | Accept a visible suggestion at the input end |
| `Tab` | 설정된 경우 보이는 제안 적용 | Accept a visible suggestion when enabled |

상단 `+` 버튼과 `Ctrl+T`는 동일한 메뉴를 엽니다. 기본 강조 항목은 **New SSH
connection**이며, **Open saved host**, **Local PowerShell**, **Import hosts**를 함께
제공합니다.

The top `+` button and `Ctrl+T` open the same menu. **New SSH connection** is the default
emphasized action, followed by **Open saved host**, **Local PowerShell**, and **Import hosts**.

`Alt+1` … `Alt+7`은 문자 키 위의 상단 숫자열을 사용합니다. 숫자 키패드의 숫자는 이
단축키에 포함되지 않습니다. 선택한 SSH 세션이 없으면 `Alt+7`은 처리되지만 새
원격 Files 화면을 열지 않습니다. `Alt+6`은 선택한 로컬 터미널 탭으로도 돌아갑니다.

`Alt+1` … `Alt+7` use the top number row above the letter keys; numeric-keypad digits are not
included. Without a selected SSH session, `Alt+7` is consumed but does not open a remote Files
view. `Alt+6` can also return to the selected local-terminal tab.

사용자에게 보이는 이름은 **Commands**입니다. 기존 설정과 작업 공간 복원 호환성을 위해
내부 저장값 `Repl`은 유지되며, 사용자가 변경할 필요는 없습니다. Commands에서 현재 줄이
Bash의 `\` 또는 PowerShell의 backtick으로 끝나면 Enter는 명령을 보내지 않고 다음 줄을
추가합니다. Terminal은 Tab과 방향키를 원격 PTY에 그대로 전달합니다.

The visible product label is **Commands**. Its internal persisted value remains `Repl` so existing
settings and restored workspaces remain compatible; users do not need to change it. In Commands,
Enter adds another line instead of executing when the current line ends in Bash `\` or a PowerShell
backtick. Terminal sends Tab and arrow keys directly to the remote PTY, preserving remote-shell
completion.

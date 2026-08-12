# Keyboard shortcuts / 키보드 단축키

| Shortcut | 한국어 | English |
| --- | --- | --- |
| `Ctrl+1` … `Ctrl+9` | 1–9번째 열린 탭으로 전환 | Switch to open tab 1–9 |
| `Ctrl+T` | 새 로컬 터미널 탭 열기 | Open a new local terminal tab |
| `Alt+1` … `Alt+6` | 왼쪽 메뉴(홈·기록·파일·명령·다중 명령·로그)를 위에서부터 전환 | Select the left work surfaces (Home through Logs) from top to bottom |
| `Alt+7` | 설정 창 열기 | Open Settings |
| `Ctrl+,` | 설정 창 열기 | Open Settings |
| `Ctrl+Insert` | 선택한 입력/터미널 텍스트 복사 | Copy selected input or terminal text |
| `Shift+Insert` | REPL 입력 또는 현재 터미널에 붙여넣기 | Paste into REPL input or the active terminal |
| `Shift+Enter` | REPL 입력에서 줄바꿈 | Insert a new line in REPL input |
| `Enter` | REPL 명령 실행 | Run the REPL command |
| `Right Arrow` | 입력 끝에서 보이는 제안 적용 | Accept a visible suggestion at the input end |
| `Tab` | 설정된 경우 보이는 제안 적용 | Accept a visible suggestion when enabled |

REPL에서 현재 줄이 Bash의 `\` 또는 PowerShell의 backtick으로 끝나면 Enter는 명령을
보내지 않고 다음 줄을 추가합니다. Terminal 모드에서는 Tab과 방향키를 원격 PTY에 그대로
전달하므로 원격 셸의 완성 기능을 사용할 수 있습니다.

In REPL mode, Enter adds another line instead of executing when the current line ends in Bash `\`
or a PowerShell backtick. Terminal mode sends Tab and arrow keys directly to the remote PTY, so
remote shell completion remains available.

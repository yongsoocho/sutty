# UI / UX review - 2026-10-06

소스 검토와 자동 검증으로 확인한 결함을 수정했습니다. Windows 화면 자동 조작은 사용자의 Escape 입력으로 중단되었습니다. 따라서 최종 화면의 DPI별 시각 인수, 실제 서버 전송, 외부 편집기, 드래그 동작까지 통과했다고 판단하지 않습니다.

Source review and automated checks identified and corrected the defects below. The user stopped Windows Computer Use with Escape. Final native visual acceptance, DPI coverage, live SSH/SFTP, external-editor interaction, and drag-and-drop remain unverified.

## Corrected defects / 수정한 결함

| Area | Trigger and correction / 재현 조건과 수정 |
| --- | --- |
| Panel width | Automatic arrangement and pane closure could overwrite the user's preferred width. Only splitter intent is persisted; temporary layout constraints retain the preference. / 창 크기 변경이나 패널 닫기가 선호 너비를 덮어쓰지 않도록 수정했습니다. |
| Terminal layout | SSH status/copy controls covered terminal cells. They now occupy a separate row; long route names are bounded and titles can truncate. / 상태 표시와 출력 복사 버튼이 터미널 내용을 가리지 않도록 옮겼습니다. |
| Terminal sizing | Very small or large browser viewports emitted dimensions rejected by the native PTY bridge. Fitting now applies the same 20-500 column and 5-200 row bounds before reporting. / 화면과 실제 PTY의 크기 계약을 일치시켰습니다. |
| Search | The search toolbar exceeded narrow terminal panes and window focus returned to the terminal even while search was open. The toolbar fits its pane and preserves search focus. / 좁은 검색창과 창 복귀 시 검색 포커스를 수정했습니다. |
| Headers/settings | Session navigation uses measured button widths, long settings labels wrap, and reopening Settings reloads current values. Window-width input now matches the persisted 720px minimum. / 버튼 줄바꿈, 긴 설정 항목, 다시 연 설정의 오래된 값과 너비 하한을 수정했습니다. |
| File activation | List-level double-click could open the previously selected file from empty space. Double-click now uses the clicked row. / 빈 공간 더블클릭으로 이전 선택 파일이 열리지 않도록 수정했습니다. |
| Local navigation | Late cancelled enumeration errors could replace current state; path normalization synchronously checked slow filesystems. Cancelled results are ignored and enumeration owns filesystem access on its worker. / 취소된 오류의 뒤늦은 반영과 느린 경로의 UI 차단을 수정했습니다. |
| Remote context | Queued callbacks and search dialogs could retain an older session or directory. Each operation snapshots and rechecks its owner; failed navigation restores the last valid path display. / 서버·경로를 고정하고 지연된 콜백이 다른 화면을 갱신하지 않도록 수정했습니다. |
| Download picker | The legacy save picker could create a destination before transfer, causing a false collision and leaving an empty file after cancellation. The SDK desktop picker selects a path; the existing staged transfer pipeline owns creation and collision handling. / 전송 전 빈 파일 생성과 잘못된 충돌을 방지했습니다. |
| Remote editing | Action buttons used horizontal scrolling and long edit details could consume the file pane. Buttons wrap and details have bounded vertical scrolling. / 편집 동작을 줄바꿈하고 상세 영역 높이를 제한했습니다. |
| Multi targets | Returning to Multi could retain targets. Entry clears all selections while keeping session identity, running state and results. Refresh no longer rebuilds every card or resets scrolling. / 재진입 시 대상을 해제하며 결과와 스크롤은 유지합니다. |
| Template expansion | Sequential replacement could reinterpret a supplied `$1` value while expanding `$12`; large parameter numbers could overflow. One-pass expansion preserves literal values and ignores unsupported numbers. / 매개변수 값의 재치환과 큰 번호로 인한 오류를 수정했습니다. |
| Command drafts | Library refresh could discard parameter inputs; cancelled Multi previews incremented usage and collapsed inputs. Compatible drafts survive refresh and usage changes only after execution approval. / 입력을 보존하고 승인 전 사용 횟수가 바뀌지 않도록 수정했습니다. |
| Tab closure | A pending WebView initialization, output drain or clipboard retry could complete after tab removal. Explicit, idempotent teardown unsubscribes events, stops timers, cancels view work and closes WebView2. Unloaded/reparenting keeps live tabs running. / 닫힌 탭의 지연 동작을 차단하면서 화면 전환 중 세션은 유지합니다. |

The desktop save picker's path-only behavior is specified in Microsoft's [Windows App SDK picker design](https://github.com/microsoft/WindowsAppSDK/blob/main/specs/Storage.Pickers/Microsoft.Windows.Storage.Pickers.md), beginning with SDK 2.0; this project already pins 2.3.1. Its native dialog interaction still requires acceptance. 기존 안전 전송 절차와 충돌 확인은 유지하며 저장창의 실제 조작 인수는 남아 있습니다.

## Automated verification / 자동 검증

- x64 Debug and Release UI compilation; ARM64 Release compilation.
- Settings self-tests: persistence/reset/theme catalog, panel-width intent, shell navigation, Multi selection, command parameter drafts, local browsing and cancelled errors.
- Command self-tests: broadcast consent, saved-host/import/history/sharing, and SQLite reset/rollback.
- SFTP self-tests: safe local transfer, checkpoints/phases, remote edit snapshots/conflicts/recovery and Transfer Center routing/concurrency.
- Security self-tests: host-key trust, credential vault, tunnels and explicit directory commands. DPAPI required the normal user execution context outside the restricted test process.
- Terminal self-tests: actual PowerShell/CMD ConPTY input, resize, exit/reopen, Unicode paths, code-page 437 and shutdown during heavy output.
- Node/Edge renderer tests: 27 executed, 27 passed, no skips, including actual PowerShell/CMD capture replay. Responsive browser cases include 150x55, 190x210 and 5000x4000 pixels.
- Product-scope guard.

Native PowerShell tests used the installed Windows module directory for `PSModulePath`: the tool runtime's bundled PSReadLine otherwise displayed a publisher-trust prompt. No publisher trust or system execution policy was changed. The unterminated-output fixture isolates prompt repaint from version-dependent unmarked trailing blank cells; explicit newline fixtures still check trailing spaces.

## Remaining acceptance / 남은 인수 검증

- Native Windows window sizes and 100/150/200% scaling, Korean/English text, splitter dragging, accessibility and keyboard focus.
- Repeated tab closure during WebView startup and clipboard retries, checking native resources and callback behavior.
- New/existing download paths, picker cancellation and failed downloads against a real SFTP server.
- Multi previews and advanced local/external consent with actual foreground programs; live remote cancellation outcomes.
- External-editor save/conflict/recovery, drag payloads and representative long-running transfers.

Automated checks do not establish that every convenience feature is free of side effects. These changes do not promote the existing Alpha live-compatibility claims. 자동 검증을 모든 기능의 무부작용 보증이나 실환경 인수 완료로 해석하지 않습니다.

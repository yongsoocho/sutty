# ADR 0001: Terminal Renderer / 터미널 렌더러

- **Status / 상태:** Accepted for Alpha only; transitional / Alpha에만 승인된 임시 결정
- **Date / 날짜:** 2026-08-11
- **Decision owners / 결정 주체:** Sutty product and engineering / Sutty 제품·개발

## English

### Context

The product specification selects a locally bundled **xterm.js renderer hosted in a hardened WebView2** for the GA terminal. It also requires a real SSH PTY, runtime server-side resize, ANSI/VT compatibility, alternate screen, color, mouse mode, Unicode cell correctness, search, copy/paste, input-method support, bounded resources, and security regression testing.

The current Alpha already has a real SSH.NET `ShellStream` PTY with runtime server-side resize through the public `ChangeWindowSize` API, but its renderer is a native WinUI `TextBlock` backed by [`VtScreenBuffer`](../../src/sutty.UI/Helpers/VtScreenBuffer.cs). The buffer is bounded and understands a useful subset of cursor, erase, scrolling, alternate-screen, device-response, and application-cursor behavior. [`SessionView`](../../src/sutty.UI/Views/SessionView.xaml.cs) bounds queued terminal output and batches UI updates.

This is not an xterm-compatible renderer:

- SGR is consumed without rendering color or style.
- Mouse reporting is absent.
- Wide, emoji, and combining characters do not have a complete terminal-cell model.
- Search and dedicated terminal copy/paste behavior are incomplete.
- The focused self-test is a parser smoke test, not the required shell/TUI/security/soak matrix.

### Decision

Keep the bounded native renderer only as an **Alpha engineering bridge** while SSH lifecycle, host-key, SFTP, and workspace flows are developed.

Sutty must not claim GA terminal compatibility, complete legacy-client replacement, or conformance for `vim`, `tmux`, ncurses, mouse applications, or arbitrary escape sequences while this decision remains active.

Before GA, implement the specification's local xterm.js/WebView2 renderer unless a superseding ADR demonstrates an alternative with equal or stronger compatibility, security, accessibility, and maintenance evidence.

### Required transition controls

The replacement must:

1. Bundle xterm.js, CSS, and bridge code as package-local pinned assets; no CDN or remote script.
2. Restrict WebView2 navigation and new windows to the application origin, apply a restrictive Content Security Policy, expose no host object, and validate a small typed JSON message schema.
3. Treat terminal bytes, OSC content, titles, links, and escape sequences as untrusted data; never write terminal output through `innerHTML`.
4. Preserve bounded queues, backpressure, cancellation, generation isolation, and a documented overflow behavior.
5. Preserve server-side PTY resize through SSH.NET's public `ChangeWindowSize` API and drive it from the replacement renderer's measured viewport; reflection into SSH.NET private channels is not an accepted release design.
6. Cover control keys, IME/UTF-8, bracketed paste, color, alternate screen, mouse, wide/combining cells, search, selection, clipboard behavior, and accessibility.
7. Pass representative `bash`, `zsh`, PowerShell over SSH, `vim`, `nano`, `less`, `top`, `htop`, `tmux`, and long-running output tests.
8. Pass malicious escape-sequence, external-navigation, bridge-schema, memory-bound, latency, and soak tests.

### Consequences

- The native buffer remains small and bounded; feature growth that tries to turn it into a second full terminal emulator should be rejected.
- Alpha users receive a real interactive channel with clearly documented rendering limitations.
- Renderer migration is a P0 GA gate and may change the terminal presentation implementation without changing the PTY/session contract.
- Documentation and release notes must continue to say **Alpha, not GA** until the transition criteria have evidence.

Traceability: [TERM-001 through TERM-006](../REQUIREMENTS.md#terminal--터미널) remain Partial or Planned under this ADR.

---

## 한국어

### 배경

제품 명세는 GA 터미널로 **로컬에 포함한 xterm.js 렌더러와 hardening된 WebView2 host**를 선택합니다. 실제 SSH PTY, 실행 중 서버 resize, ANSI/VT 호환성, 대체 화면, 색상, 마우스 모드, 정확한 Unicode 셀, 검색, copy/paste, 입력기 지원, 제한된 리소스, 보안 회귀 테스트도 요구합니다.

현재 Alpha에는 공개 `ChangeWindowSize` API로 실행 중 서버 측 크기 변경을 지원하는 실제 SSH.NET `ShellStream` PTY가 있지만 렌더러는 [`VtScreenBuffer`](../../src/sutty.UI/Helpers/VtScreenBuffer.cs)를 사용하는 네이티브 WinUI `TextBlock`입니다. 이 버퍼는 크기가 제한되어 있고 일부 커서·지우기·스크롤·대체 화면·장치 응답·application cursor 동작을 처리합니다. [`SessionView`](../../src/sutty.UI/Views/SessionView.xaml.cs)는 대기 중인 터미널 출력 크기를 제한하고 UI 갱신을 묶어서 처리합니다.

이는 xterm 호환 렌더러가 아닙니다.

- SGR을 소비하지만 색과 스타일을 표시하지 않습니다.
- 마우스 보고가 없습니다.
- 넓은 문자·emoji·결합 문자를 위한 완전한 터미널 셀 모델이 없습니다.
- 검색과 전용 터미널 copy/paste 동작이 미완성입니다.
- 현재 집중형 self-test는 parser smoke test이며 필수 shell/TUI/security/soak 매트릭스가 아닙니다.

### 결정

SSH 수명주기, 호스트키, SFTP, workspace 흐름을 개발하는 동안 제한된 네이티브 렌더러를 **Alpha 엔지니어링 연결 단계**로만 유지합니다.

이 결정이 유효한 동안 Sutty는 GA 터미널 호환성, 기존 터미널 클라이언트의 완전한 대체, `vim`·`tmux`·ncurses·마우스 앱·임의 escape sequence 호환성을 주장하면 안 됩니다.

GA 전에 명세의 로컬 xterm.js/WebView2 렌더러를 구현해야 합니다. 다른 대안을 선택하려면 동등하거나 더 강한 호환성·보안·접근성·유지보수 증거를 가진 후속 ADR이 필요합니다.

### 필수 전환 조건

교체 구현은 다음을 충족해야 합니다.

1. xterm.js·CSS·bridge 코드를 package-local 고정 버전 asset으로 포함하고 CDN·원격 script를 사용하지 않습니다.
2. WebView2 navigation과 새 창을 앱 origin으로 제한하고, 강한 Content Security Policy를 적용하며, host object를 노출하지 않고, 작은 typed JSON message schema를 검증합니다.
3. 터미널 byte·OSC 내용·title·link·escape sequence를 신뢰하지 않는 데이터로 취급하고 터미널 출력을 `innerHTML`로 쓰지 않습니다.
4. 제한된 queue, backpressure, 취소, 세대 격리, 문서화된 overflow 동작을 유지합니다.
5. SSH.NET 공개 `ChangeWindowSize` API를 통한 서버 측 PTY resize를 유지하고 교체 렌더러가 측정한 viewport로 이를 구동합니다. SSH.NET private channel에 대한 reflection은 승인된 릴리스 설계가 아닙니다.
6. 제어키, IME/UTF-8, bracketed paste, 색, 대체 화면, 마우스, 넓은 문자·결합 문자 셀, 검색, 선택, clipboard 동작, 접근성을 지원합니다.
7. 대표 `bash`, `zsh`, SSH PowerShell, `vim`, `nano`, `less`, `top`, `htop`, `tmux`, 장시간 출력 테스트를 통과합니다.
8. 악성 escape sequence, 외부 navigation, bridge schema, 메모리 제한, 지연, 장시간 실행 테스트를 통과합니다.

### 결과

- 네이티브 버퍼는 작고 제한된 상태로 유지합니다. 이를 두 번째 완전한 터미널 emulator로 키우려는 기능 확장은 거부해야 합니다.
- Alpha 사용자는 렌더링 한계를 명확히 안내받은 실제 대화형 채널을 사용합니다.
- 렌더러 전환은 P0 GA 게이트이며 PTY/session 계약을 유지한 채 표시 구현을 바꿀 수 있습니다.
- 전환 조건의 증거가 생길 때까지 문서와 릴리스 노트는 계속 **Alpha, GA 아님**이라고 표시해야 합니다.

추적: 이 ADR이 유효한 동안 [TERM-001부터 TERM-006](../REQUIREMENTS.md#terminal--터미널)은 Partial 또는 Planned 상태를 유지합니다.

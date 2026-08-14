# ADR 0001: Terminal Renderer / 터미널 렌더러

- **Status / 상태:** Implemented for Alpha; GA verification pending / Alpha 구현 완료, GA 검증 대기
- **Date / 날짜:** 2026-08-13
- **Decision owners / 결정 주체:** Sutty product and engineering / Sutty 제품·개발

## English

### Context

The product specification selects a locally bundled **xterm.js renderer hosted in a hardened WebView2** for the GA terminal. It also requires a real SSH PTY, runtime server-side resize, ANSI/VT compatibility, alternate screen, color, mouse mode, Unicode cell correctness, search, copy/paste, input-method support, bounded resources, and security regression testing.

The current Alpha now uses pinned package-local xterm.js 6.0.0, fit 0.11.0, and search 0.16.0 assets hosted by [`TerminalRendererControl`](../../src/sutty.UI/Controls/TerminalRendererControl.cs) in WebView2. Both SSH.NET `ShellStream` and local ConPTY feed the same renderer. The host measures the xterm viewport and preserves runtime server-side resize through the public `ChangeWindowSize` API.

The renderer provides ANSI/VT color and style, alternate screen, mouse/input modes, IME and Unicode cell handling, selection, search, and bracketed-paste-aware input. Integration is complete for Alpha, but the required representative shell/TUI, malicious-sequence, keyboard-layout, latency, memory, and soak matrix is not complete; GA compatibility is therefore not claimed.

### Decision

Use the package-local xterm.js/WebView2 renderer as Sutty's single interactive terminal presentation for SSH and local ConPTY. Keep `VtScreenBuffer` only as a bounded parser/self-test fixture; do not grow it into a second runtime emulator.

Sutty must not claim GA terminal compatibility or conformance for the representative TUI matrix until the remaining acceptance evidence is recorded.

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

- Pinned third-party bytes and their MIT license are recorded beside the packaged assets.
- The WebView2 origin is local-only, navigation and permissions are denied, no host object is exposed, and the typed versioned bridge validates message types and bounds.
- Output uses bounded queues and one acknowledged in-flight write; overflow resets the screen with an explicit notice instead of slicing UTF-8 or VT sequences.
- Documentation and release notes continue to say **Alpha, not GA** until the compatibility, security, latency, and soak criteria have evidence.

Traceability: [TERM-001 through TERM-006](../REQUIREMENTS.md#terminal--터미널) remain Partial until the required matrix has evidence.

---

## 한국어

### 배경

제품 명세는 GA 터미널로 **로컬에 포함한 xterm.js 렌더러와 hardening된 WebView2 host**를 선택합니다. 실제 SSH PTY, 실행 중 서버 resize, ANSI/VT 호환성, 대체 화면, 색상, 마우스 모드, 정확한 Unicode 셀, 검색, copy/paste, 입력기 지원, 제한된 리소스, 보안 회귀 테스트도 요구합니다.

현재 Alpha는 WebView2 안의 [`TerminalRendererControl`](../../src/sutty.UI/Controls/TerminalRendererControl.cs)이 패키지 내부에 고정한 xterm.js 6.0.0, fit 0.11.0, search 0.16.0을 사용합니다. SSH.NET `ShellStream`과 로컬 ConPTY가 같은 렌더러를 사용하며, host가 xterm viewport를 측정해 공개 `ChangeWindowSize` API의 실행 중 서버 측 크기 변경을 유지합니다.

렌더러는 ANSI/VT 색·스타일, 대체 화면, 마우스·입력 모드, IME·Unicode 셀, 선택, 검색, bracketed paste 입력을 제공합니다. Alpha 통합은 완료됐지만 대표 셸·TUI, 악성 sequence, 키보드 배열, 지연, 메모리, 장시간 실행 매트릭스는 아직 완성되지 않았으므로 GA 호환성을 주장하지 않습니다.

### 결정

패키지 내부 xterm.js/WebView2 렌더러를 SSH와 로컬 ConPTY의 단일 대화형 터미널 표시 구현으로 사용합니다. `VtScreenBuffer`는 제한된 parser/self-test fixture로만 유지하며 두 번째 runtime emulator로 확장하지 않습니다.

대표 TUI 매트릭스의 남은 인수 증거가 기록될 때까지 Sutty는 GA 터미널 호환성을 주장하지 않습니다.

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

- 고정한 third-party byte와 MIT 라이선스를 패키지 자산 옆에 기록합니다.
- WebView2 origin은 로컬 전용이고 탐색·권한을 거부하며 host object를 노출하지 않습니다. 형식과 크기를 검증하는 typed versioned bridge만 사용합니다.
- 출력은 제한된 queue와 한 개의 확인 대기 write를 사용합니다. overflow 시 UTF-8·VT sequence를 자르지 않고 명시적 알림과 함께 화면을 재설정합니다.
- 호환성·보안·지연·장시간 실행 조건의 증거가 생길 때까지 문서와 릴리스 노트는 계속 **Alpha, GA 아님**이라고 표시합니다.

추적: 필수 매트릭스의 증거가 생길 때까지 [TERM-001부터 TERM-006](../REQUIREMENTS.md#terminal--터미널)은 Partial 상태를 유지합니다.

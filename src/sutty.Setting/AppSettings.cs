namespace sutty.Setting;

/// <summary>settings.json에 저장되는 앱 설정. 새 설정은 여기에 프로퍼티만 추가하면 된다.</summary>
public sealed class AppSettings
{
    /// <summary>Theme preset name selected by the user.</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>UI 언어: "ko"(한국어) 또는 "en"(English).</summary>
    public string Language { get; set; } = "ko";

    public string TerminalFontFamily { get; set; } = "Cascadia Mono";
    public int TerminalFontSize { get; set; } = 13;

<<<<<<< HEAD
    /// <summary>Terminal palette id, or FollowApplication to follow the app light/dark mode.</summary>
    public string TerminalTheme { get; set; } = "FollowApplication";

    /// <summary>xterm cursor shape: underline, bar, or block.</summary>
    public string TerminalCursorStyle { get; set; } = "underline";

    public bool TerminalCursorBlink { get; set; } = true;

    /// <summary>Bounded xterm scrollback retained per live terminal.</summary>
    public int TerminalScrollbackLines { get; set; } = 5_000;

    /// <summary>Expose xterm's accessibility tree for screen-reader users.</summary>
    public bool TerminalScreenReaderMode { get; set; }

    /// <summary>Load the user's PowerShell profile in newly opened local terminal tabs.</summary>
    public bool LoadLocalShellProfile { get; set; } = true;

=======
>>>>>>> e47dd3e633b929266266b8bb37b277af3130f013
    /// <summary>Color JSON/YAML keys, strings, numbers, literals, and comments in REPL cells.</summary>
    public bool EnableStructuredTextHighlighting { get; set; } = true;

    /// <summary>Mark critical/error text red and warning text amber.</summary>
    public bool EnableSeverityHighlighting { get; set; } = true;

    /// <summary>Suggest matching recent and saved commands while entering a REPL command.</summary>
    public bool EnableCommandSuggestions { get; set; } = true;

    /// <summary>Allow Tab, in addition to Right Arrow, to accept a visible suggestion.</summary>
    public bool AcceptSuggestionWithTab { get; set; } = true;

    public int DefaultSshPort { get; set; } = 22;
    public int DefaultKeepAliveSeconds { get; set; } = 30;

    /// <summary>Home 화면에서 마지막으로 선택한 SSH 인증 방식.</summary>
    public string LastAuthMethod { get; set; } = "Password";

    /// <summary>
    /// 최근 사용한 개인키 파일 경로. 경로만 저장하며 비밀번호와 passphrase는 저장하지 않는다.
    /// </summary>
    public List<string> RecentPrivateKeyPaths { get; set; } = [];

    /// <summary>Home 연결 태그 입력 시 제안할 최근 태그.</summary>
    public List<string> RecentConnectionTags { get; set; } = [];

    private string _terminalMode = "Repl";

    /// <summary>
    /// 터미널 표시 방식: "Repl"(구조화된 셀) 또는 "Terminal"(대화형 PTY).
    /// 이전 버전의 "Raw" 값은 JSON 역직렬화 시 자동으로 "Terminal"로 마이그레이션한다.
    /// </summary>
    public string TerminalMode
    {
        get => _terminalMode;
        set => _terminalMode = string.Equals(value, "Raw", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Terminal", StringComparison.OrdinalIgnoreCase)
                ? "Terminal"
                : "Repl";
    }

    /// <summary>접속 히스토리 보관 일수 (append-only 로그, 지난 기록은 삭제).</summary>
    public int HistoryRetentionDays { get; set; } = 60;

    /// <summary>접속 횟수 기준으로 History 상단에 표시할 호스트 수.</summary>
    public int HistoryTopHostCount { get; set; } = 4;

    // ── 창 크기 (클라이언트 영역, px). Deep Field 기준 크기로 시작하고 이후 사용자 값을 기억 ──
    public int MainWindowWidth { get; set; } = 1360;
    public int MainWindowHeight { get; set; } = 850;
    public int SettingWindowWidth { get; set; } = 520;
    public int SettingWindowHeight { get; set; } = 660;

    /// <summary>오른쪽 패널 폭 (스플리터 드래그로 조절, 논리 px).</summary>
    public int RightPanelWidth { get; set; } = 316;
}

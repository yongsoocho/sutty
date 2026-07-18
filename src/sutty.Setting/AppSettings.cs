namespace sutty.Setting;

/// <summary>settings.json에 저장되는 앱 설정. 새 설정은 여기에 프로퍼티만 추가하면 된다.</summary>
public sealed class AppSettings
{
    /// <summary>"Dark" 또는 "Light".</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>UI 언어: "ko"(한국어) 또는 "en"(English).</summary>
    public string Language { get; set; } = "ko";

    public string TerminalFontFamily { get; set; } = "Cascadia Mono";
    public int TerminalFontSize { get; set; } = 13;

    public int DefaultSshPort { get; set; } = 22;
    public int DefaultKeepAliveSeconds { get; set; } = 30;

    /// <summary>터미널 표시 방식: "Repl"(대화 블록) 또는 "Raw"(PuTTY식 검정 화면).</summary>
    public string TerminalMode { get; set; } = "Repl";

    /// <summary>접속 히스토리 보관 일수 (append-only 로그, 지난 기록은 삭제).</summary>
    public int HistoryRetentionDays { get; set; } = 60;

    /// <summary>History 상단에 고정할 자주 접속 호스트 개수.</summary>
    public int HistoryPinnedTop { get; set; } = 4;

    // ── 창 크기 (클라이언트 영역, px). 0이면 시스템 기본 크기 사용 ──
    public int MainWindowWidth { get; set; }
    public int MainWindowHeight { get; set; }
    public int SettingWindowWidth { get; set; } = 520;
    public int SettingWindowHeight { get; set; } = 660;

    /// <summary>오른쪽 패널 폭 (스플리터 드래그로 조절, 논리 px).</summary>
    public int RightPanelWidth { get; set; } = 336;
}

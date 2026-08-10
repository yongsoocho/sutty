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

    /// <summary>Home 화면에서 마지막으로 선택한 SSH 인증 방식.</summary>
    public string LastAuthMethod { get; set; } = "Password";

    /// <summary>
    /// 최근 사용한 개인키 파일 경로. 경로만 저장하며 비밀번호와 passphrase는 저장하지 않는다.
    /// </summary>
    public List<string> RecentPrivateKeyPaths { get; set; } = [];

    /// <summary>Home 연결 태그 입력 시 제안할 최근 태그.</summary>
    public List<string> RecentConnectionTags { get; set; } = [];

    /// <summary>터미널 표시 방식: "Repl"(구조화된 셀) 또는 "Raw"(연속 명령 로그).</summary>
    public string TerminalMode { get; set; } = "Repl";

    /// <summary>접속 히스토리 보관 일수 (append-only 로그, 지난 기록은 삭제).</summary>
    public int HistoryRetentionDays { get; set; } = 60;

    // ── 창 크기 (클라이언트 영역, px). Deep Field 기준 크기로 시작하고 이후 사용자 값을 기억 ──
    public int MainWindowWidth { get; set; } = 1360;
    public int MainWindowHeight { get; set; } = 850;
    public int SettingWindowWidth { get; set; } = 520;
    public int SettingWindowHeight { get; set; } = 660;

    /// <summary>오른쪽 패널 폭 (스플리터 드래그로 조절, 논리 px).</summary>
    public int RightPanelWidth { get; set; } = 316;
}

namespace sutty.Setting;

/// <summary>settings.json에 저장되는 앱 설정. 새 설정은 여기에 프로퍼티만 추가하면 된다.</summary>
public sealed class AppSettings
{
    /// <summary>"Dark" 또는 "Light".</summary>
    public string Theme { get; set; } = "Dark";

    public string TerminalFontFamily { get; set; } = "Cascadia Mono";
    public int TerminalFontSize { get; set; } = 13;

    public int DefaultSshPort { get; set; } = 22;
    public int DefaultKeepAliveSeconds { get; set; } = 30;

    /// <summary>연결 중인 탭을 닫을 때 확인 다이얼로그를 띄울지.</summary>
    public bool ConfirmOnTabClose { get; set; } = true;

    // ── 창 크기 (클라이언트 영역, px). 0이면 시스템 기본 크기 사용 ──
    public int MainWindowWidth { get; set; }
    public int MainWindowHeight { get; set; }
    public int SettingWindowWidth { get; set; } = 520;
    public int SettingWindowHeight { get; set; } = 660;

    /// <summary>오른쪽 패널 폭 (스플리터 드래그로 조절, 논리 px).</summary>
    public int RightPanelWidth { get; set; } = 336;
}

namespace sutty.Setting;

/// <summary>settings.json에 저장되는 앱 설정. 새 설정은 여기에 프로퍼티만 추가하면 된다.</summary>
public sealed class AppSettings
{
    public string TerminalFontFamily { get; set; } = "Cascadia Mono";
    public int TerminalFontSize { get; set; } = 13;

    public int DefaultSshPort { get; set; } = 22;
    public int DefaultKeepAliveSeconds { get; set; } = 30;

    /// <summary>연결 중인 탭을 닫을 때 확인 다이얼로그를 띄울지.</summary>
    public bool ConfirmOnTabClose { get; set; } = true;
}

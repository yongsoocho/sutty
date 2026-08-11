using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Sessions;
using sutty.UI.Views;

namespace sutty.UI.ViewModels;

/// <summary>
/// Multi command 4×4 그리드의 슬롯 하나.
/// 세션이 있으면 체크박스(기본 on)로 브로드캐스트 대상 여부를 정하고, 없으면 빈 칸.
/// LastOutput에 마지막 브로드캐스트 결과가 작게 표시된다.
/// </summary>
public sealed class MultiSlotVm : ObservableObject
{
    // XAML 타입 생성기가 init 접근자를 지원하지 않아 set 사용 (생성 후 바꾸지 말 것)
    public SessionView? View { get; set; }

    /// <summary>체크된 세션에만 명령이 전송된다 (기본 전체 체크).</summary>
    public bool IsSelected { get; set; }

    private string _lastOutput = "";
    /// <summary>이 세션에서 실행한 마지막 브로드캐스트 명령의 출력(축약).</summary>
    public string LastOutput
    {
        get => _lastOutput;
        set => SetProperty(ref _lastOutput, value);
    }

    private string _resultText = "";
    /// <summary>Last command outcome, for example "exit 0" or "failed".</summary>
    public string ResultText
    {
        get => _resultText;
        set => SetProperty(ref _resultText, value);
    }

    public bool HasSession => View is not null;
    public bool IsEmpty => View is null;

    public string Title => View?.Session.Info.Title ?? "";

    public string HostText => View is null
        ? ""
        : $"{View.Session.Info.Host}:{View.Session.Info.Port}";

    public string StateText => View?.Session.State switch
    {
        null => "",
        SessionState.Connecting => "connecting…",
        SessionState.Connected => "connected",
        SessionState.Disconnecting => "disconnecting…",
        SessionState.Disconnected => "disconnected",
        SessionState.Failed => "failed",
        _ => "ready",
    };

    public Brush? StateBrush => View is null
        ? null
        : (Brush)Application.Current.Resources[View.Session.State switch
        {
            SessionState.Connected => "StatusGreen",
            SessionState.Connecting or SessionState.Disconnecting => "StatusAmber",
            SessionState.Failed => "StatusRed",
            _ => "StatusIdle",
        }];
}

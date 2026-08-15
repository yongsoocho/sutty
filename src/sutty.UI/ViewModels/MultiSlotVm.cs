using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Commands;
using sutty.Core.Sessions;
using sutty.Core.Sftp;
using sutty.Core.Terminal;
using sutty.UI.Helpers;
using sutty.UI.Views;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace sutty.UI.ViewModels;

/// <summary>
/// Multi command 4×4 그리드의 슬롯 하나.
/// 세션이 있으면 사용자가 체크박스로 브로드캐스트 대상 여부를 명시하고, 없으면 빈 칸.
/// LastOutput에 마지막 브로드캐스트 결과가 작게 표시된다.
/// </summary>
public sealed class MultiSlotVm : ObservableObject
{
    // XAML 타입 생성기가 init 접근자를 지원하지 않아 set 사용 (생성 후 바꾸지 말 것)
    public SessionView? View { get; set; }
    public LocalTerminalView? LocalView { get; set; }

    /// <summary>체크된 세션에만 명령이 전송된다. 새 대상은 안전을 위해 선택하지 않는다.</summary>
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

    public bool HasSession => View is not null || LocalView is not null;
    public bool IsEmpty => !HasSession;

    public string Title => View?.Session.Info.Title
        ?? (LocalView is null ? "" : "PowerShell");

    public string HostText => View is not null
        ? $"{View.Session.Info.Host}:{View.Session.Info.Port}"
        : LocalView is not null
            ? $"{Loc.T("로컬", "local")} · {Environment.UserName}@{Environment.MachineName}"
            : "";

    public string StateText => View is not null
        ? View.Session.State switch
        {
            SessionState.Connecting => Loc.T("연결 중…", "connecting…"),
            SessionState.Connected => Loc.T("연결됨", "connected"),
            SessionState.Disconnecting => Loc.T("연결 종료 중…", "disconnecting…"),
            SessionState.Disconnected => Loc.T("연결 끊김", "disconnected"),
            SessionState.Failed => Loc.T("실패", "failed"),
            _ => Loc.T("준비", "ready"),
        }
        : LocalView?.Terminal.TerminalState switch
        {
            TerminalState.Opening => Loc.T("시작 중…", "starting…"),
            TerminalState.Open => Loc.T("로컬 · 실행 중", "local · running"),
            TerminalState.Failed => Loc.T("로컬 · 실패", "local · failed"),
            TerminalState.Closed => Loc.T("로컬 · 닫힘", "local · closed"),
            _ => "",
        };

    public Brush? StateBrush => IsEmpty
        ? null
        : (Brush)Application.Current.Resources[View is not null
            ? View.Session.State switch
            {
                SessionState.Connected => "StatusGreen",
                SessionState.Connecting or SessionState.Disconnecting => "StatusAmber",
                SessionState.Failed => "StatusRed",
                _ => "StatusIdle",
            }
            : LocalView!.Terminal.TerminalState switch
            {
                TerminalState.Open => "StatusGreen",
                TerminalState.Opening => "StatusAmber",
                TerminalState.Failed => "StatusRed",
                _ => "StatusIdle",
            }];

    public object? SessionKey => (object?)View ?? LocalView;

    public bool CanUseSftp => View?.Session is
    {
        State: SessionState.Connected,
        SftpState: SftpConnectionState.Ready,
    };

    public MultiSftpTarget? CreateSftpTarget(string remoteDirectory) => CanUseSftp
        ? new MultiSftpTarget(
            View!.Session.Id.ToString("N"),
            Title,
            View.Session.Sftp,
            remoteDirectory)
        : null;

    public Task<CommandExecutionResult> ExecuteAsync(
        string command,
        CancellationToken cancellationToken = default) => View is not null
        ? View.RunExternalCommandDetailedAsync(command, cancellationToken)
        : LocalView is not null
            ? LocalView.RunExternalCommandDetailedAsync(command, cancellationToken)
            : Task.FromException<CommandExecutionResult>(
                new InvalidOperationException("The broadcast slot is empty."));

    public bool IsProduction => View?.Session.Info.Tags.Any(tag =>
        tag.Trim().Equals("prod", StringComparison.OrdinalIgnoreCase) ||
        tag.Trim().Equals("production", StringComparison.OrdinalIgnoreCase) ||
        tag.Trim().StartsWith("prod-", StringComparison.OrdinalIgnoreCase) ||
        tag.Trim().StartsWith("prod_", StringComparison.OrdinalIgnoreCase)) == true;
}

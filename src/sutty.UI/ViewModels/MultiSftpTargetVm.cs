using CommunityToolkit.Mvvm.ComponentModel;
using sutty.Core.Sftp;
using sutty.UI.Helpers;
using System;

namespace sutty.UI.ViewModels;

/// <summary>Compact per-server SFTP state shown in the Multi side panel.</summary>
public sealed class MultiSftpTargetVm : ObservableObject
{
    private MultiSftpTargetState _state;
    private double _progress;
    private string _relativePath = "";
    private string _error = "";

    public MultiSftpTargetVm(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    public string Id { get; }
    public string DisplayName { get; }

    public MultiSftpTargetState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(StateGlyph));
        }
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (!SetProperty(ref _progress, Math.Clamp(value, 0, 1))) return;
            OnPropertyChanged(nameof(ProgressText));
        }
    }

    public string ProgressText => $"{Progress:P0}";

    public string StateText => State switch
    {
        MultiSftpTargetState.Pending => Loc.T("대기", "Pending"),
        MultiSftpTargetState.Transferring => Loc.T("전송 중", "Transferring"),
        MultiSftpTargetState.Succeeded => Loc.T("성공", "Succeeded"),
        MultiSftpTargetState.Failed => Loc.T("실패", "Failed"),
        MultiSftpTargetState.Cancelled => Loc.T("취소됨", "Cancelled"),
        _ => "",
    };

    public string StateGlyph => State switch
    {
        MultiSftpTargetState.Succeeded => "\uE73E",
        MultiSftpTargetState.Failed => "\uEA39",
        MultiSftpTargetState.Cancelled => "\uE711",
        _ => "\uE898",
    };

    public string DetailText => State == MultiSftpTargetState.Failed
        ? _error
        : string.IsNullOrWhiteSpace(_relativePath)
            ? StateText
            : $"{StateText} · {_relativePath}";

    public void Update(MultiSftpTargetStatus status)
    {
        State = status.State;
        Progress = status.Fraction;
        _relativePath = status.TransferProgress?.RelativePath ?? "";
        _error = status.Error ?? "";
        OnPropertyChanged(nameof(DetailText));
    }

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(DetailText));
    }
}

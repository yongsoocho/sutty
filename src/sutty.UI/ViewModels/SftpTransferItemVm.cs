using CommunityToolkit.Mvvm.ComponentModel;
using sutty.UI.Helpers;
using System;
using System.Diagnostics;
using System.Threading;

namespace sutty.UI.ViewModels;

public enum SftpTransferDirection
{
    Upload,
    Download,
}

public enum SftpTransferState
{
    Queued,
    Running,
    Cancelling,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>Compact UI state for one upload or download.</summary>
public sealed class SftpTransferItemVm : ObservableObject, IDisposable
{
    private readonly Stopwatch _watch = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _token;
    private double _progress;
    private SftpTransferState _state = SftpTransferState.Queued;
    private string? _error;

    public SftpTransferItemVm(
        string name,
        string sourcePath,
        string destinationPath,
        long totalBytes,
        SftpTransferDirection direction)
    {
        Name = name;
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        TotalBytes = Math.Max(0, totalBytes);
        Direction = direction;
        _token = _cancellation.Token;
    }

    public string Name { get; }
    public string SourcePath { get; }
    public string DestinationPath { get; }
    public long TotalBytes { get; }
    public SftpTransferDirection Direction { get; }
    public CancellationToken Token => _token;
    public string DirectionGlyph => Direction == SftpTransferDirection.Upload ? "\uE898" : "\uE896";
    public string DirectionText => Direction == SftpTransferDirection.Upload
        ? Loc.T("업로드", "Upload")
        : Loc.T("다운로드", "Download");

    public double Progress
    {
        get => _progress;
        private set
        {
            if (!SetProperty(ref _progress, Math.Clamp(value, 0, 1))) return;
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(DetailText));
        }
    }

    public SftpTransferState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(IsActive));
        }
    }

    public bool CanCancel => State is SftpTransferState.Queued or SftpTransferState.Running;
    public bool IsActive => State is SftpTransferState.Queued or SftpTransferState.Running or SftpTransferState.Cancelling;
    public string ProgressText => $"{Progress:P0}";

    public string StateText => State switch
    {
        SftpTransferState.Queued => Loc.T("대기", "Queued"),
        SftpTransferState.Running => Loc.T("전송 중", "Transferring"),
        SftpTransferState.Cancelling => Loc.T("취소 중", "Cancelling"),
        SftpTransferState.Completed => Loc.T("완료", "Completed"),
        SftpTransferState.Cancelled => Loc.T("취소됨", "Cancelled"),
        SftpTransferState.Failed => Loc.T("실패", "Failed"),
        _ => "",
    };

    public string DetailText
    {
        get
        {
            if (State == SftpTransferState.Failed && !string.IsNullOrWhiteSpace(_error))
                return $"{DirectionText} · {_error}";
            if (State is SftpTransferState.Completed or SftpTransferState.Cancelled or SftpTransferState.Cancelling)
                return $"{DirectionText} · {StateText}";
            if (State != SftpTransferState.Running || _watch.Elapsed.TotalSeconds <= 0)
                return $"{DirectionText} · {StateText}";

            var transferred = TotalBytes * Progress;
            var bytesPerSecond = transferred / _watch.Elapsed.TotalSeconds;
            var speed = bytesPerSecond > 0 ? $"{FormatBytes(bytesPerSecond)}/s" : "—";
            var eta = bytesPerSecond > 0 && TotalBytes > transferred
                ? FormatEta(TimeSpan.FromSeconds((TotalBytes - transferred) / bytesPerSecond))
                : "—";
            return $"{DirectionText} · {speed} · ETA {eta}";
        }
    }

    public void Start()
    {
        if (State != SftpTransferState.Queued)
            return;
        State = SftpTransferState.Running;
        _watch.Restart();
    }

    public void Report(double progress)
    {
        if (State == SftpTransferState.Running && progress > Progress)
            Progress = progress;
    }

    public void Complete()
    {
        Progress = 1;
        _watch.Stop();
        State = SftpTransferState.Completed;
    }

    public void MarkCancelled()
    {
        _watch.Stop();
        State = SftpTransferState.Cancelled;
    }

    public void Fail(string message)
    {
        _watch.Stop();
        _error = message;
        State = SftpTransferState.Failed;
    }

    public void Cancel()
    {
        if (!CanCancel) return;
        State = SftpTransferState.Cancelling;
        _cancellation.Cancel();
    }

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(DirectionText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(DetailText));
    }

    public void Dispose() => _cancellation.Dispose();

    private static string FormatBytes(double bytes) => bytes switch
    {
        < 1024 => $"{bytes:0} B",
        < 1024 * 1024 => $"{bytes / 1024:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024 * 1024):0.#} MB",
        _ => $"{bytes / (1024 * 1024 * 1024):0.#} GB",
    };

    private static string FormatEta(TimeSpan eta) => eta.TotalHours >= 1
        ? eta.ToString(@"h\:mm\:ss")
        : eta.ToString(@"m\:ss");
}

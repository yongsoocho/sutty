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
    Pausing,
    Paused,
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
    private double _progress;
    private SftpTransferState _state = SftpTransferState.Queued;
    private string? _error;
    private bool _userCancellationRequested;

    public SftpTransferItemVm(
        string name,
        string sourcePath,
        string destinationPath,
        long totalBytes,
        SftpTransferDirection direction,
        string? queueJobId = null)
    {
        Name = name;
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        TotalBytes = Math.Max(0, totalBytes);
        Direction = direction;
        QueueJobId = queueJobId;
    }

    public string Name { get; }
    public string SourcePath { get; }
    public string DestinationPath { get; }
    public long TotalBytes { get; }
    public SftpTransferDirection Direction { get; }
    public string? QueueJobId { get; }
    public bool UserCancellationRequested => _userCancellationRequested;
    public bool PauseRequested { get; private set; }
    public CancellationToken Token => _cancellation.Token;
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
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(IsActive));
        }
    }

    public bool CanCancel => State is SftpTransferState.Queued or SftpTransferState.Running;
    public bool CanPause => State is SftpTransferState.Queued or SftpTransferState.Running;
    public bool CanResume => State == SftpTransferState.Paused;
    public bool IsActive => State is SftpTransferState.Queued or SftpTransferState.Running or
        SftpTransferState.Pausing or SftpTransferState.Cancelling;
    public string ProgressText => $"{Progress:P0}";

    public string StateText => State switch
    {
        SftpTransferState.Queued => Loc.T("대기", "Queued"),
        SftpTransferState.Running => Loc.T("전송 중", "Transferring"),
        SftpTransferState.Pausing => Loc.T("일시 정지 중", "Pausing"),
        SftpTransferState.Paused => Loc.T("일시 정지됨", "Paused"),
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
            if (State is SftpTransferState.Completed or SftpTransferState.Cancelled or
                SftpTransferState.Cancelling or SftpTransferState.Pausing or SftpTransferState.Paused)
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
        PauseRequested = false;
        Progress = 1;
        _watch.Stop();
        State = SftpTransferState.Completed;
    }

    public void MarkCancelled()
    {
        PauseRequested = false;
        _watch.Stop();
        State = SftpTransferState.Cancelled;
    }

    public void MarkPaused()
    {
        if (!PauseRequested)
            return;
        _watch.Stop();
        State = SftpTransferState.Paused;
    }

    public void Fail(string message)
    {
        PauseRequested = false;
        _watch.Stop();
        _error = message;
        State = SftpTransferState.Failed;
    }

    public void Cancel(bool userInitiated = false)
    {
        if (!CanCancel) return;
        PauseRequested = false;
        _userCancellationRequested |= userInitiated;
        State = SftpTransferState.Cancelling;
        _cancellation.Cancel();
    }

    public void Pause()
    {
        if (!CanPause)
            return;
        PauseRequested = true;
        State = SftpTransferState.Pausing;
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

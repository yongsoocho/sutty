using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using sutty.Core.Sftp;
using sutty.UI.Helpers;
using sutty.UI.Services;
using System;
using System.IO;
using System.Linq;
using CoreSftpTransferDirection = sutty.Core.Sftp.SftpTransferDirection;

namespace sutty.UI.ViewModels;

/// <summary>Mutable live projection of one durable transfer queue job.</summary>
public sealed class TransferCenterItemViewModel : ObservableObject
{
    private readonly TransferCenterService _service;
    private SftpQueuedJob _job;
    private bool _isBusy;

    public TransferCenterItemViewModel(
        SftpQueuedJob job,
        TransferCenterService service)
    {
        _job = job ?? throw new ArgumentNullException(nameof(job));
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public string Id => _job.Id;

    public SftpQueuedJob Job => _job;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value))
                return;
            RaiseCapabilitiesChanged();
        }
    }

    public string Name => ResolveName(_job);

    public string TargetText => BuildTargetText(_job);

    public string PathText => $"{_job.SourcePath}  →  {_job.DestinationPath}";

    public string ErrorText => _job.RequiresEditReview && _job.State is SftpQueueJobState.Failed or SftpQueueJobState.Interrupted or SftpQueueJobState.Paused
        ? Loc.T("파일 > 편집본에서 원격 변경을 확인한 뒤 다시 반영하세요. 로컬 편집본은 보관됩니다.",
            "Review remote changes in Files > Edits before uploading again. The local working copy is retained.")
        : _job.Targets
        .Select(target => target.Error)
        .FirstOrDefault(error => !string.IsNullOrWhiteSpace(error)) ?? "";

    public double ProgressPercent => SftpTransferQueueStore.GetProgressPercentage(_job);

    public string ProgressText
    {
        get
        {
            var total = _job.Targets.Sum(target => Math.Max(0, target.TotalBytes));
            return total > 0 || _job.State == SftpQueueJobState.Completed
                ? $"{ProgressPercent:0}%"
                : "—";
        }
    }

    public string UpdatedText => _job.UpdatedAtUtc.ToLocalTime().ToString("g");

    public string DirectionGlyph => _job.Direction == CoreSftpTransferDirection.Upload
        ? "\uE898"
        : "\uE896";

    public string StateText => _job.State switch
    {
        SftpQueueJobState.Pending => Loc.T("대기", "Pending"),
        SftpQueueJobState.Running => Loc.T("전송 중", "Running"),
        SftpQueueJobState.Paused => Loc.T("일시 정지됨", "Paused"),
        SftpQueueJobState.Interrupted => Loc.T("복구 필요", "Interrupted"),
        SftpQueueJobState.Failed => Loc.T("실패", "Failed"),
        SftpQueueJobState.Completed => Loc.T("완료", "Completed"),
        SftpQueueJobState.Cancelled => Loc.T("취소됨", "Cancelled"),
        _ => "",
    };

    public bool IsIndeterminate => _job.State == SftpQueueJobState.Running &&
        _job.Targets.Sum(target => Math.Max(0, target.TotalBytes)) == 0;

    public Visibility ProgressVisibility => _job.State == SftpQueueJobState.Cancelled &&
        _job.Targets.All(target => target.TotalBytes <= 0)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public Visibility ErrorVisibility => string.IsNullOrWhiteSpace(ErrorText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PauseVisibility => HasTarget(
        SftpQueueTargetState.Pending,
        SftpQueueTargetState.Running)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ResumeVisibility => HasTarget(
        SftpQueueTargetState.Paused,
        SftpQueueTargetState.Interrupted)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility RetryVisibility => HasTarget(SftpQueueTargetState.Failed)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CancelVisibility => HasTarget(
        SftpQueueTargetState.Pending,
        SftpQueueTargetState.Running,
        SftpQueueTargetState.Paused,
        SftpQueueTargetState.Interrupted)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility RemoveVisibility => _job.State == SftpQueueJobState.Completed
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool CanPause => !IsBusy &&
        _service.CanExecute(_job, TransferCenterAction.Pause);

    public bool CanResume => !IsBusy &&
        _service.CanExecute(_job, TransferCenterAction.Resume);

    public bool CanRetry => !IsBusy &&
        _service.CanExecute(_job, TransferCenterAction.RetryFailed);

    public bool CanCancel => !IsBusy &&
        _service.CanExecute(_job, TransferCenterAction.Cancel);

    public bool CanRemove => !IsBusy && _job.State == SftpQueueJobState.Completed;

    public string PauseAutomationName => Loc.T(
        $"{Name} 전송 일시 정지",
        $"Pause transfer {Name}");

    public string ResumeAutomationName => Loc.T(
        $"{Name} 전송 재개",
        $"Resume transfer {Name}");

    public string RetryAutomationName => Loc.T(
        $"{Name} 실패 대상 재시도",
        $"Retry failed targets for {Name}");

    public string CancelAutomationName => Loc.T(
        $"{Name} 전송 취소",
        $"Cancel transfer {Name}");

    public string RemoveAutomationName => Loc.T(
        $"{Name} 완료 기록 제거",
        $"Remove completed transfer {Name}");

    public void Update(SftpQueuedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!string.Equals(job.Id, Id, StringComparison.Ordinal))
            throw new ArgumentException("A transfer row cannot change its queue id.", nameof(job));

        _job = job;
        RaiseAllChanged();
    }

    public void RefreshCapabilities() => RaiseCapabilitiesChanged();

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(PauseAutomationName));
        OnPropertyChanged(nameof(ResumeAutomationName));
        OnPropertyChanged(nameof(RetryAutomationName));
        OnPropertyChanged(nameof(CancelAutomationName));
        OnPropertyChanged(nameof(RemoveAutomationName));
    }

    private bool HasTarget(params SftpQueueTargetState[] states) =>
        _job.Targets.Any(target => states.Contains(target.State));

    private void RaiseAllChanged()
    {
        OnPropertyChanged(nameof(Job));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(DirectionGlyph));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(ProgressVisibility));
        OnPropertyChanged(nameof(ErrorVisibility));
        RaiseCapabilitiesChanged();
    }

    private void RaiseCapabilitiesChanged()
    {
        OnPropertyChanged(nameof(BusyVisibility));
        OnPropertyChanged(nameof(PauseVisibility));
        OnPropertyChanged(nameof(ResumeVisibility));
        OnPropertyChanged(nameof(RetryVisibility));
        OnPropertyChanged(nameof(CancelVisibility));
        OnPropertyChanged(nameof(RemoveVisibility));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(PauseAutomationName));
        OnPropertyChanged(nameof(ResumeAutomationName));
        OnPropertyChanged(nameof(RetryAutomationName));
        OnPropertyChanged(nameof(CancelAutomationName));
        OnPropertyChanged(nameof(RemoveAutomationName));
    }

    private static string ResolveName(SftpQueuedJob job)
    {
        var path = job.Direction == CoreSftpTransferDirection.Upload
            ? job.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : job.SourcePath.TrimEnd('/');
        var name = job.Direction == CoreSftpTransferDirection.Upload
            ? Path.GetFileName(path)
            : RemotePath.GetName(path);
        return string.IsNullOrWhiteSpace(name)
            ? Loc.T("전송 작업", "Transfer job")
            : name;
    }

    private static string BuildTargetText(SftpQueuedJob job)
    {
        var names = job.Targets
            .Select(target => target.DisplayName.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var shown = names.Take(3).ToArray();
        var targetText = shown.Length == 0
            ? Loc.T("대상 정보 없음", "Target not recorded")
            : string.Join(", ", shown);
        if (names.Length > shown.Length)
        {
            targetText += Loc.T(
                $" 외 {names.Length - shown.Length}개",
                $" +{names.Length - shown.Length} more");
        }

        var operation = job.Direction == CoreSftpTransferDirection.Upload
            ? Loc.T("업로드", "Upload")
            : Loc.T("다운로드", "Download");
        var mode = job.Mode switch
        {
            SftpQueueMode.FanOut => Loc.T("여러 대상", "multiple targets"),
            SftpQueueMode.FanIn => Loc.T("여러 원본", "multiple sources"),
            _ => Loc.T("단일 전송", "single transfer"),
        };
        return $"{operation} · {mode} · {targetText}";
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Sftp;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace sutty.UI.Views;

/// <summary>
/// Read-only projection of the durable transfer queue. This surface deliberately never
/// claims, resumes, retries, cancels, deletes, or otherwise mutates a queued job.
/// </summary>
public sealed partial class TransferCenterPanel : UserControl
{
    private readonly SftpTransferQueueStore _queue = SftpTransferQueueStore.Default;
    private IReadOnlyList<SftpQueuedJob> _snapshot = [];

    public ObservableCollection<TransferCenterItemVm> Items { get; } = [];

    public TransferCenterPanel()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => ApplyFilter();
    }

    public void RefreshLanguage()
    {
        Bindings.Update();
        ApplyFilter();
    }

    public void RefreshFromStore()
    {
        _snapshot = _queue.GetAll()
            .OrderByDescending(job => job.UpdatedAtUtc)
            .ThenByDescending(job => job.CreatedAtUtc)
            .ToArray();
        SnapshotTimeText.Text = Loc.T(
            $"스냅샷 {DateTimeOffset.Now:g}",
            $"Snapshot {DateTimeOffset.Now:g}");
        ApplyFilter();
    }

    private void TransferCenterPanel_Loaded(object sender, RoutedEventArgs e) =>
        RefreshFromStore();

    private void Refresh_Click(object sender, RoutedEventArgs e) =>
        RefreshFromStore();

    private void StateFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Items is not null)
            ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (StateFilter is null || TransferList is null || EmptyState is null)
            return;

        var filter = StateFilter.SelectedIndex;
        var filtered = _snapshot
            .Where(job => filter switch
            {
                1 => job.State is SftpQueueJobState.Pending or
                    SftpQueueJobState.Running or
                    SftpQueueJobState.Paused or
                    SftpQueueJobState.Interrupted,
                2 => job.State == SftpQueueJobState.Failed,
                3 => job.State == SftpQueueJobState.Completed,
                4 => job.State == SftpQueueJobState.Cancelled,
                _ => true,
            })
            .Select(job => new TransferCenterItemVm(job))
            .ToArray();

        Items.Clear();
        foreach (var item in filtered)
            Items.Add(item);

        CountText.Text = filtered.Length == _snapshot.Count
            ? filtered.Length.ToString("N0")
            : $"{filtered.Length:N0} / {_snapshot.Count:N0}";
        EmptyStateText.Text = _snapshot.Count == 0
            ? Loc.T("저장된 전송 작업이 없습니다.", "There are no saved transfer jobs.")
            : Loc.T("선택한 상태에 해당하는 전송 작업이 없습니다.",
                "No transfer jobs match the selected state.");
        EmptyState.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TransferList.Visibility = filtered.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>Immutable, localized view of one queue snapshot row.</summary>
public sealed class TransferCenterItemVm
{
    private readonly SftpQueuedJob _job;

    public TransferCenterItemVm(SftpQueuedJob job)
    {
        _job = job;
        Name = ResolveName(job);
        TargetText = BuildTargetText(job);
        PathText = $"{job.SourcePath}  →  {job.DestinationPath}";
        ErrorText = job.Targets
            .Select(target => target.Error)
            .FirstOrDefault(error => !string.IsNullOrWhiteSpace(error)) ?? "";

        var total = job.Targets.Sum(target => Math.Max(0, target.TotalBytes));
        ProgressPercent = SftpTransferQueueStore.GetProgressPercentage(job);
        ProgressText = total > 0 || job.State == SftpQueueJobState.Completed
            ? $"{ProgressPercent:0}%"
            : "—";
    }

    public string Name { get; }
    public string TargetText { get; }
    public string PathText { get; }
    public string ErrorText { get; }
    public double ProgressPercent { get; }
    public string ProgressText { get; }
    public string UpdatedText => _job.UpdatedAtUtc.ToLocalTime().ToString("g");
    public string DirectionGlyph => _job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload
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

    private static string ResolveName(SftpQueuedJob job)
    {
        var path = job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload
            ? job.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : job.SourcePath.TrimEnd('/');
        var name = job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload
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
            targetText += Loc.T($" 외 {names.Length - shown.Length}개", $" +{names.Length - shown.Length} more");

        var operation = job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload
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

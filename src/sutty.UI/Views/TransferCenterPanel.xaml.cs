using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Sftp;
using sutty.UI.Helpers;
using sutty.UI.Services;
using sutty.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace sutty.UI.Views;

/// <summary>Live projection and control surface for the durable transfer queue.</summary>
public sealed partial class TransferCenterPanel : UserControl
{
    private readonly TransferCenterService _service = TransferCenterService.Default;
    private readonly Dictionary<string, TransferCenterItemViewModel> _itemsById =
        new(StringComparer.Ordinal);
    private IReadOnlyList<SftpQueuedJob> _snapshot = [];
    private bool _subscribed;

    public ObservableCollection<TransferCenterItemViewModel> Items { get; } = [];

    public TransferCenterPanel() => InitializeComponent();

    public void RefreshLanguage()
    {
        Bindings.Update();
        foreach (var item in _itemsById.Values)
            item.RefreshLanguage();
        LiveStatusText.Text = "";
        LiveStatusText.Visibility = Visibility.Collapsed;
        ApplyFilter();
    }

    public void RefreshFromStore() => _service.RefreshNow();

    private void TransferCenterPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _service.SnapshotChanged += Service_SnapshotChanged;
            _subscribed = true;
        }
        ApplySnapshot(_service.Snapshot);
    }

    private void TransferCenterPanel_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
            return;
        _service.SnapshotChanged -= Service_SnapshotChanged;
        _subscribed = false;
    }

    private void Service_SnapshotChanged(
        object? sender,
        TransferQueueSnapshotChangedEventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplySnapshot(args.Jobs);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_subscribed)
                ApplySnapshot(args.Jobs);
        });
    }

    private void ApplySnapshot(IReadOnlyList<SftpQueuedJob> jobs)
    {
        _snapshot = jobs
            .OrderByDescending(job => job.UpdatedAtUtc)
            .ThenByDescending(job => job.CreatedAtUtc)
            .ToArray();

        var liveIds = _snapshot.Select(job => job.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in _itemsById.Keys.Where(id => !liveIds.Contains(id)).ToArray())
            _itemsById.Remove(staleId);

        foreach (var job in _snapshot)
        {
            if (_itemsById.TryGetValue(job.Id, out var item))
                item.Update(job);
            else
                _itemsById.Add(job.Id, new TransferCenterItemViewModel(job, _service));
        }

        SnapshotTimeText.Text = Loc.T(
            $"업데이트 {DateTimeOffset.Now:g}",
            $"Updated {DateTimeOffset.Now:g}");
        ApplyFilter();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _service.RefreshNow();
        ShowStatus(Loc.T("전송 큐를 새로 고쳤습니다.", "Transfer queue refreshed."));
    }

    private void StateFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Items is not null)
            ApplyFilter();
    }

    private void DirectionFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Items is not null)
            ApplyFilter();
    }

    private void TargetFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Items is not null)
            ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (StateFilter is null || TransferList is null || EmptyState is null)
            return;

        var filter = StateFilter.SelectedIndex;
        var directionFilter = DirectionFilter?.SelectedIndex ?? 0;
        var targetFilter = TargetFilter?.Text.Trim() ?? "";
        var filtered = _snapshot
            .Where(job => filter switch
            {
                1 => job.State is SftpQueueJobState.Pending or SftpQueueJobState.Running,
                2 => job.State is SftpQueueJobState.Paused or SftpQueueJobState.Interrupted,
                3 => job.State == SftpQueueJobState.Failed,
                4 => job.State == SftpQueueJobState.Completed,
                5 => job.State == SftpQueueJobState.Cancelled,
                _ => true,
            })
            .Where(job => directionFilter switch
            {
                1 => job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload,
                2 => job.Direction == sutty.Core.Sftp.SftpTransferDirection.Download,
                _ => true,
            })
            .Where(job => targetFilter.Length == 0 || job.Targets.Any(target =>
                target.DisplayName.Contains(targetFilter, StringComparison.OrdinalIgnoreCase) ||
                target.Id.Contains(targetFilter, StringComparison.OrdinalIgnoreCase)))
            .Select(job => _itemsById[job.Id])
            .ToArray();

        ReconcileVisibleItems(filtered);
        CountText.Text = filtered.Length == _snapshot.Count
            ? filtered.Length.ToString("N0")
            : $"{filtered.Length:N0} / {_snapshot.Count:N0}";
        EmptyStateText.Text = _snapshot.Count == 0
            ? Loc.T("저장된 전송 작업이 없습니다.", "There are no saved transfer jobs.")
            : Loc.T(
                "선택한 상태에 해당하는 전송 작업이 없습니다.",
                "No transfer jobs match the selected state.");
        EmptyState.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TransferList.Visibility = filtered.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RemoveCompletedButton.IsEnabled = _snapshot.Any(job =>
            job.State == SftpQueueJobState.Completed);
    }

    private void ReconcileVisibleItems(IReadOnlyList<TransferCenterItemViewModel> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < Items.Count && ReferenceEquals(Items[index], desired[index]))
                continue;

            var existingIndex = Items.IndexOf(desired[index]);
            if (existingIndex >= 0)
                Items.Move(existingIndex, index);
            else
                Items.Insert(index, desired[index]);
        }

        while (Items.Count > desired.Count)
            Items.RemoveAt(Items.Count - 1);
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) =>
        await ExecuteActionAsync(GetItem(sender), TransferCenterAction.Pause);

    private async void Resume_Click(object sender, RoutedEventArgs e) =>
        await ExecuteActionAsync(GetItem(sender), TransferCenterAction.Resume);

    private async void RetryFailed_Click(object sender, RoutedEventArgs e) =>
        await ExecuteActionAsync(GetItem(sender), TransferCenterAction.RetryFailed);

    private async void Cancel_Click(object sender, RoutedEventArgs e) =>
        await ExecuteActionAsync(GetItem(sender), TransferCenterAction.Cancel);

    private async Task ExecuteActionAsync(
        TransferCenterItemViewModel? item,
        TransferCenterAction action)
    {
        if (item is null || item.IsBusy)
            return;

        item.IsBusy = true;
        ShowStatus(ActionStartedText(item.Name, action));
        try
        {
            var result = await _service.ExecuteAsync(item.Id, action);
            ShowStatus(ControlResultText(item.Name, action, result));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      ArgumentException or InvalidOperationException)
        {
            ShowStatus(Loc.T(
                $"전송 제어를 완료하지 못했습니다: {error.Message}",
                $"Could not complete the transfer control: {error.Message}"));
        }
        finally
        {
            item.IsBusy = false;
            item.RefreshCapabilities();
            _service.RefreshNow();
        }
    }

    private void RemoveCompleted_Click(object sender, RoutedEventArgs e)
    {
        var item = GetItem(sender);
        if (item is null || item.IsBusy || !item.CanRemove)
            return;

        item.IsBusy = true;
        try
        {
            var removed = _service.RemoveCompleted(item.Id);
            ShowStatus(removed
                ? Loc.T("완료된 전송 기록을 제거했습니다.", "Completed transfer removed.")
                : Loc.T("전송 상태가 변경되어 제거하지 않았습니다.",
                    "The transfer changed state and was not removed."));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      ArgumentException or InvalidOperationException)
        {
            ShowStatus(Loc.T(
                $"완료 기록을 제거하지 못했습니다: {error.Message}",
                $"Could not remove the completed transfer: {error.Message}"));
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private void RemoveAllCompleted_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var removed = _service.RemoveAllCompleted();
            ShowStatus(removed > 0
                ? Loc.T(
                    $"완료된 전송 기록 {removed:N0}개를 제거했습니다.",
                    $"Removed {removed:N0} completed transfer(s).")
                : Loc.T("제거할 완료 기록이 없습니다.", "There are no completed transfers to remove."));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      InvalidOperationException)
        {
            ShowStatus(Loc.T(
                $"완료 기록을 정리하지 못했습니다: {error.Message}",
                $"Could not clear completed transfers: {error.Message}"));
        }
    }

    private static TransferCenterItemViewModel? GetItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as TransferCenterItemViewModel;

    private void ShowStatus(string text)
    {
        LiveStatusText.Text = text;
        LiveStatusText.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static string ActionStartedText(string name, TransferCenterAction action) =>
        action switch
        {
            TransferCenterAction.Pause => Loc.T(
                $"{name} 일시 정지를 요청하는 중…",
                $"Requesting pause for {name}…"),
            TransferCenterAction.Resume => Loc.T(
                $"{name} 재개를 요청하는 중…",
                $"Requesting resume for {name}…"),
            TransferCenterAction.RetryFailed => Loc.T(
                $"{name} 실패 대상 재시도를 요청하는 중…",
                $"Requesting failed-target retry for {name}…"),
            TransferCenterAction.Cancel => Loc.T(
                $"{name} 취소를 요청하는 중…",
                $"Requesting cancellation for {name}…"),
            _ => "",
        };

    private static string ControlResultText(
        string name,
        TransferCenterAction action,
        TransferCenterControlResult result)
    {
        if (result.Status == TransferCenterControlStatus.Accepted)
        {
            return action switch
            {
                TransferCenterAction.Pause => Loc.T(
                    $"{name} 일시 정지 요청을 전달했습니다.",
                    $"Pause requested for {name}."),
                TransferCenterAction.Resume => Loc.T(
                    $"{name} 재개를 시작했습니다.",
                    $"Resume started for {name}."),
                TransferCenterAction.RetryFailed => Loc.T(
                    $"{name}의 실패 대상 {result.AcceptedTargetCount:N0}개를 재시도합니다.",
                    $"Retrying {result.AcceptedTargetCount:N0} failed target(s) for {name}."),
                TransferCenterAction.Cancel => Loc.T(
                    $"{name} 취소 요청을 전달했습니다.",
                    $"Cancellation requested for {name}."),
                _ => "",
            };
        }

        return result.Status switch
        {
            TransferCenterControlStatus.PartiallyAccepted => Loc.T(
                $"대상 {result.EligibleTargetCount:N0}개 중 {result.AcceptedTargetCount:N0}개가 요청을 수락했습니다.",
                $"{result.AcceptedTargetCount:N0} of {result.EligibleTargetCount:N0} target(s) accepted the request."),
            TransferCenterControlStatus.ExecutorUnavailable => Loc.T(
                "일치하는 연결된 Files 세션이 없습니다.",
                "No matching connected Files session is available."),
            TransferCenterControlStatus.NoEligibleTargets => Loc.T(
                "전송 상태가 이미 변경되어 요청할 대상이 없습니다.",
                "The transfer state already changed; there are no eligible targets."),
            TransferCenterControlStatus.NotFound => Loc.T(
                "해당 전송 기록이 더 이상 없습니다.",
                "That transfer record no longer exists."),
            TransferCenterControlStatus.Busy => Loc.T(
                "해당 전송의 다른 제어 요청이 진행 중입니다.",
                "Another control request is already in progress for this transfer."),
            _ when !string.IsNullOrWhiteSpace(result.Message) => result.Message!,
            _ => Loc.T(
                "연결된 세션이 요청을 수락하지 않았습니다.",
                "The connected session did not accept the request."),
        };
    }
}

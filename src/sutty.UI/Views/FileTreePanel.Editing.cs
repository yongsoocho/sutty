using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Sftp;
using sutty.Setting;
using sutty.UI.Helpers;
using sutty.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreDirection = sutty.Core.Sftp.SftpTransferDirection;
using UiDirection = sutty.UI.ViewModels.SftpTransferDirection;

namespace sutty.UI.Views;

public sealed partial class FileTreePanel
{
    private readonly List<EditItem> _remoteEdits = [];
    private DispatcherQueueTimer? _editTimer;
    private ComboBox _editSelector = null!;
    private TextBlock _editDetails = null!;
    private Button _applyEdit = null!;
    private Button _reloadEdit = null!;
    private Button _openEdit = null!;
    private Button _finishEdit = null!;
    private Button _recoveryEdit = null!;
    private CheckBox _autoEdit = null!;
    private bool _scanningEdits;
    private bool _updatingEditUi;
    private bool _editsStopped;
    private bool _editsSuspended;
    private readonly CancellationTokenSource _editLifetime = new();
    private EditItem? SelectedEdit => _editSelector?.SelectedItem as EditItem;

    private void InitializeRemoteEditing()
    {
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(12) };
        _editSelector = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = nameof(EditItem.Title) };
        AutomationProperties.SetName(_editSelector, Loc.T("원격 편집본", "Remote working copies"));
        _editSelector.SelectionChanged += (_, _) => UpdateEditUi();
        panel.Children.Add(_editSelector);
        _editDetails = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        panel.Children.Add(_editDetails);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _applyEdit = EditButton(Loc.T("서버에 반영", "Upload changes"), async () =>
        {
            if (SelectedEdit is { } item) await ApplyEditAsync(item, automatic: false);
        });
        _openEdit = EditButton(Loc.T("편집기 열기", "Open editor"), () =>
        {
            if (SelectedEdit is { } item) LaunchEditor(item);
            return Task.CompletedTask;
        });
        _reloadEdit = EditButton(Loc.T("다시 내려받기", "Download again"), async () =>
        {
            if (SelectedEdit is { } item) await ReloadEditAsync(item);
        });
        _finishEdit = EditButton(Loc.T("편집 종료", "Finish editing"), async () =>
        {
            if (SelectedEdit is { } item) await FinishEditAsync(item);
        });
        actions.Children.Add(_applyEdit);
        actions.Children.Add(_openEdit);
        actions.Children.Add(_reloadEdit);
        _recoveryEdit = EditButton(Loc.T("보관 폴더", "Recovery folder"), () =>
        {
            OpenRecoveryFolder(SelectedEdit?.Edit.WorkingDirectory ?? RemoteEditSession.DefaultStorageRoot);
            return Task.CompletedTask;
        });
        actions.Children.Add(_recoveryEdit);
        actions.Children.Add(_finishEdit);
        panel.Children.Add(new ScrollViewer { Content = actions, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _autoEdit = new CheckBox { Content = Loc.T("이번 파일만 저장 시 자동 반영", "Automatically upload saves for this file only") };
        _autoEdit.Checked += (_, _) => { if (!_updatingEditUi && SelectedEdit is { } item) item.AutoUpload = true; };
        _autoEdit.Unchecked += (_, _) => { if (!_updatingEditUi && SelectedEdit is { } item) item.AutoUpload = false; };
        panel.Children.Add(_autoEdit);
        RemoteEditsHost.Content = panel;
        _editTimer = DispatcherQueue.CreateTimer();
        _editTimer.Interval = TimeSpan.FromSeconds(2);
        _editTimer.Tick += EditTimer_Tick;
    }

    private Button EditButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text, FontSize = 12 };
        AutomationProperties.SetName(button, text);
        button.Click += async (_, _) =>
        {
            var requestedItem = SelectedEdit;
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (requestedItem is { } item)
                    SetEditFailure(item, Loc.T("작업에 실패했습니다. 로컬 편집본을 보관합니다.", "Operation failed. The local working copy is retained.") + $" ({error.GetType().Name})");
                UpdateEditUi();
            }
        };
        return button;
    }

    private async void EditRemoteFile_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { } node || !IsNodeCurrent(node) || _sftp is null || _session is null || _editsStopped || _editsSuspended) return;
        var existing = _remoteEdits.FirstOrDefault(item => item.Edit.RemoteFilePath == node.FullPath && item.Version == _sessionVersion);
        if (existing is not null)
        {
            _editSelector.SelectedItem = existing;
            RemoteEditsHost.Visibility = Visibility.Visible;
            if (existing.Ready && !existing.Busy)
            {
                try { LaunchEditor(existing); }
                catch (Exception error)
                {
                    SetEditFailure(existing, Loc.T("편집기를 열 수 없습니다. 설정에서 실행 파일을 확인하세요.",
                        "Could not open the editor. Check its executable in Settings.") + $" ({error.GetType().Name})");
                    UpdateEditUi();
                }
            }
            return;
        }
        if (_remoteEdits.Count >= 8)
        {
            ShowStatus(Loc.T("편집본은 한 세션에서 8개까지 열 수 있습니다. 완료한 편집을 종료하세요.", "Finish an edit before opening more than 8 working copies in this session."));
            return;
        }
        EditItem? item = null;
        try
        {
            RemoteEditSession.ValidateEntry(node.Entry);
            var identity = $"{_session.Info.Username}@{_session.Info.Host}:{_session.Info.Port}";
            var edit = new RemoteEditSession(identity, node.FullPath);
            item = new EditItem(edit, _sftp, _sessionVersion, _session.Info.Environment) { Busy = true };
            _remoteEdits.Add(item);
            RefreshEditSelection(item);
            RemoteEditsHost.Visibility = Visibility.Visible;
            await DownloadEditCopyAsync(item);
            LaunchEditor(item);
        }
        catch (OperationCanceledException) { if (item is not null) SetEditFailure(item, Loc.T("중단됨 · 로컬 파일 보관", "Interrupted · local files retained")); }
        catch (Exception error)
        {
            var message = Loc.T("텍스트 파일(8 MiB 이하)을 선택하고 편집기 설정·SFTP 권한을 확인하세요. 다운로드된 편집본은 보관됩니다.",
                "Choose a text file up to 8 MiB and check editor settings and SFTP permissions. Downloaded copies are retained.") + $" ({error.GetType().Name})";
            if (item is not null) SetEditFailure(item, message);
            else ShowStatus(message);
        }
        finally
        {
            if (item is not null) item.Busy = false;
            if (_remoteEdits.Count > 0 && !_editsStopped) _editTimer?.Start();
            UpdateEditUi();
        }
    }

    private void ShowRemoteEdits_Click(object sender, RoutedEventArgs e)
    {
        RemoteEditsHost.Visibility = RemoteEditsHost.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        UpdateEditUi();
    }

    private bool EditConnected(EditItem item) => !_editsStopped && item.Version == _sessionVersion && ReferenceEquals(item.Sftp, _sftp);

    private async Task DownloadEditCopyAsync(EditItem item)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_editLifetime.Token, _sessionCts.Token);
        var ct = cts.Token;
        if (!EditConnected(item)) throw new OperationCanceledException(ct);
        item.AutoUpload = false;
        item.FailureDetail = null;
        // Reload into a separate session and directory. Until verification succeeds, an
        // existing editor still refers to its original path and uploaded baseline.
        var candidate = item.Ready ? new RemoteEditSession(item.Edit.HostIdentity, item.Edit.RemoteFilePath,
            Path.GetDirectoryName(item.Edit.WorkingDirectory)) : item.Edit;
        var before = await RemoteEditSession.ReadRemoteStampAsync(item.Sftp, item.Edit.RemoteFilePath, ct)
            ?? throw new IOException("The remote file no longer exists.");
        item.Status = Loc.T("편집본 내려받는 중…", "Downloading a working copy…");
        UpdateEditUi();
        var local = candidate.AllocateWorkingCopy();
        await RunQueuedEditTransferAsync(item, CoreDirection.Download, item.Edit.RemoteFilePath, local, before.Size,
            SftpConflictPolicy.Ask, null, ct);
        var after = await RemoteEditSession.ReadRemoteStampAsync(item.Sftp, item.Edit.RemoteFilePath, ct);
        await candidate.AcceptDownloadAsync(before, after, ct);
        ct.ThrowIfCancellationRequested();
        if (!EditConnected(item)) throw new OperationCanceledException(ct);
        item.Edit = candidate;
        item.Ready = true;
        item.Dirty = false;
        item.AutoUpload = false;
        item.LastObservedHash = item.Edit.UploadedHash;
        item.Status = item.Edit.NeedsReview
            ? Loc.T("다운로드 중 원격 변경 감지 · 반영 전 확인 필요", "Remote change detected during download · review before upload")
            : Loc.T("편집기에서 저장하면 변경을 감지합니다. 로컬 편집본이 보관됩니다.", "Save in your editor to detect changes. Working copies are kept locally.");
        AppendRecoveryNoteWarning(item);
    }

    private void LaunchEditor(EditItem item)
    {
        if (!item.Ready || _editsStopped) return;
        var settings = SettingsService.Current;
        using var process = Process.Start(ExternalEditorCommand.Create(settings.ExternalEditorExecutable, settings.ExternalEditorArguments, item.Edit.LocalFilePath));
    }

    private async void EditTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_scanningEdits || _editsStopped || _editsSuspended) return;
        _scanningEdits = true;
        try
        {
            foreach (var item in _remoteEdits.ToArray())
            {
                if (item.Busy || !item.Ready || _editsStopped || _editsSuspended) continue;
                try
                {
                    var workingCopy = item.Edit;
                    var hash = await workingCopy.ReadLocalHashAsync(_editLifetime.Token);
                    if (_editsStopped || _editsSuspended || item.Busy || !_remoteEdits.Contains(item) || !ReferenceEquals(workingCopy, item.Edit)) continue;
                    var stable = string.Equals(hash, item.LastObservedHash, StringComparison.Ordinal);
                    item.LastObservedHash = hash;
                    item.Dirty = !string.Equals(hash, item.Edit.UploadedHash, StringComparison.Ordinal);
                    if (!EditConnected(item))
                    {
                        item.AutoUpload = false;
                        item.Edit.RequireReview();
                        SetEditFailure(item, Loc.T("연결 종료 · 편집본 보관. 재연결 후 원격 파일을 확인하고 다시 업로드하세요.", "Disconnected · copy retained. Review the remote file after reconnect before uploading."));
                    }
                    else if (item.Dirty && stable && item.AutoUpload)
                        await ApplyEditAsync(item, automatic: true);
                    else if (item.Dirty)
                        item.Status = Loc.T("로컬 변경 있음 · 서버에 반영하지 않음", "Local changes · not uploaded");
                }
                catch (OperationCanceledException) { }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    item.Dirty = true;
                    item.Status = Loc.T("편집기가 저장 중이거나 파일을 읽을 수 없습니다. 다음 검사에서 다시 확인합니다.", "The editor is saving or the file cannot be read. Sutty will check again.");
                }
            }
        }
        finally { _scanningEdits = false; UpdateEditUi(); }
    }

    private async Task ApplyEditAsync(EditItem item, bool automatic)
    {
        if (item.Busy || !item.Ready || !EditConnected(item) || _editsSuspended) return;
        item.Busy = true;
        item.FailureDetail = null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_editLifetime.Token, _sessionCts.Token);
        var ct = cts.Token;
        try
        {
            var current = await RemoteEditSession.ReadRemoteStampAsync(item.Sftp, item.Edit.RemoteFilePath, ct);
            var conflict = item.Edit.HasRemoteConflict(current);
            var destination = item.Edit.RemoteFilePath;
            if (automatic && conflict)
            {
                item.AutoUpload = false;
                SetEditFailure(item, Loc.T("원격 변경 또는 확인 불가 · 자동 반영 중지. 서버에 반영 버튼으로 확인하세요.", "Remote change or unknown version · automatic upload stopped. Review with Upload changes."));
                return;
            }
            if (!automatic)
            {
                var name = new TextBox { Header = Loc.T("다른 이름으로 저장할 절대 경로 (선택)", "Absolute path for Save as (optional)"), PlaceholderText = destination };
                var body = new StackPanel { Spacing = 12 };
                body.Children.Add(new TextBlock { Text = $"{item.Environment} · {item.Edit.HostIdentity}\n{destination}\n\n" + (conflict
                    ? Loc.T("원격 파일이 변경되었거나 확인할 수 없습니다. 덮어쓰기 전에 검토하세요.", "The remote file changed or cannot be compared. Review before overwriting.")
                    : Loc.T("이 로컬 편집본을 서버에 반영합니다.", "Upload this local working copy to the server.")) + "\n" + Loc.T("크기·수정시각 비교는 동시 수정을 완전히 막지 못합니다.", "Size/time comparison cannot prevent every concurrent edit."), TextWrapping = TextWrapping.Wrap });
                body.Children.Add(name);
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = Loc.T("서버 파일 반영", "Upload edited file"), Content = body,
                    PrimaryButtonText = conflict ? Loc.T("덮어쓰기 / 다른 이름", "Overwrite / Save as") : Loc.T("반영", "Upload"),
                    SecondaryButtonText = Loc.T("다시 내려받기", "Download again"), CloseButtonText = Loc.T("취소", "Cancel"), DefaultButton = ContentDialogButton.Close };
                var choice = await dialog.ShowAsync();
                ct.ThrowIfCancellationRequested();
                if (choice == ContentDialogResult.Secondary)
                {
                    await DownloadEditCopyAsync(item); // Allocates a new path; the previous copy remains recoverable.
                    LaunchEditor(item);
                    return;
                }
                if (choice != ContentDialogResult.Primary) return;
                if (!string.IsNullOrWhiteSpace(name.Text))
                {
                    if (!name.Text.StartsWith('/') || name.Text.Any(char.IsControl) || RemotePath.Normalize(name.Text) == "/")
                        throw new ArgumentException("An absolute file path is required.");
                    destination = RemotePath.Normalize(name.Text);
                }
            }
            if (!EditConnected(item) || _editsSuspended) return;
            var saveAs = !string.Equals(destination, item.Edit.RemoteFilePath, StringComparison.Ordinal);
            var upload = await item.Edit.CreateUploadAsync(ct);
            item.Status = Loc.T("서버에 반영 중…", "Uploading changes…");
            UpdateEditUi();
            await RunQueuedEditTransferAsync(item, CoreDirection.Upload, upload.LocalPath, destination, upload.Size,
                saveAs ? SftpConflictPolicy.Ask : SftpConflictPolicy.Overwrite,
                async token =>
                {
                    var latest = await RemoteEditSession.ReadRemoteStampAsync(item.Sftp, destination, token);
                    if (saveAs ? latest is not null : current is null ? latest is not null :
                        latest is null || (current.CanCompare && latest.CanCompare && !current.Matches(latest)))
                        throw new IOException("The remote file changed after confirmation. Review again.");
                }, ct);
            // Once promotion succeeds, do not report the local content as unuploaded even if the follow-up stat fails.
            RemoteEditStamp? after = null;
            if (!ct.IsCancellationRequested)
            {
                try { after = await RemoteEditSession.ReadRemoteStampAsync(item.Sftp, destination, ct); }
                catch (Exception) { } // Promotion already succeeded; missing metadata requires review, not retransmission.
            }
            await item.Edit.AcceptUploadAsync(upload, destination, after, CancellationToken.None);
            item.Dirty = await item.Edit.HasChangesAsync(CancellationToken.None);
            item.Status = item.Dirty ? Loc.T("이전 저장본 반영 완료 · 새 로컬 변경은 미반영", "Previous save uploaded · newer local changes remain")
                : Loc.T("서버에 반영 완료", "Changes uploaded");
            if (item.Edit.NeedsReview)
            {
                item.AutoUpload = false;
                item.Status += Loc.T(" · 원격 버전 확인 불가: 다음 반영 전 확인 필요", " · Remote version unavailable: review before the next upload");
            }
            AppendRecoveryNoteWarning(item);
        }
        catch (OperationCanceledException)
        {
            item.AutoUpload = false;
            item.Edit.RequireReview();
            SetEditFailure(item, Loc.T("반영 중단 · 로컬 편집본 보관", "Upload interrupted · working copy retained"));
        }
        catch (Exception error)
        {
            item.AutoUpload = false;
            item.Edit.RequireReview();
            SetEditFailure(item, Loc.T("반영 실패 · 로컬 편집본을 보관합니다. 원격 상태를 확인하고 다시 반영하세요.", "Upload failed · working copy retained. Review the remote file and upload again.") + $" ({error.GetType().Name})");
        }
        finally { item.Busy = false; UpdateEditUi(); }
    }

    private async Task ReloadEditAsync(EditItem item)
    {
        if (item.Busy || !EditConnected(item) || _editsSuspended) return;
        item.Busy = true;
        item.AutoUpload = false;
        UpdateEditUi();
        try
        {
            var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = Loc.T("새 편집본 내려받기", "Download a fresh working copy"),
                Content = Loc.T("현재 편집기에서 저장을 마치세요. 기존 로컬 편집본은 보관하고 새 경로에 내려받습니다.", "Finish saving in your editor. The existing local copy is retained and a fresh copy is downloaded to a new path."),
                PrimaryButtonText = Loc.T("내려받기", "Download"), CloseButtonText = Loc.T("취소", "Cancel"), DefaultButton = ContentDialogButton.Close };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            if (_editsSuspended || !EditConnected(item)) return;
            await DownloadEditCopyAsync(item);
            LaunchEditor(item);
        }
        finally { item.Busy = false; UpdateEditUi(); }
    }

    private async Task FinishEditAsync(EditItem item)
    {
        if (item.Busy || _editsStopped || _editsSuspended) return;
        item.Busy = true;
        item.AutoUpload = false;
        UpdateEditUi();
        try
        {
            var changed = await item.Edit.HasChangesAsync(_editLifetime.Token);
            var confirm = new ContentDialog { XamlRoot = XamlRoot, Title = Loc.T("편집 감지 종료", "Stop watching this edit"),
                Content = (changed ? Loc.T("서버에 반영하지 않은 변경이 있을 수 있습니다.\n", "There may be changes that have not been uploaded.\n") : "") +
                    Loc.T("편집기를 닫고 저장을 마쳤는지 확인하세요. 로컬 파일은 아래에 보관하며 직접 삭제할 수 있습니다.\n", "Finish saving and close the editor. Local files are retained below; you can remove them yourself.\n") + item.Edit.WorkingDirectory,
                PrimaryButtonText = Loc.T("보관하고 종료", "Keep copy and finish"), CloseButtonText = Loc.T("취소", "Cancel"), DefaultButton = ContentDialogButton.Close };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            _remoteEdits.Remove(item);
            RefreshEditSelection(_remoteEdits.FirstOrDefault());
            if (_remoteEdits.Count == 0) _editTimer?.Stop();
        }
        finally { item.Busy = false; UpdateEditUi(); }
    }

    public void SuspendAutomaticEdits(bool suspended) => _editsSuspended = suspended;

    public async Task<bool> HasPendingEditsAsync()
    {
        foreach (var item in _remoteEdits.ToArray())
            if (item.Busy || await item.Edit.HasChangesAsync()) return true;
        return false;
    }

    private static void AppendRecoveryNoteWarning(EditItem item)
    {
        if (item.Edit.RecoveryNoteError is { } error)
            item.Status += Loc.T(" · 복구 메모 저장 실패. 표시된 로컬 경로를 보관하세요.",
                " · Could not save recovery metadata. Keep the displayed local path.") + $" ({error})";
    }

    private static void SetEditFailure(EditItem item, string message)
    {
        item.Status = message;
        item.FailureDetail = message;
    }

    public bool HasOpenEdits => _remoteEdits.Count > 0;
    public string EditRecoveryLocation => RemoteEditSession.DefaultStorageRoot;

    public void StopRemoteEditing()
    {
        if (_editsStopped) return;
        _editsStopped = true;
        _editTimer?.Stop();
        if (_editTimer is not null) _editTimer.Tick -= EditTimer_Tick;
        _editLifetime.Cancel();
        _editLifetime.Dispose();
        foreach (var item in _remoteEdits) item.AutoUpload = false;
        // External editors belong to the user; their copies and processes remain available.
    }

    private void RefreshEditSelection(EditItem? selected)
    {
        _editSelector.ItemsSource = _remoteEdits.ToArray();
        _editSelector.SelectedItem = selected;
        UpdateEditUi();
    }

    private void RefreshRemoteEditingLanguage()
    {
        if (_editSelector is null) return;
        foreach (var (button, text) in new[]
        {
            (_applyEdit, Loc.T("서버에 반영", "Upload changes")),
            (_openEdit, Loc.T("편집기 열기", "Open editor")),
            (_reloadEdit, Loc.T("다시 내려받기", "Download again")),
            (_finishEdit, Loc.T("편집 종료", "Finish editing")),
            (_recoveryEdit, Loc.T("보관 폴더", "Recovery folder")),
        })
        {
            button.Content = text;
            AutomationProperties.SetName(button, text);
        }
        _autoEdit.Content = Loc.T("이번 파일만 저장 시 자동 반영", "Automatically upload saves for this file only");
        AutomationProperties.SetName(_editSelector, Loc.T("원격 편집본", "Remote working copies"));
        UpdateEditUi();
    }

    private void UpdateEditUi()
    {
        if (_editSelector is null) return;
        _updatingEditUi = true;
        try
        {
            var item = SelectedEdit;
            _editDetails.Text = item is null ? Loc.T("원격 파일 메뉴에서 외부 편집기로 열기를 선택하세요. 이전 편집본은 보관 폴더에서 복구할 수 있습니다.",
                "Choose Edit with external editor on a remote file. Earlier copies are available in the recovery folder.")
                : $"{item.Environment} · {item.Edit.HostIdentity}\n{item.Edit.RemoteFilePath}\n{item.Status}\n{item.Edit.LocalFilePath}";
            if (item?.FailureDetail is { } failure && !string.Equals(failure, item.Status, StringComparison.Ordinal))
                _editDetails.Text += "\n" + failure;
            _applyEdit.IsEnabled = item is { Ready: true, Busy: false } && EditConnected(item);
            _reloadEdit.IsEnabled = item is { Busy: false } && EditConnected(item);
            _openEdit.IsEnabled = item is { Ready: true, Busy: false };
            _finishEdit.IsEnabled = item is { Busy: false };
            _autoEdit.IsEnabled = item is { Ready: true, Busy: false } && EditConnected(item);
            _autoEdit.IsChecked = item?.AutoUpload ?? false;
            RemoteEditsButton.Content = Loc.T($"편집본 ({_remoteEdits.Count})", $"Edits ({_remoteEdits.Count})");
        }
        finally { _updatingEditUi = false; }
    }

    private static void OpenRecoveryFolder(string path)
    {
        Directory.CreateDirectory(path);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
        start.ArgumentList.Add(Path.GetFullPath(path));
        using var process = Process.Start(start);
    }

    /// <summary>Uses the same durable queue, execution lease, worker gate and SFTP stage/verify/promote engine.</summary>
    private async Task<SftpTransferResult> RunQueuedEditTransferAsync(EditItem item, CoreDirection direction, string source, string destination,
        long size, SftpConflictPolicy policy, Func<CancellationToken, Task>? beforeTransfer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!EditConnected(item) || _editsSuspended) throw new OperationCanceledException(ct);
        var options = CreateTransferOptions(policy) with { Resume = false, RetryEnabled = false, VerifyChecksum = true };
        var name = RemotePath.GetName(item.Edit.RemoteFilePath);
        var job = CreateSingleQueuedJob(direction, source, destination, name, size, options) with { RequiresEditReview = true };
        _transferQueue.Upsert(job);
        if (!_transferQueue.TryAcquireTargetLease(job.Id, job.Targets[0].Id, _targetLeaseOwnerToken, out var lease))
            throw new IOException("The transfer is already running.");
        SftpTransferItemVm? transfer = null;
        var acquired = false;
        try
        {
            transfer = TryAddTransfer(name, source, destination, size, direction == CoreDirection.Upload ? UiDirection.Upload : UiDirection.Download, job.Id)
                ?? throw new IOException("The transfer queue is full.");
            transfer.AllowsPause = false;
            _liveWorkerJobIds.Add(job.Id);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, transfer.Token);
            var token = linked.Token;
            await _transferWorkerGate.WaitAsync(token);
            acquired = true;
            token.ThrowIfCancellationRequested();
            if (!EditConnected(item) || _editsSuspended) throw new OperationCanceledException(token);
            if (beforeTransfer is not null) await beforeTransfer(token);
            token.ThrowIfCancellationRequested();
            if (!EditConnected(item) || _editsSuspended) throw new OperationCanceledException(token);
            transfer.Start();
            UpdateQueuedTransfer(job, SftpQueueTargetState.Running);
            var durable = new DurableProgressState();
            var progress = CreateQueuedTransferProgress(job, transfer, durable);
            var result = direction == CoreDirection.Upload
                ? await item.Sftp.UploadPathAsync(source, destination, options, progress, token)
                : await item.Sftp.DownloadPathAsync(source, destination, options, progress, token);
            if (result.FilesSkipped > 0 || result.FilesTransferred != 1)
                throw new IOException("The edited file was not transferred.");
            transfer.Complete();
            UpdateQueuedTransfer(job, SftpQueueTargetState.Succeeded, bytesTransferred: durable.BytesTransferred, totalBytes: durable.TotalBytes);
            return result;
        }
        catch (OperationCanceledException)
        {
            transfer?.MarkCancelled();
            UpdateQueuedTransfer(job, SftpQueueTargetState.Interrupted, Loc.T("편집본에서 다시 확인하세요.", "Review in Edits before retrying."));
            throw;
        }
        catch (Exception error)
        {
            var message = Loc.T("편집본에서 원격 파일을 확인하고 다시 반영하세요.", "Review the remote file in Edits and upload again.") + $" ({error.GetType().Name})";
            transfer?.Fail(message);
            UpdateQueuedTransfer(job, SftpQueueTargetState.Failed, message);
            throw;
        }
        finally
        {
            _liveWorkerJobIds.Remove(job.Id);
            lease!.Dispose();
            if (acquired) _transferWorkerGate.Release();
            transfer?.Dispose();
            RefreshRestoredTransfers();
        }
    }

    private sealed class EditItem(RemoteEditSession edit, ISftpService sftp, int version, string environment)
    {
        public RemoteEditSession Edit { get; set; } = edit;
        public ISftpService Sftp { get; } = sftp;
        public int Version { get; } = version;
        public string Environment { get; } = environment;
        public string Title => RemotePath.GetName(Edit.RemoteFilePath);
        public string Status { get; set; } = "";
        public string? FailureDetail { get; set; }
        public bool Busy { get; set; }
        public bool Ready { get; set; }
        public bool Dirty { get; set; }
        public bool AutoUpload { get; set; }
        public string? LastObservedHash { get; set; }
    }
}

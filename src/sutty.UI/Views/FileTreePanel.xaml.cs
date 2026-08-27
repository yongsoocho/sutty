using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using sutty.Core.Models;
using sutty.Core.Sessions;
using sutty.Core.Sftp;
using sutty.Setting;
using sutty.UI.Helpers;
using sutty.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using UiSftpTransferDirection = sutty.UI.ViewModels.SftpTransferDirection;

namespace sutty.UI.Views;

/// <summary>Remote SFTP browser and compact transfer surface for the active SSH session.</summary>
public sealed partial class FileTreePanel : UserControl
{
    public IntPtr OwnerWindowHandle { get; set; }
    public event EventHandler<string>? OpenTerminalHereRequested;

    public ObservableCollection<FileNode> RootNodes { get; } = [];
    public ObservableCollection<SftpTransferItemVm> Transfers { get; } = [];

    private ISftpService? _sftp;
    private ISshSession? _session;
    private CancellationTokenSource _sessionCts = new();
    private CancellationTokenSource? _navigationCts;
    private readonly SemaphoreSlim _transferWorkerGate = new(1, 1);
    private readonly SftpTransferQueueStore _transferQueue = SftpTransferQueueStore.Default;
    private readonly string _targetLeaseOwnerToken = Guid.NewGuid().ToString("N");
    private int _sessionVersion;
    private int _navigationVersion;
    private string _currentPath = "/";
    private bool _isAvailable = true;

    public FileTreePanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _isAvailable = true;
            _ = LoadAsync(_session);
        };
        Unloaded += (_, _) =>
        {
            // MainWindow caches this panel so transfers survive a temporary navigation
            // to Home/History/Commands. Session changes and disconnect events still call
            // InvalidateSessionOperations and cancel work tied to the old server.
            _isAvailable = false;
            FileTree.IsEnabled = false;
            CancelNavigation();
        };
    }

    public void RefreshLanguage()
    {
        Bindings.Update();
        foreach (var transfer in Transfers)
            transfer.RefreshLanguage();
        if (_sftp is not null)
            _ = NavigateToPathAsync(_currentPath); // recreate localized data-template flyouts

        if (_session is not { } session) return;
        if (session.State == SessionState.Failed)
            ShowStatus(Loc.T("SSH 연결에 실패했습니다.", "SSH connection failed."));
        else if (session.State == SessionState.Connected && session.SftpState == SftpConnectionState.Connecting)
            ShowStatus(Loc.T("SFTP에 연결하는 중입니다…", "Connecting to SFTP…"));
        else if (session.State == SessionState.Connected &&
                 session.SftpState == SftpConnectionState.Unavailable &&
                 string.IsNullOrWhiteSpace(session.LastSftpError))
            ShowStatus(Loc.T("서버에서 SFTP subsystem을 사용할 수 없습니다.",
                "The SFTP subsystem is unavailable on this server."));
    }

    public async Task LoadAsync(ISshSession? session)
    {
        var sameReadySession = ReferenceEquals(_session, session) &&
            session is { State: SessionState.Connected, SftpState: SftpConnectionState.Ready } &&
            ReferenceEquals(_sftp, session.Sftp) && RootNodes.Count > 0;
        if (sameReadySession)
        {
            if (_isAvailable)
                FileTree.IsEnabled = true;
            RefreshRestoredTransfers();
            return;
        }

        var sessionChanged = !ReferenceEquals(_session, session);
        DetachSession();
        if (sessionChanged)
        {
            InvalidateSessionOperations(clearTransfers: true);
            RootNodes.Clear();
            PathBox.Text = "";
            FileTree.IsEnabled = false;
        }
        _session = session;

        if (session is null)
        {
            _sftp = null;
            RootNodes.Clear();
            PathBox.Text = "";
            FileTree.IsEnabled = false;
            ShowStatus(null);
            LoadingRing.IsActive = false;
            SftpUnavailableState.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            ResumeRestoredButton.Visibility = Visibility.Collapsed;
            return;
        }

        session.StateChanged += OnSessionStateChanged;
        session.SftpStateChanged += OnSftpStateChanged;
        EmptyState.Visibility = Visibility.Collapsed;
        SftpUnavailableState.Visibility = Visibility.Collapsed;

        if (session.State != SessionState.Connected)
        {
            _sftp = null;
            InvalidateSessionOperations(clearTransfers: true);
            RootNodes.Clear();
            FileTree.IsEnabled = false;
            LoadingRing.IsActive = session.State == SessionState.Connecting;
            ShowStatus(session.State == SessionState.Failed
                ? Loc.T("SSH 연결에 실패했습니다.", "SSH connection failed.")
                : null);
            return;
        }

        switch (session.SftpState)
        {
            case SftpConnectionState.Ready:
                _sftp = session.Sftp;
                if (_isAvailable)
                    await NavigateToPathAsync("/");
                RefreshRestoredTransfers();
                break;
            case SftpConnectionState.Unavailable:
                _sftp = null;
                InvalidateSessionOperations(clearTransfers: true);
                RootNodes.Clear();
                FileTree.IsEnabled = false;
                LoadingRing.IsActive = false;
                SftpUnavailableState.Visibility = Visibility.Visible;
                ShowStatus(string.IsNullOrWhiteSpace(session.LastSftpError)
                    ? Loc.T("서버에서 SFTP subsystem을 사용할 수 없습니다.",
                        "The SFTP subsystem is unavailable on this server.")
                    : session.LastSftpError);
                break;
            default:
                _sftp = null;
                RootNodes.Clear();
                FileTree.IsEnabled = false;
                LoadingRing.IsActive = true;
                ShowStatus(Loc.T("SFTP에 연결하는 중입니다…", "Connecting to SFTP…"));
                break;
        }
    }

    /// <summary>Navigate the active SFTP browser to an absolute remote path.</summary>
    public async Task NavigateToPathAsync(string path)
    {
        var sftp = _sftp;
        if (sftp is null || !_isAvailable) return;

        CancelNavigation();
        var cts = new CancellationTokenSource();
        _navigationCts = cts;
        var sessionVersion = _sessionVersion;
        var navigationVersion = ++_navigationVersion;
        var normalized = RemotePath.Normalize(path);

        LoadingRing.IsActive = true;
        FileTree.IsEnabled = false;
        ShowStatus(null);
        try
        {
            var entries = await sftp.ListDirectoryAsync(normalized, cts.Token);
            if (!IsCurrent(sftp, sessionVersion, navigationVersion, cts.Token)) return;

            var root = new FileNode(new RemoteFileEntry
            {
                Name = normalized,
                FullPath = normalized,
                IsDirectory = true,
            }) { IsExpanded = true, SessionVersion = sessionVersion };
            PopulateChildren(root, entries, sessionVersion);

            _currentPath = normalized;
            PathBox.Text = normalized;
            RootNodes.Clear();
            RootNodes.Add(root);
            FileTree.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            // A newer navigation or active-session change superseded this request.
        }
        catch (Exception ex)
        {
            if (IsCurrent(sftp, sessionVersion, navigationVersion, CancellationToken.None))
                ShowOperationError(Loc.T("폴더를 열 수 없습니다", "Could not open folder"), ex);
        }
        finally
        {
            if (navigationVersion == _navigationVersion)
            {
                LoadingRing.IsActive = false;
                FileTree.IsEnabled = _isAvailable && RootNodes.Count > 0 && _sftp is not null;
            }
        }
    }

    private void OnSessionStateChanged(object? sender, SessionState state)
    {
        if (sender is not ISshSession session || !ReferenceEquals(session, _session)) return;
        DispatcherQueue.TryEnqueue(() => _ = LoadAsync(session));
    }

    private void OnSftpStateChanged(object? sender, SftpConnectionState state)
    {
        if (sender is not ISshSession session || !ReferenceEquals(session, _session)) return;
        DispatcherQueue.TryEnqueue(() => _ = LoadAsync(session));
    }

    private void DetachSession()
    {
        if (_session is null) return;
        _session.StateChanged -= OnSessionStateChanged;
        _session.SftpStateChanged -= OnSftpStateChanged;
    }

    private void InvalidateSessionOperations(bool clearTransfers)
    {
        _sessionVersion++;
        _sessionCts.Cancel();
        _sessionCts.Dispose();
        _sessionCts = new CancellationTokenSource();
        CancelNavigation();
        foreach (var transfer in Transfers.Where(item => item.CanCancel).ToList())
            transfer.Cancel();
        if (clearTransfers)
        {
            foreach (var transfer in Transfers.Where(item => !item.IsActive))
                transfer.Dispose();
            Transfers.Clear();
            TransfersPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelNavigation()
    {
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = null;
    }

    private bool IsCurrent(
        ISftpService sftp, int sessionVersion, int navigationVersion, CancellationToken token) =>
        !token.IsCancellationRequested &&
        _isAvailable &&
        ReferenceEquals(_sftp, sftp) &&
        sessionVersion == _sessionVersion &&
        navigationVersion == _navigationVersion;

    private static void PopulateChildren(
        FileNode parent, IReadOnlyList<RemoteFileEntry> entries, int sessionVersion)
    {
        parent.Children.Clear();
        foreach (var entry in entries)
        {
            parent.Children.Add(new FileNode(entry)
            {
                Parent = parent,
                SessionVersion = sessionVersion,
                HasUnrealizedChildren = entry.IsDirectory,
            });
        }
    }

    private async Task LoadChildrenAsync(FileNode directory)
    {
        var sftp = _sftp;
        var cts = _navigationCts;
        if (sftp is null || cts is null || !IsNodeCurrent(directory)) return;
        var sessionVersion = _sessionVersion;
        var navigationVersion = _navigationVersion;
        var entries = await sftp.ListDirectoryAsync(directory.FullPath, cts.Token);
        if (!IsCurrent(sftp, sessionVersion, navigationVersion, cts.Token)) return;
        PopulateChildren(directory, entries, sessionVersion);
    }

    private async void FileTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is not FileNode node || !node.HasUnrealizedChildren || !IsNodeCurrent(node)) return;
        node.HasUnrealizedChildren = false;
        try
        {
            await LoadChildrenAsync(node);
        }
        catch (OperationCanceledException)
        {
            if (_isAvailable && node.SessionVersion == _sessionVersion && _sftp is not null)
                node.HasUnrealizedChildren = true;
        }
        catch (Exception ex)
        {
            node.HasUnrealizedChildren = true;
            ShowOperationError(Loc.T("폴더를 열 수 없습니다", "Could not open folder"), ex);
        }
    }

    private async void Node_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileNode { IsDirectory: true } node ||
            !IsNodeCurrent(node)) return;
        e.Handled = true;
        await NavigateToPathAsync(node.FullPath);
    }

    private async void ParentPath_Click(object sender, RoutedEventArgs e)
        => await NavigateToPathAsync(RemotePath.GetDirectory(_currentPath));

    private async void GoPath_Click(object sender, RoutedEventArgs e)
        => await NavigateToPathAsync(PathBox.Text);

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await NavigateToPathAsync(_currentPath);

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var sftp = _sftp;
        if (sftp is null || !_isAvailable)
            return;

        var queryBox = new TextBox
        {
            Header = Loc.T("파일 또는 폴더 이름", "File or folder name"),
            PlaceholderText = Loc.T("예: nginx, .log, config", "For example: nginx, .log, config"),
            MinWidth = 300,
            MaxLength = 128,
        };
        var queryDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("원격 파일명 검색", "Search remote filenames"),
            Content = queryBox,
            PrimaryButtonText = Loc.T("검색", "Search"),
            CloseButtonText = Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        queryBox.TextChanged += (_, _) =>
            queryDialog.IsPrimaryButtonEnabled = IsValidSearchQuery(queryBox.Text);
        queryDialog.Opened += (_, _) => queryBox.Focus(FocusState.Programmatic);
        if (await ShowDialogSafelyAsync(queryDialog) != ContentDialogResult.Primary)
            return;

        var query = queryBox.Text.Trim();
        CancelNavigation();
        var cts = new CancellationTokenSource();
        _navigationCts = cts;
        var sessionVersion = _sessionVersion;
        var navigationVersion = ++_navigationVersion;
        LoadingRing.IsActive = true;
        FileTree.IsEnabled = false;
        ShowStatus(Loc.T(
            $"'{query}' 파일명을 검색하는 중…",
            $"Searching filenames for '{query}'…"));
        try
        {
            var matches = await sftp.SearchByNameAsync(_currentPath, query, 500, cts.Token);
            if (!IsCurrent(sftp, sessionVersion, navigationVersion, cts.Token))
                return;
            if (matches.Count == 0)
            {
                ShowStatus(Loc.T(
                    $"'{query}'과(와) 일치하는 파일명이 없습니다.",
                    $"No filenames match '{query}'."));
                return;
            }

            var choices = matches.Select(match => new RemoteSearchChoice(
                match,
                $"[{(match.Entry.IsDirectory ? Loc.T("폴더", "DIR") : Loc.T("파일", "FILE"))}]  {match.RelativePath}"))
                .ToList();
            var results = new ListView
            {
                ItemsSource = choices,
                DisplayMemberPath = nameof(RemoteSearchChoice.Display),
                SelectionMode = ListViewSelectionMode.Single,
                MinWidth = 420,
                MaxHeight = 420,
            };
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = Loc.T(
                    $"{_currentPath} 아래에서 {matches.Count:N0}개를 찾았습니다. 열 항목을 선택하세요.",
                    $"Found {matches.Count:N0} item(s) below {_currentPath}. Select one to open."),
                Foreground = ThemeResources.Brush(this, "TextMuted"),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(results);
            if (matches.Count == 500)
            {
                content.Children.Add(new TextBlock
                {
                    Text = Loc.T(
                        "처음 500개 결과만 표시합니다. 검색어를 더 구체적으로 입력하세요.",
                        "Only the first 500 results are shown. Use a more specific query."),
                    Foreground = ThemeResources.Brush(this, "StatusAmber"),
                    FontSize = 10.5,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            var resultDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.T("검색 결과", "Search results"),
                Content = content,
                PrimaryButtonText = Loc.T("위치 열기", "Open location"),
                CloseButtonText = Loc.T("닫기", "Close"),
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
            };
            var openRequestedByDoubleTap = false;
            results.SelectionChanged += (_, _) =>
                resultDialog.IsPrimaryButtonEnabled = results.SelectedItem is RemoteSearchChoice;
            results.DoubleTapped += (_, args) =>
            {
                if (results.SelectedItem is RemoteSearchChoice)
                {
                    args.Handled = true;
                    openRequestedByDoubleTap = true;
                    resultDialog.Hide();
                }
            };
            var dialogResult = await ShowDialogSafelyAsync(resultDialog);
            if ((dialogResult == ContentDialogResult.Primary || openRequestedByDoubleTap) &&
                results.SelectedItem is RemoteSearchChoice selected &&
                IsCurrent(sftp, sessionVersion, navigationVersion, cts.Token))
            {
                var destination = selected.Result.Entry.IsDirectory
                    ? selected.Result.Entry.FullPath
                    : RemotePath.GetDirectory(selected.Result.Entry.FullPath);
                await NavigateToPathAsync(destination);
            }
            else if (IsCurrent(sftp, sessionVersion, navigationVersion, cts.Token))
            {
                ShowStatus(null);
            }
        }
        catch (OperationCanceledException)
        {
            // Session changes and newer navigation requests supersede this search.
        }
        catch (Exception error)
        {
            if (IsCurrent(sftp, sessionVersion, navigationVersion, CancellationToken.None))
                ShowOperationError(Loc.T("원격 검색 실패", "Remote search failed"), error);
        }
        finally
        {
            if (navigationVersion == _navigationVersion)
            {
                LoadingRing.IsActive = false;
                FileTree.IsEnabled = _isAvailable && RootNodes.Count > 0 && _sftp is not null;
            }
        }
    }

    private async void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await NavigateToPathAsync(PathBox.Text);
    }

    private void Node_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        if ((sender as FrameworkElement)?.DataContext is FileNode candidate && !IsNodeCurrent(candidate)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        if ((sender as FrameworkElement)?.DataContext is FileNode node)
            e.DragUIOverride.Caption = Loc.T($"{node.DirectoryPath}에 업로드", $"Upload to {node.DirectoryPath}");
        e.Handled = true;
    }

    private async void Node_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        await HandleDropAsync(e, (sender as FrameworkElement)?.DataContext as FileNode);
    }

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Loc.T($"{_currentPath}에 업로드", $"Upload to {_currentPath}");
    }

    private async void Tree_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        await HandleDropAsync(e, null);
    }

    private async Task HandleDropAsync(DragEventArgs e, FileNode? targetNode)
    {
        var sftp = _sftp;
        var sessionVersion = _sessionVersion;
        if (sftp is null || RootNodes.Count == 0) return;
        if (targetNode is not null && !IsNodeCurrent(targetNode)) return;

        IReadOnlyList<IStorageItem> items;
        try { items = await e.DataView.GetStorageItemsAsync(); }
        catch (Exception ex)
        {
            ShowOperationError(Loc.T("파일을 읽을 수 없습니다", "Could not read dropped files"), ex);
            return;
        }
        if (!ReferenceEquals(_sftp, sftp) || sessionVersion != _sessionVersion) return;

        var directory = targetNode is null ? RootNodes[0]
            : targetNode.IsDirectory ? targetNode
            : targetNode.Parent ?? RootNodes[0];
        if (directory.HasUnrealizedChildren)
        {
            directory.HasUnrealizedChildren = false;
            try { await LoadChildrenAsync(directory); }
            catch (OperationCanceledException)
            {
                if (_isAvailable && directory.SessionVersion == _sessionVersion &&
                    ReferenceEquals(_sftp, sftp))
                    directory.HasUnrealizedChildren = true;
                return;
            }
            catch (Exception ex)
            {
                directory.HasUnrealizedChildren = true;
                if (_isAvailable && directory.SessionVersion == _sessionVersion &&
                    ReferenceEquals(_sftp, sftp))
                    ShowOperationError(Loc.T("대상 폴더를 열 수 없습니다", "Could not open target folder"), ex);
                return;
            }
        }

        foreach (var item in items.Where(item =>
                     item is StorageFile or StorageFolder &&
                     !string.IsNullOrWhiteSpace(item.Path)))
        {
            var collision = directory.Children.Any(child => child.Name.Equals(item.Name, StringComparison.Ordinal)) ||
                Transfers.Any(transferItem => transferItem.CanCancel &&
                    transferItem.DestinationPath.Equals(
                        RemotePath.Combine(directory.FullPath, item.Name),
                        StringComparison.Ordinal));
            var conflictPolicy = await ResolveConflictPolicyAsync(item.Name, collision);
            if (!_isAvailable || !ReferenceEquals(_sftp, sftp) || sessionVersion != _sessionVersion)
                return;
            if (conflictPolicy is null) continue;
            _ = UploadAsync(item, directory.FullPath, conflictPolicy.Value, sftp, sessionVersion);
        }
    }

    private async Task UploadAsync(
        IStorageItem item,
        string remoteDirectory,
        SftpConflictPolicy conflictPolicy,
        ISftpService sftp,
        int sessionVersion)
    {
        long size = 0;
        if (item is StorageFile file)
        {
            try { size = (long)(await file.GetBasicPropertiesAsync()).Size; } catch { }
        }
        if (!ReferenceEquals(_sftp, sftp) || sessionVersion != _sessionVersion) return;

        var destination = RemotePath.Combine(remoteDirectory, item.Name);
        var options = CreateTransferOptions(conflictPolicy);
        var queuedJob = CreateSingleQueuedJob(
            sutty.Core.Sftp.SftpTransferDirection.Upload,
            item.Path,
            destination,
            item.Name,
            size,
            options);
        try { _transferQueue.Upsert(queuedJob); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or InvalidOperationException)
        {
            ShowOperationError(Loc.T("전송 큐를 저장할 수 없습니다", "Could not save the transfer queue"), ex);
            return;
        }
        if (!_transferQueue.TryAcquireTargetLease(
                queuedJob.Id,
                queuedJob.Targets[0].Id,
                _targetLeaseOwnerToken,
                out var targetLease))
        {
            ShowStatus(Loc.T("이 전송 작업을 시작할 수 없습니다.",
                "This transfer job could not be started."));
            return;
        }
        SftpTransferItemVm? transfer;
        try
        {
            transfer = TryAddTransfer(
                item.Name,
                item.Path,
                destination,
                size,
                UiSftpTransferDirection.Upload,
                queuedJob.Id);
        }
        catch
        {
            targetLease!.Dispose();
            throw;
        }
        if (transfer is null)
        {
            targetLease!.Dispose();
            _transferQueue.Delete(queuedJob.Id);
            return;
        }
        var workerAcquired = false;
        try
        {
            var token = transfer.Token;
            await _transferWorkerGate.WaitAsync(token);
            workerAcquired = true;
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_sftp, sftp) || sessionVersion != _sessionVersion)
                throw new OperationCanceledException(token);
            transfer.Start();
            UpdateQueuedTransfer(queuedJob, SftpQueueTargetState.Running);
            var durableProgress = new DurableProgressState();
            var progress = CreateQueuedTransferProgress(queuedJob, transfer, durableProgress);
            await sftp.UploadPathAsync(
                item.Path,
                destination,
                options,
                progress,
                token);
            transfer.Complete();
            UpdateQueuedTransfer(
                queuedJob,
                SftpQueueTargetState.Succeeded,
                bytesTransferred: durableProgress.BytesTransferred,
                totalBytes: durableProgress.TotalBytes);
            if (ReferenceEquals(_sftp, sftp) && sessionVersion == _sessionVersion)
                await NavigateToPathAsync(_currentPath);
        }
        catch (OperationCanceledException)
        {
            if (transfer.PauseRequested)
            {
                transfer.MarkPaused();
                UpdateQueuedTransfer(queuedJob, SftpQueueTargetState.Paused);
            }
            else
            {
                transfer.MarkCancelled();
                UpdateQueuedTransfer(
                    queuedJob,
                    transfer.UserCancellationRequested
                        ? SftpQueueTargetState.Cancelled
                        : SftpQueueTargetState.Interrupted);
            }
        }
        catch (Exception ex)
        {
            transfer.Fail(ex.Message);
            UpdateQueuedTransfer(queuedJob, SftpQueueTargetState.Failed, ex.Message);
            if (ReferenceEquals(_sftp, sftp) && sessionVersion == _sessionVersion)
                ShowOperationError(Loc.T("업로드 실패", "Upload failed"), ex);
        }
        finally
        {
            targetLease!.Dispose();
            if (workerAcquired) _transferWorkerGate.Release();
            if (transfer.State != SftpTransferState.Paused)
                transfer.Dispose();
            RefreshRestoredTransfers();
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { CanDownload: true } node || !IsNodeCurrent(node) ||
            _sftp is not { } sftp) return;
        var sessionVersion = _sessionVersion;
        if (OwnerWindowHandle == IntPtr.Zero)
        {
            ShowStatus(Loc.T("다운로드 창을 열 수 없습니다.", "The download picker is not available."));
            return;
        }

        string? localPath;
        try
        {
            if (node.IsDirectory)
            {
                var folderPicker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.Downloads,
                };
                folderPicker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(folderPicker, OwnerWindowHandle);
                var selectedFolder = await folderPicker.PickSingleFolderAsync();
                localPath = selectedFolder is null || string.IsNullOrWhiteSpace(selectedFolder.Path)
                    ? null
                    : Path.Combine(selectedFolder.Path, node.Name);
            }
            else
            {
                var extension = Path.GetExtension(node.Name);
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.Downloads,
                    SuggestedFileName = node.Name,
                };
                if (!string.IsNullOrEmpty(extension))
                    picker.DefaultFileExtension = extension;
                picker.FileTypeChoices.Add(Loc.T("파일", "File"),
                    [string.IsNullOrEmpty(extension) ? "*" : extension]);
                InitializeWithWindow.Initialize(picker, OwnerWindowHandle);
                localPath = (await picker.PickSaveFileAsync())?.Path;
            }
        }
        catch (Exception ex)
        {
            if (_isAvailable && ReferenceEquals(_sftp, sftp) && sessionVersion == _sessionVersion)
                ShowOperationError(Loc.T("다운로드 창을 열 수 없습니다", "Could not open the download picker"), ex);
            return;
        }

        if (localPath is null || !_isAvailable || !ReferenceEquals(_sftp, sftp) ||
            sessionVersion != _sessionVersion) return;
        if (string.IsNullOrWhiteSpace(localPath))
        {
            ShowStatus(Loc.T("이 위치는 직접 파일 경로를 제공하지 않아 다운로드할 수 없습니다.",
                "This location does not provide a direct file path for downloads."));
            return;
        }
        var localCollision = File.Exists(localPath) || Directory.Exists(localPath);
        var conflictPolicy = await ResolveConflictPolicyAsync(node.Name, localCollision);
        if (conflictPolicy is null || !_isAvailable || !ReferenceEquals(_sftp, sftp) ||
            sessionVersion != _sessionVersion)
        {
            return;
        }
        _ = DownloadAsync(node, localPath, conflictPolicy.Value, sftp, sessionVersion);
    }

    private async Task DownloadAsync(
        FileNode node,
        string localPath,
        SftpConflictPolicy conflictPolicy,
        ISftpService sftp,
        int sessionVersion)
    {
        var options = CreateTransferOptions(conflictPolicy);
        var queuedJob = CreateSingleQueuedJob(
            sutty.Core.Sftp.SftpTransferDirection.Download,
            node.FullPath,
            localPath,
            node.Name,
            node.Entry.Size,
            options);
        try { _transferQueue.Upsert(queuedJob); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or InvalidOperationException)
        {
            ShowOperationError(Loc.T("전송 큐를 저장할 수 없습니다", "Could not save the transfer queue"), ex);
            return;
        }
        if (!_transferQueue.TryAcquireTargetLease(
                queuedJob.Id,
                queuedJob.Targets[0].Id,
                _targetLeaseOwnerToken,
                out var targetLease))
        {
            ShowStatus(Loc.T("이 전송 작업을 시작할 수 없습니다.",
                "This transfer job could not be started."));
            return;
        }
        SftpTransferItemVm? transfer;
        try
        {
            transfer = TryAddTransfer(
                node.Name,
                node.FullPath,
                localPath,
                node.Entry.Size,
                UiSftpTransferDirection.Download,
                queuedJob.Id);
        }
        catch
        {
            targetLease!.Dispose();
            throw;
        }
        if (transfer is null)
        {
            targetLease!.Dispose();
            _transferQueue.Delete(queuedJob.Id);
            return;
        }
        var workerAcquired = false;
        try
        {
            var token = transfer.Token;
            await _transferWorkerGate.WaitAsync(token);
            workerAcquired = true;
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_sftp, sftp) || sessionVersion != _sessionVersion)
                throw new OperationCanceledException(token);
            transfer.Start();
            UpdateQueuedTransfer(queuedJob, SftpQueueTargetState.Running);
            var durableProgress = new DurableProgressState();
            var progress = CreateQueuedTransferProgress(queuedJob, transfer, durableProgress);
            // The picker chooses the local path; the durable policy above controls collisions.
            await sftp.DownloadPathAsync(
                node.FullPath,
                localPath,
                options,
                progress,
                token);
            transfer.Complete();
            UpdateQueuedTransfer(
                queuedJob,
                SftpQueueTargetState.Succeeded,
                bytesTransferred: durableProgress.BytesTransferred,
                totalBytes: durableProgress.TotalBytes);
        }
        catch (OperationCanceledException)
        {
            if (transfer.PauseRequested)
            {
                transfer.MarkPaused();
                UpdateQueuedTransfer(queuedJob, SftpQueueTargetState.Paused);
            }
            else
            {
                transfer.MarkCancelled();
                UpdateQueuedTransfer(
                    queuedJob,
                    transfer.UserCancellationRequested
                        ? SftpQueueTargetState.Cancelled
                        : SftpQueueTargetState.Interrupted);
            }
        }
        catch (Exception ex)
        {
            transfer.Fail(ex.Message);
            UpdateQueuedTransfer(queuedJob, SftpQueueTargetState.Failed, ex.Message);
            if (ReferenceEquals(_sftp, sftp) && sessionVersion == _sessionVersion)
                ShowOperationError(Loc.T("다운로드 실패", "Download failed"), ex);
        }
        finally
        {
            targetLease!.Dispose();
            if (workerAcquired) _transferWorkerGate.Release();
            if (transfer.State != SftpTransferState.Paused)
                transfer.Dispose();
            RefreshRestoredTransfers();
        }
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { IsDirectory: true } node || !IsNodeCurrent(node) ||
            _sftp is not { } sftp) return;
        var version = _sessionVersion;
        var token = _sessionCts.Token;
        var name = await PromptForNameAsync(
            Loc.T("새 폴더", "New folder"), "", Loc.T("만들기", "Create"));
        if (name is null || !_isAvailable || token.IsCancellationRequested ||
            !ReferenceEquals(_sftp, sftp) || version != _sessionVersion) return;
        try
        {
            await sftp.CreateDirectoryAsync(RemotePath.Combine(node.FullPath, name), token);
            if (ReferenceEquals(_sftp, sftp) && version == _sessionVersion)
                await NavigateToPathAsync(_currentPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_sftp, sftp) && version == _sessionVersion)
                ShowOperationError(Loc.T("폴더 만들기 실패", "Could not create folder"), ex);
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { CanModify: true } node || !IsNodeCurrent(node) ||
            _sftp is not { } sftp) return;
        var version = _sessionVersion;
        var token = _sessionCts.Token;
        var name = await PromptForNameAsync(
            Loc.T("이름 바꾸기", "Rename"), node.Name, Loc.T("변경", "Rename"));
        if (name is null || name == node.Name || !_isAvailable || token.IsCancellationRequested ||
            !ReferenceEquals(_sftp, sftp) || version != _sessionVersion) return;
        var destination = RemotePath.Combine(RemotePath.GetDirectory(node.FullPath), name);
        try
        {
            await sftp.MoveAsync(node.FullPath, destination, token);
            if (ReferenceEquals(_sftp, sftp) && version == _sessionVersion)
                await NavigateToPathAsync(_currentPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_sftp, sftp) && version == _sessionVersion)
                ShowOperationError(Loc.T("이름 변경 실패", "Rename failed"), ex);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { CanModify: true } node || !IsNodeCurrent(node) ||
            _sftp is not { } sftp) return;
        var version = _sessionVersion;
        var token = _sessionCts.Token;
        try
        {
            if (node.IsDirectory)
            {
                var preview = await sftp.PreviewDeleteAsync(node.FullPath, token);
                if (!_isAvailable || token.IsCancellationRequested ||
                    !ReferenceEquals(_sftp, sftp) || version != _sessionVersion)
                    return;
                if (!await ConfirmRecursiveDeleteAsync(node, preview))
                    return;
                if (!_isAvailable || token.IsCancellationRequested ||
                    !ReferenceEquals(_sftp, sftp) || version != _sessionVersion)
                {
                    return;
                }
                await sftp.DeletePathRecursiveAsync(node.FullPath, token);
            }
            else
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = Loc.T("원격 파일 삭제", "Delete remote file"),
                    Content = Loc.T($"파일 '{node.Name}'을(를) 삭제할까요? 이 작업은 되돌릴 수 없습니다.",
                        $"Delete '{node.Name}'? This cannot be undone."),
                    PrimaryButtonText = Loc.T("삭제", "Delete"),
                    CloseButtonText = Loc.T("취소", "Cancel"),
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await ShowDialogSafelyAsync(dialog) != ContentDialogResult.Primary) return;
                if (!_isAvailable || token.IsCancellationRequested ||
                    !ReferenceEquals(_sftp, sftp) || version != _sessionVersion) return;
                await sftp.DeleteFileAsync(node.FullPath, token);
            }
            if (ReferenceEquals(_sftp, sftp) && version == _sessionVersion)
                await NavigateToPathAsync(_currentPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_sftp, sftp) && version == _sessionVersion)
                ShowOperationError(Loc.T("삭제 실패", "Delete failed"), ex);
        }
    }

    private async void MoveTo_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { CanModify: true } node || !IsNodeCurrent(node) ||
            _sftp is not { } sftp)
        {
            return;
        }

        var version = _sessionVersion;
        var token = _sessionCts.Token;
        var destination = await PromptForRemotePathAsync(node.FullPath);
        if (destination is null || destination == RemotePath.Normalize(node.FullPath) ||
            !_isAvailable || token.IsCancellationRequested ||
            !ReferenceEquals(_sftp, sftp) || version != _sessionVersion)
        {
            return;
        }

        try
        {
            await sftp.MoveAsync(node.FullPath, destination, token);
            if (ReferenceEquals(_sftp, sftp) && version == _sessionVersion)
                await NavigateToPathAsync(_currentPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (ReferenceEquals(_sftp, sftp) && version == _sessionVersion)
                ShowOperationError(Loc.T("원격 이동 실패", "Remote move failed"), error);
        }
    }

    private async void ChangePermissions_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { CanModify: true } node || !IsNodeCurrent(node) ||
            _sftp is not { } sftp) return;
        var version = _sessionVersion;
        var token = _sessionCts.Token;
        var modeBox = new TextBox
        {
            Text = node.IsDirectory ? "0755" : "0644",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            Header = Loc.T("Unix 권한 (8진수)", "Unix permissions (octal)"),
            PlaceholderText = "0755",
            MinWidth = 240,
            MaxLength = 4,
        };
        var recursive = new CheckBox
        {
            Content = Loc.T("하위 항목에도 적용 (심볼릭 링크 제외)",
                "Apply to descendants (symbolic links excluded)"),
            Visibility = node.IsDirectory ? Visibility.Visible : Visibility.Collapsed,
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = Loc.T(
                $"{node.FullPath}의 권한을 변경합니다. 서버 권한 정책에 따라 실패할 수 있습니다.",
                $"Change permissions for {node.FullPath}. Server permissions can reject this operation."),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(modeBox);
        content.Children.Add(recursive);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("원격 권한 변경", "Change remote permissions"),
            Content = content,
            PrimaryButtonText = Loc.T("적용", "Apply"),
            CloseButtonText = Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = TryParseUnixMode(modeBox.Text, out _),
        };
        modeBox.TextChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = TryParseUnixMode(modeBox.Text, out _);
        dialog.Opened += (_, _) =>
        {
            modeBox.Focus(FocusState.Programmatic);
            modeBox.SelectAll();
        };

        if (await ShowDialogSafelyAsync(dialog) != ContentDialogResult.Primary ||
            !TryParseUnixMode(modeBox.Text, out var unixMode) ||
            !_isAvailable || token.IsCancellationRequested ||
            !ReferenceEquals(_sftp, sftp) || version != _sessionVersion)
        {
            return;
        }

        try
        {
            await sftp.ChangePermissionsAsync(node.FullPath, unixMode, recursive.IsChecked == true, token);
            if (ReferenceEquals(_sftp, sftp) && version == _sessionVersion)
                await NavigateToPathAsync(_currentPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_sftp, sftp) && version == _sessionVersion)
                ShowOperationError(Loc.T("권한 변경 실패", "Permission change failed"), ex);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { } node || !IsNodeCurrent(node)) return;
        var package = new DataPackage();
        package.SetText(node.FullPath);
        Clipboard.SetContent(package);
    }

    private void OpenTerminalHere_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { } node || !IsNodeCurrent(node)) return;
        OpenTerminalHereRequested?.Invoke(this, node.DirectoryPath);
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SftpTransferItemVm transfer)
            transfer.Cancel(userInitiated: true);
    }

    private void PauseTransfer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SftpTransferItemVm transfer)
            transfer.Pause();
    }

    private void ResumePausedTransfer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SftpTransferItemVm paused ||
            paused.State != SftpTransferState.Paused ||
            string.IsNullOrWhiteSpace(paused.QueueJobId) ||
            _sftp is not { } sftp || _session is null)
        {
            return;
        }

        SftpQueuedJob? job;
        try { job = _transferQueue.Get(paused.QueueJobId); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowOperationError(Loc.T("전송 큐를 읽을 수 없습니다", "Could not read the transfer queue"), error);
            return;
        }
        var identity = CurrentSftpPersistenceId();
        if (job is null || job.Mode != SftpQueueMode.Single ||
            !job.Targets.Any(target => target.Id == identity))
        {
            ShowStatus(Loc.T("재개할 전송 정보를 찾을 수 없습니다.", "The paused transfer could not be found."));
            return;
        }

        var target = job.Targets.Single(item => item.Id == identity);
        if (!_transferQueue.TryAcquireRetryTargetLease(
                job.Id,
                target.Id,
                _targetLeaseOwnerToken,
                out var targetLease))
        {
            ShowStatus(Loc.T("이 전송은 이미 실행 중이거나 완료되었습니다.",
                "This transfer is already running or finished."));
            return;
        }

        try
        {
            var sessionVersion = _sessionVersion;
            Transfers.Remove(paused);
            paused.Dispose();
            var transfer = TryAddTransfer(
                job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload
                    ? Path.GetFileName(job.SourcePath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar))
                    : RemotePath.GetName(job.SourcePath),
                job.SourcePath,
                job.DestinationPath,
                target.TotalBytes,
                job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload
                    ? UiSftpTransferDirection.Upload
                    : UiSftpTransferDirection.Download,
                job.Id);
            if (transfer is null)
                return;

            _ = ResumeSingleQueuedJobAsync(
                job, transfer, sftp, sessionVersion, targetLease!);
            targetLease = null;
        }
        finally
        {
            targetLease?.Dispose();
        }
    }

    public void CancelTransfersForSession(ISshSession session, bool userInitiated)
    {
        if (!ReferenceEquals(_session, session))
            return;
        foreach (var transfer in Transfers.Where(item => item.CanCancel).ToList())
            transfer.Cancel(userInitiated);
    }

    private void RefreshRestoredTransfers()
    {
        if (_session is not
            {
                State: SessionState.Connected,
                SftpState: SftpConnectionState.Ready,
            })
        {
            ResumeRestoredButton.Visibility = Visibility.Collapsed;
            return;
        }

        var identity = CurrentSftpPersistenceId();
        IReadOnlyList<SftpQueuedJob> jobs;
        try
        {
            jobs = _transferQueue.RecoverIncomplete()
                .Where(job => job.Mode == SftpQueueMode.Single &&
                              job.Targets.Any(target => target.Id == identity))
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowOperationError(
                Loc.T("전송 큐를 복원할 수 없습니다", "Could not restore the transfer queue"),
                error);
            ResumeRestoredButton.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var job in jobs)
        {
            if (Transfers.Any(transfer => transfer.QueueJobId == job.Id))
                continue;
            var target = job.Targets.Single(item => item.Id == identity);
            var direction = job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload
                ? UiSftpTransferDirection.Upload
                : UiSftpTransferDirection.Download;
            var name = job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload
                ? Path.GetFileName(job.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : RemotePath.GetName(job.SourcePath);
            TryAddTransfer(
                string.IsNullOrWhiteSpace(name) ? Loc.T("복원된 전송", "Restored transfer") : name,
                job.SourcePath,
                job.DestinationPath,
                target.TotalBytes,
                direction,
                job.Id);
        }

        ReconcileRestoredTransferRows(jobs);
        if (jobs.Count > 0)
            TransfersPanel.Visibility = Visibility.Visible;
    }

    private void ResumeRestoredTransfers_Click(object sender, RoutedEventArgs e)
    {
        if (_sftp is not { } sftp || _session is null)
            return;
        var identity = CurrentSftpPersistenceId();
        var sessionVersion = _sessionVersion;
        IReadOnlyList<SftpQueuedJob> jobs;
        try
        {
            jobs = _transferQueue.RecoverIncomplete()
                .Where(job => job.Mode == SftpQueueMode.Single &&
                              job.Targets.Any(target => target.Id == identity))
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowOperationError(
                Loc.T("전송 큐를 복원할 수 없습니다", "Could not restore the transfer queue"),
                error);
            return;
        }
        ReconcileRestoredTransferRows(jobs);
        var alreadyResuming = false;
        foreach (var job in jobs)
        {
            var transfer = Transfers.FirstOrDefault(item => item.QueueJobId == job.Id);
            if (transfer?.State is SftpTransferState.Running or SftpTransferState.Cancelling)
                continue;

            var target = job.Targets.Single(item => item.Id == identity);
            if (!_transferQueue.TryAcquireRetryTargetLease(
                    job.Id,
                    target.Id,
                    _targetLeaseOwnerToken,
                    out var targetLease))
            {
                alreadyResuming = true;
                continue;
            }

            try
            {
                if (transfer is not null && transfer.State != SftpTransferState.Queued)
                {
                    Transfers.Remove(transfer);
                    transfer.Dispose();
                    transfer = null;
                }
                transfer ??= TryAddTransfer(
                    job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload
                        ? Path.GetFileName(job.SourcePath.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar))
                        : RemotePath.GetName(job.SourcePath),
                    job.SourcePath,
                    job.DestinationPath,
                    target.TotalBytes,
                    job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload
                        ? UiSftpTransferDirection.Upload
                        : UiSftpTransferDirection.Download,
                    job.Id);
                if (transfer is null)
                    continue;

                _ = ResumeSingleQueuedJobAsync(
                    job, transfer, sftp, sessionVersion, targetLease!);
                targetLease = null;
            }
            finally
            {
                targetLease?.Dispose();
            }
        }

        if (alreadyResuming)
            ShowStatus(Loc.T("일부 전송은 이미 실행 중이거나 완료되었습니다.",
                "Some transfers are already running or finished."));
    }

    private void ReconcileRestoredTransferRows(IReadOnlyList<SftpQueuedJob> jobs)
    {
        var durableJobIds = jobs
            .Select(job => job.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var stale in Transfers.Where(transfer =>
                     transfer.State == SftpTransferState.Queued &&
                     !string.IsNullOrWhiteSpace(transfer.QueueJobId) &&
                     !durableJobIds.Contains(transfer.QueueJobId)).ToList())
        {
            Transfers.Remove(stale);
            stale.Dispose();
        }

        ResumeRestoredButton.Visibility = jobs.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task ResumeSingleQueuedJobAsync(
        SftpQueuedJob job,
        SftpTransferItemVm transfer,
        ISftpService sftp,
        int sessionVersion,
        IDisposable targetLease)
    {
        var workerAcquired = false;
        try
        {
            var token = transfer.Token;
            if (job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload &&
                !File.Exists(job.SourcePath) && !Directory.Exists(job.SourcePath))
            {
                throw new FileNotFoundException(
                    Loc.T("업로드 원본이 더 이상 존재하지 않습니다.",
                        "The upload source no longer exists."),
                    job.SourcePath);
            }
            await _transferWorkerGate.WaitAsync(token);
            workerAcquired = true;
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_sftp, sftp) || sessionVersion != _sessionVersion)
                throw new OperationCanceledException(token);
            transfer.Start();
            UpdateQueuedTransfer(job, SftpQueueTargetState.Running);
            var durableProgress = new DurableProgressState();
            var progress = CreateQueuedTransferProgress(job, transfer, durableProgress);
            if (job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload)
            {
                await sftp.UploadPathAsync(
                    job.SourcePath, job.DestinationPath, job.Options, progress, token);
            }
            else
            {
                await sftp.DownloadPathAsync(
                    job.SourcePath, job.DestinationPath, job.Options, progress, token);
            }
            transfer.Complete();
            UpdateQueuedTransfer(
                job,
                SftpQueueTargetState.Succeeded,
                bytesTransferred: durableProgress.BytesTransferred,
                totalBytes: durableProgress.TotalBytes);
            if (job.Direction == sutty.Core.Sftp.SftpTransferDirection.Upload &&
                ReferenceEquals(_sftp, sftp) && sessionVersion == _sessionVersion)
            {
                await NavigateToPathAsync(_currentPath);
            }
        }
        catch (OperationCanceledException)
        {
            if (transfer.PauseRequested)
            {
                transfer.MarkPaused();
                UpdateQueuedTransfer(job, SftpQueueTargetState.Paused);
            }
            else
            {
                transfer.MarkCancelled();
                UpdateQueuedTransfer(
                    job,
                    transfer.UserCancellationRequested
                        ? SftpQueueTargetState.Cancelled
                        : SftpQueueTargetState.Interrupted);
            }
        }
        catch (Exception error)
        {
            transfer.Fail(error.Message);
            UpdateQueuedTransfer(job, SftpQueueTargetState.Failed, error.Message);
            if (ReferenceEquals(_sftp, sftp) && sessionVersion == _sessionVersion)
                ShowOperationError(Loc.T("복원 전송 실패", "Restored transfer failed"), error);
        }
        finally
        {
            targetLease.Dispose();
            if (workerAcquired) _transferWorkerGate.Release();
            if (transfer.State != SftpTransferState.Paused)
                transfer.Dispose();
            RefreshRestoredTransfers();
        }
    }

    private SftpQueuedJob CreateSingleQueuedJob(
        sutty.Core.Sftp.SftpTransferDirection direction,
        string sourcePath,
        string destinationPath,
        string displayName,
        long totalBytes,
        SftpTransferOptions options) => new()
    {
        Mode = SftpQueueMode.Single,
        Direction = direction,
        SourcePath = sourcePath,
        DestinationPath = destinationPath,
        Options = options,
        State = SftpQueueJobState.Pending,
        Targets =
        [
            new SftpQueuedTarget
            {
                Id = CurrentSftpPersistenceId(),
                DisplayName = _session?.Info.Title ?? displayName,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                TotalBytes = Math.Max(0, totalBytes),
                State = SftpQueueTargetState.Pending,
            },
        ],
    };

    private string CurrentSftpPersistenceId()
    {
        if (_session is null)
            throw new InvalidOperationException("An SSH session is required for a durable SFTP job.");
        return !string.IsNullOrWhiteSpace(_session.Info.SavedHostId)
            ? $"profile:{_session.Info.SavedHostId}"
            : $"endpoint:{_session.Info.Username.Trim().ToLowerInvariant()}@" +
              $"{_session.Info.Host.Trim().ToLowerInvariant()}:{_session.Info.Port}";
    }

    private void UpdateQueuedTransfer(
        SftpQueuedJob job,
        SftpQueueTargetState state,
        string? error = null,
        long bytesTransferred = 0,
        long totalBytes = 0)
    {
        try
        {
            _transferQueue.UpdateTarget(
                job.Id,
                job.Targets[0].Id,
                state,
                bytesTransferred,
                totalBytes,
                error: error);
        }
        catch (Exception persistenceError) when (persistenceError is IOException or
                                                   UnauthorizedAccessException or
                                                   ArgumentException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"SFTP queue update failed: {persistenceError.GetType().Name}");
        }
    }

    private SftpTransferItemVm? TryAddTransfer(
        string name,
        string source,
        string destination,
        long size,
        UiSftpTransferDirection direction,
        string? queueJobId = null)
    {
        while (Transfers.Count >= 8)
        {
            var removable = Transfers.FirstOrDefault(item => !item.IsActive);
            if (removable is null) break;
            Transfers.Remove(removable);
            removable.Dispose();
        }
        if (Transfers.Count >= 8)
        {
            ShowStatus(Loc.T("동시에 대기하거나 실행할 수 있는 전송은 최대 8개입니다.",
                "Up to 8 transfers can be queued or running at once."));
            return null;
        }
        var transfer = new SftpTransferItemVm(
            name, source, destination, size, direction, queueJobId);
        Transfers.Add(transfer);
        TransfersPanel.Visibility = Visibility.Visible;
        return transfer;
    }

    private static SftpTransferOptions CreateTransferOptions(SftpConflictPolicy conflictPolicy)
    {
        var settings = SettingsService.Current;
        return new SftpTransferOptions
        {
            Overwrite = conflictPolicy is SftpConflictPolicy.Overwrite or SftpConflictPolicy.NewerOnly,
            ConflictPolicy = conflictPolicy,
            Resume = true,
            VerifyChecksum = string.Equals(
                settings.SftpVerificationMode,
                "Sha256",
                StringComparison.OrdinalIgnoreCase),
            RetryEnabled = settings.SftpRetryEnabled,
            MaxRetries = settings.SftpRetryCount,
        };
    }

    private async Task<SftpConflictPolicy?> ResolveConflictPolicyAsync(string name, bool collision)
    {
        var configured = SftpTransferOptions.ParseConflictPolicy(
            SettingsService.Current.SftpConflictPolicy);
        if (configured != SftpConflictPolicy.Ask)
            return configured;

        var choices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        choices.Items.Add(new ComboBoxItem
        {
            Content = Loc.T("기존 파일 건너뛰기", "Skip existing files"),
            Tag = SftpConflictPolicy.Skip,
        });
        choices.Items.Add(new ComboBoxItem
        {
            Content = Loc.T("안전하게 덮어쓰기", "Safely overwrite"),
            Tag = SftpConflictPolicy.Overwrite,
        });
        choices.Items.Add(new ComboBoxItem
        {
            Content = Loc.T("새 이름으로 저장", "Keep both with a new name"),
            Tag = SftpConflictPolicy.Rename,
        });
        choices.Items.Add(new ComboBoxItem
        {
            Content = Loc.T("새 파일일 때만 교체", "Replace only when source is newer"),
            Tag = SftpConflictPolicy.NewerOnly,
        });
        choices.SelectedIndex = 0;
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = collision
                ? Loc.T($"'{name}'과(와) 같은 이름의 항목이 이미 있습니다.",
                    $"An item named '{name}' already exists.")
                : Loc.T($"'{name}' 전송 중 같은 이름의 항목이 발견될 경우 적용할 방법을 선택하세요.",
                    $"Choose what to do if a name conflict is found while transferring '{name}'."),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(choices);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("같은 이름 파일 처리", "File conflict policy"),
            Content = content,
            PrimaryButtonText = Loc.T("이 정책으로 전송", "Transfer with this policy"),
            CloseButtonText = Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogSafelyAsync(dialog) != ContentDialogResult.Primary ||
            choices.SelectedItem is not ComboBoxItem { Tag: SftpConflictPolicy policy })
        {
            return null;
        }
        return policy;
    }

    private async Task<bool> ConfirmRecursiveDeleteAsync(
        FileNode node,
        SftpDeletePreview preview)
    {
        var paths = preview.PreviewPaths.Count == 0
            ? Loc.T("(비어 있음)", "(empty)")
            : string.Join(Environment.NewLine, preview.PreviewPaths.Select(path => $"• {path}"));
        var acknowledgement = new CheckBox
        {
            Content = Loc.T("이 폴더와 모든 하위 항목을 영구 삭제함을 이해했습니다.",
                "I understand this permanently deletes the folder and all descendants."),
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = Loc.T(
                $"'{node.FullPath}'에서 파일 {preview.FileCount:N0}개, 폴더 {preview.DirectoryCount:N0}개를 삭제합니다. 총 {FormatBytes(preview.TotalBytes)}입니다.",
                $"Delete {preview.FileCount:N0} file(s) and {preview.DirectoryCount:N0} folder(s) from '{node.FullPath}' ({FormatBytes(preview.TotalBytes)} total)."),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("미리보기 (최대 20개)", "Preview (up to 20 paths)"),
            Foreground = ThemeResources.Brush(this, "TextMuted"),
            FontSize = 11,
        });
        content.Children.Add(new TextBlock
        {
            Text = paths,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 10.5,
            MaxHeight = 180,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(acknowledgement);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("원격 폴더 영구 삭제", "Permanently delete remote folder"),
            Content = content,
            PrimaryButtonText = Loc.T("영구 삭제", "Delete permanently"),
            CloseButtonText = Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };
        acknowledgement.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        acknowledgement.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        return await ShowDialogSafelyAsync(dialog) == ContentDialogResult.Primary;
    }

    private async Task<string?> PromptForNameAsync(string title, string initial, string primaryText)
    {
        var input = new TextBox { Text = initial, MinWidth = 250 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = primaryText,
            CloseButtonText = Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = IsValidRemoteName(initial),
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = IsValidRemoteName(input.Text);
        dialog.Opened += (_, _) =>
        {
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        };
        return await ShowDialogSafelyAsync(dialog) == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }

    private async Task<string?> PromptForRemotePathAsync(string initial)
    {
        var input = new TextBox
        {
            Text = initial,
            Header = Loc.T("이동할 전체 원격 경로", "Full remote destination path"),
            PlaceholderText = "/var/archive/name",
            MinWidth = 360,
            MaxLength = 4_096,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
        };
        var help = new TextBlock
        {
            Text = Loc.T(
                "대상 상위 폴더가 서버에 이미 있어야 하며 기존 항목을 덮어쓰지 않습니다.",
                "The destination parent must already exist; existing entries are never overwritten."),
            Foreground = ThemeResources.Brush(this, "TextMuted"),
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
        };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(input);
        content.Children.Add(help);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("다른 원격 폴더로 이동", "Move to another remote folder"),
            Content = content,
            PrimaryButtonText = Loc.T("이동", "Move"),
            CloseButtonText = Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = IsValidAbsoluteRemotePath(initial),
        };
        input.TextChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = IsValidAbsoluteRemotePath(input.Text);
        dialog.Opened += (_, _) =>
        {
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        };
        return await ShowDialogSafelyAsync(dialog) == ContentDialogResult.Primary
            ? RemotePath.Normalize(input.Text)
            : null;
    }

    private async Task<ContentDialogResult> ShowDialogSafelyAsync(ContentDialog dialog)
    {
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            if (_isAvailable)
                ShowOperationError(Loc.T("대화 상자를 열 수 없습니다", "Could not open the dialog"), ex);
            return ContentDialogResult.None;
        }
    }

    private static bool IsValidRemoteName(string? name)
    {
        var value = name?.Trim();
        return !string.IsNullOrEmpty(value) && value is not "." and not ".." &&
               !value.Contains('/') && !value.Contains('\\');
    }

    private static bool IsValidSearchQuery(string? query)
    {
        var value = query?.Trim() ?? "";
        return value.Length is > 0 and <= 128 && !value.Any(char.IsControl);
    }

    private static bool IsValidAbsoluteRemotePath(string? path)
    {
        var value = path?.Trim() ?? "";
        return value.Length is > 1 and <= 4_096 && value.StartsWith('/') &&
               !value.Contains('\0') && !value.Any(char.IsControl) &&
               RemotePath.Normalize(value) != "/";
    }

    private static bool TryParseUnixMode(string? value, out int unixMode)
    {
        unixMode = 0;
        var normalized = value?.Trim() ?? "";
        if (normalized.Length is < 3 or > 4 || normalized.Any(character => character is < '0' or > '7'))
            return false;
        try
        {
            unixMode = Convert.ToInt32(normalized, 8);
            return unixMode is >= 0 and <= 0x0FFF;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private IProgress<sutty.Core.Sftp.SftpTransferProgress> CreateQueuedTransferProgress(
        SftpQueuedJob job,
        SftpTransferItemVm transfer,
        DurableProgressState state) =>
        new Progress<sutty.Core.Sftp.SftpTransferProgress>(progress =>
        {
            transfer.Report(progress.Fraction);
            state.BytesTransferred = Math.Max(state.BytesTransferred, progress.BytesTransferred);
            state.TotalBytes = Math.Max(state.TotalBytes, progress.TotalBytes);

            // Persist a coarse snapshot for the global Transfer Center without turning
            // each SFTP buffer report into a synchronous disk write on the UI thread.
            var now = DateTimeOffset.UtcNow;
            if (transfer.State != SftpTransferState.Running ||
                now - state.LastPersistedAtUtc < TimeSpan.FromSeconds(1))
            {
                return;
            }

            state.LastPersistedAtUtc = now;
            UpdateQueuedTransfer(
                job,
                SftpQueueTargetState.Running,
                bytesTransferred: state.BytesTransferred,
                totalBytes: state.TotalBytes);
        });

    private sealed class DurableProgressState
    {
        public long BytesTransferred { get; set; }

        public long TotalBytes { get; set; }

        public DateTimeOffset LastPersistedAtUtc { get; set; } = DateTimeOffset.MinValue;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1024d / 1024d:0.#} MB",
        _ => $"{bytes / 1024d / 1024d / 1024d:0.#} GB",
    };

    private static FileNode? NodeFrom(object sender) =>
        (sender as FrameworkElement)?.Tag as FileNode;

    private sealed record RemoteSearchChoice(RemoteTreeEntry Result, string Display);

    private bool IsNodeCurrent(FileNode node) =>
        _isAvailable && FileTree.IsEnabled &&
        node.SessionVersion == _sessionVersion && _sftp is not null;

    private void ShowOperationError(string action, Exception error)
        => ShowStatus($"{action}: {error.Message}");

    private void ShowStatus(string? message)
    {
        StatusText.Text = message ?? "";
        StatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using sutty.Core.Models;
using sutty.Core.Sessions;
using sutty.Core.Sftp;
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
            foreach (var transfer in Transfers.Where(item => !item.CanCancel))
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

        foreach (var file in items.OfType<StorageFile>().Where(file => !string.IsNullOrWhiteSpace(file.Path)))
        {
            var collision = directory.Children.Any(child => child.Name.Equals(file.Name, StringComparison.Ordinal)) ||
                Transfers.Any(item => item.CanCancel &&
                    item.DestinationPath.Equals(RemotePath.Combine(directory.FullPath, file.Name), StringComparison.Ordinal));
            var overwrite = !collision || await ConfirmOverwriteAsync(file.Name);
            if (!_isAvailable || !ReferenceEquals(_sftp, sftp) || sessionVersion != _sessionVersion)
                return;
            if (collision && !overwrite) continue;
            _ = UploadAsync(file, directory.FullPath, overwrite, sftp, sessionVersion);
        }
    }

    private async Task UploadAsync(
        StorageFile file, string remoteDirectory, bool overwrite, ISftpService sftp, int sessionVersion)
    {
        long size = 0;
        try { size = (long)(await file.GetBasicPropertiesAsync()).Size; } catch { }
        if (!ReferenceEquals(_sftp, sftp) || sessionVersion != _sessionVersion) return;

        var destination = RemotePath.Combine(remoteDirectory, file.Name);
        var transfer = TryAddTransfer(file.Name, file.Path, destination, size, SftpTransferDirection.Upload);
        if (transfer is null) return;
        var token = transfer.Token;
        var workerAcquired = false;
        try
        {
            await _transferWorkerGate.WaitAsync(token);
            workerAcquired = true;
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_sftp, sftp) || sessionVersion != _sessionVersion)
                throw new OperationCanceledException(token);
            transfer.Start();
            var progress = new Progress<double>(transfer.Report);
            await sftp.UploadFileAsync(file.Path, remoteDirectory, overwrite, progress, token);
            transfer.Complete();
            if (ReferenceEquals(_sftp, sftp) && sessionVersion == _sessionVersion)
                await NavigateToPathAsync(_currentPath);
        }
        catch (OperationCanceledException) { transfer.MarkCancelled(); }
        catch (Exception ex)
        {
            transfer.Fail(ex.Message);
            if (ReferenceEquals(_sftp, sftp) && sessionVersion == _sessionVersion)
                ShowOperationError(Loc.T("업로드 실패", "Upload failed"), ex);
        }
        finally
        {
            if (workerAcquired) _transferWorkerGate.Release();
            transfer.Dispose();
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { IsFile: true } node || !IsNodeCurrent(node) ||
            _sftp is not { } sftp) return;
        var sessionVersion = _sessionVersion;
        if (OwnerWindowHandle == IntPtr.Zero)
        {
            ShowStatus(Loc.T("다운로드 창을 열 수 없습니다.", "The download picker is not available."));
            return;
        }

        StorageFile? target;
        try
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
            target = await picker.PickSaveFileAsync();
        }
        catch (Exception ex)
        {
            if (_isAvailable && ReferenceEquals(_sftp, sftp) && sessionVersion == _sessionVersion)
                ShowOperationError(Loc.T("다운로드 창을 열 수 없습니다", "Could not open the download picker"), ex);
            return;
        }

        if (target is null || !_isAvailable || !ReferenceEquals(_sftp, sftp) ||
            sessionVersion != _sessionVersion) return;
        if (string.IsNullOrWhiteSpace(target.Path))
        {
            ShowStatus(Loc.T("이 위치는 직접 파일 경로를 제공하지 않아 다운로드할 수 없습니다.",
                "This location does not provide a direct file path for downloads."));
            return;
        }
        _ = DownloadAsync(node, target.Path, sftp, sessionVersion);
    }

    private async Task DownloadAsync(FileNode node, string localPath, ISftpService sftp, int sessionVersion)
    {
        var transfer = TryAddTransfer(
            node.Name, node.FullPath, localPath, node.Entry.Size, SftpTransferDirection.Download);
        if (transfer is null) return;
        var token = transfer.Token;
        var workerAcquired = false;
        try
        {
            await _transferWorkerGate.WaitAsync(token);
            workerAcquired = true;
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_sftp, sftp) || sessionVersion != _sessionVersion)
                throw new OperationCanceledException(token);
            transfer.Start();
            var progress = new Progress<double>(transfer.Report);
            // FileSavePicker is the explicit user overwrite confirmation for this path.
            await sftp.DownloadFileAsync(node.FullPath, localPath, overwrite: true, progress, token);
            transfer.Complete();
        }
        catch (OperationCanceledException) { transfer.MarkCancelled(); }
        catch (Exception ex)
        {
            transfer.Fail(ex.Message);
            if (ReferenceEquals(_sftp, sftp) && sessionVersion == _sessionVersion)
                ShowOperationError(Loc.T("다운로드 실패", "Download failed"), ex);
        }
        finally
        {
            if (workerAcquired) _transferWorkerGate.Release();
            transfer.Dispose();
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
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("원격 항목 삭제", "Delete remote item"),
            Content = node.IsDirectory
                ? Loc.T($"빈 폴더 '{node.Name}'을(를) 삭제할까요? 비어 있지 않으면 삭제되지 않습니다.",
                    $"Delete the empty folder '{node.Name}'? Non-empty folders will not be deleted.")
                : Loc.T($"파일 '{node.Name}'을(를) 삭제할까요? 이 작업은 되돌릴 수 없습니다.",
                    $"Delete '{node.Name}'? This cannot be undone."),
            PrimaryButtonText = Loc.T("삭제", "Delete"),
            CloseButtonText = Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogSafelyAsync(dialog) != ContentDialogResult.Primary) return;
        if (!_isAvailable || token.IsCancellationRequested ||
            !ReferenceEquals(_sftp, sftp) || version != _sessionVersion) return;
        try
        {
            if (node.IsDirectory) await sftp.DeleteDirectoryAsync(node.FullPath, token);
            else await sftp.DeleteFileAsync(node.FullPath, token);
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
            transfer.Cancel();
    }

    private SftpTransferItemVm? TryAddTransfer(
        string name, string source, string destination, long size, SftpTransferDirection direction)
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
        var transfer = new SftpTransferItemVm(name, source, destination, size, direction);
        Transfers.Add(transfer);
        TransfersPanel.Visibility = Visibility.Visible;
        return transfer;
    }

    private async Task<bool> ConfirmOverwriteAsync(string name)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("같은 이름의 파일", "File already exists"),
            Content = Loc.T($"'{name}'을(를) 안전하게 교체할까요?", $"Safely replace '{name}'?"),
            PrimaryButtonText = Loc.T("교체", "Replace"),
            CloseButtonText = Loc.T("건너뛰기", "Skip"),
            DefaultButton = ContentDialogButton.Close,
        };
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

    private static FileNode? NodeFrom(object sender) =>
        (sender as FrameworkElement)?.Tag as FileNode;

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

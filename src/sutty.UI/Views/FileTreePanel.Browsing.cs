using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using sutty.Setting;
using sutty.UI.Helpers;
using sutty.UI.Services;
using sutty.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace sutty.UI.Views;

public sealed partial class FileTreePanel
{
    private readonly FileBrowserNavigationHistory _remoteHistory = new(StringComparer.Ordinal);
    private readonly RemotePathFavoritesStore _pathFavorites = new();
    private const string LocalDragFormat = "sutty/local-browser-copy-v1";
    private const string RemoteDragFormat = "sutty/remote-browser-copy-v1";
    private BrowserDragPayload? _browserDrag;
    private sealed record BrowserDragPayload(string Token, int SessionVersion,
        IReadOnlyList<LocalFileItemViewModel> LocalItems, IReadOnlyList<FileNode> RemoteItems);

    private void ApplyRemoteBrowserView()
    {
        if (RemoteSortBox is null || RootNodes.Count == 0) return;
        var visible = RootNodes[0].Children.Where(node => RemoteHiddenToggle.IsChecked == true || !node.Name.StartsWith('.'));
        var foldersFirst = visible.OrderByDescending(node => node.IsDirectory);
        var descending = RemoteDescendingToggle.IsChecked == true;
        IOrderedEnumerable<FileNode> ordered = RemoteSortBox.SelectedIndex switch
        {
            1 => descending ? foldersFirst.ThenByDescending(node => node.Entry.Size) : foldersFirst.ThenBy(node => node.Entry.Size),
            2 => descending ? foldersFirst.ThenByDescending(node => node.Entry.Modified) : foldersFirst.ThenBy(node => node.Entry.Modified),
            _ => descending ? foldersFirst.ThenByDescending(node => node.Name, StringComparer.OrdinalIgnoreCase) : foldersFirst.ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase),
        };
        RemoteItems.Clear();
        foreach (var node in ordered.ThenBy(node => node.Name, StringComparer.Ordinal)) RemoteItems.Add(node);
        UpdateTransferButtons();
    }

    private void ApplyBrowserNavigationState()
    {
        if (LocalBackButton is null || RemoteBackButton is null) return;
        LocalBackButton.IsEnabled = LocalBrowser.CanGoBack && !LocalBrowser.IsLoading;
        LocalForwardButton.IsEnabled = LocalBrowser.CanGoForward && !LocalBrowser.IsLoading;
        var ready = _isAvailable && _sftp is not null && RootNodes.Count > 0;
        RemoteBackButton.IsEnabled = ready && _remoteHistory.CanGoBack;
        RemoteForwardButton.IsEnabled = ready && _remoteHistory.CanGoForward;
        RemoteFavoritesButton.IsEnabled = ready;
    }

    private void LocalViewOptions_Changed(object sender, RoutedEventArgs e) => ApplyLocalViewOptions();
    private void LocalSort_Changed(object sender, SelectionChangedEventArgs e) => ApplyLocalViewOptions();
    private void ApplyLocalViewOptions()
    {
        if (LocalSortBox is null || LocalDescendingToggle is null || LocalHiddenToggle is null ||
            LocalList is null || LocalStatusText is null) return;
        LocalBrowser.SetViewOptions((FileBrowserSort)Math.Max(0, LocalSortBox.SelectedIndex),
            LocalDescendingToggle.IsChecked == true, LocalHiddenToggle.IsChecked == true);
        ApplyLocalBrowserState();
    }

    private void RemoteViewOptions_Changed(object sender, RoutedEventArgs e) => ApplyRemoteBrowserView();
    private void RemoteSort_Changed(object sender, SelectionChangedEventArgs e) => ApplyRemoteBrowserView();

    private async void LocalBack_Click(object sender, RoutedEventArgs e)
    {
        await LocalBrowser.GoBackAsync();
        ApplyLocalBrowserState();
    }

    private async void LocalForward_Click(object sender, RoutedEventArgs e)
    {
        await LocalBrowser.GoForwardAsync();
        ApplyLocalBrowserState();
    }

    private async void RemoteBack_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteHistory.BackPath is { } path) await NavigateRemoteCoreAsync(path, -1);
    }

    private async void RemoteForward_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteHistory.ForwardPath is { } path) await NavigateRemoteCoreAsync(path, 1);
    }

    private void RemoteFavorites_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _sftp is null) return;
        try
        {
            var identity = CurrentSftpPersistenceId();
            var path = _currentPath;
            var paths = _pathFavorites.GetPaths(identity);
            var favorite = paths.Contains(path, StringComparer.Ordinal);
            var flyout = new MenuFlyout();
            var toggle = new MenuFlyoutItem
            {
                Text = favorite ? Loc.T("현재 폴더 즐겨찾기 해제", "Remove current folder from favorites") :
                    Loc.T("현재 폴더 즐겨찾기 추가", "Add current folder to favorites"),
                Icon = new SymbolIcon(favorite ? Symbol.UnFavorite : Symbol.Favorite),
            };
            toggle.Click += (_, _) =>
            {
                try { _pathFavorites.SetFavorite(identity, path, !favorite); }
                catch (Exception error) { ShowFavoritesError(error); }
            };
            flyout.Items.Add(toggle);
            if (paths.Count > 0) flyout.Items.Add(new MenuFlyoutSeparator());
            foreach (var favoritePath in paths)
            {
                var item = new MenuFlyoutItem { Text = favoritePath };
                item.Click += async (_, _) =>
                {
                    if (_session is not null && identity == CurrentSftpPersistenceId())
                        await NavigateToPathAsync(favoritePath);
                };
                flyout.Items.Add(item);
            }
            flyout.ShowAt(RemoteFavoritesButton);
        }
        catch (Exception error) { ShowFavoritesError(error); }
    }

    private void ShowFavoritesError(Exception error) => ShowOperationError(
        Loc.T("즐겨찾기를 읽거나 저장할 수 없습니다. 로컬 즐겨찾기 파일과 쓰기 권한을 확인하세요",
            "Could not read or save favorites. Check the local favorites file and write permissions"), error);

    private void LocalDragItems_Starting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<LocalFileItemViewModel>().ToArray();
        if (items.Length == 0 || !_isAvailable || _sftp is null) { e.Cancel = true; return; }
        _browserDrag = new(Guid.NewGuid().ToString("N"), _sessionVersion, items, []);
        e.Data.SetData(LocalDragFormat, _browserDrag.Token);
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void RemoteDragItems_Starting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<FileNode>().Where(IsNodeCurrent).ToArray();
        if (items.Length == 0 || !_isAvailable || _sftp is null) { e.Cancel = true; return; }
        _browserDrag = new(Guid.NewGuid().ToString("N"), _sessionVersion, [], items);
        e.Data.SetData(RemoteDragFormat, _browserDrag.Token);
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void BrowserDragItems_Completed(ListViewBase sender, DragItemsCompletedEventArgs args) => _browserDrag = null;

    private async Task<BrowserDragPayload?> ReadBrowserDragAsync(DataPackageView data, string format)
    {
        var payload = _browserDrag;
        if (payload is null || payload.SessionVersion != _sessionVersion || !data.Contains(format)) return null;
        var token = await data.GetDataAsync(format);
        return token is string value && value == payload.Token && payload.SessionVersion == _sessionVersion
            ? payload : null;
    }

    private bool CanAcceptLocalUpload(DataPackageView data) => _isAvailable && _sftp is not null &&
        RootNodes.Count > 0 && (data.Contains(StandardDataFormats.StorageItems) ||
            data.Contains(LocalDragFormat) && _browserDrag is { LocalItems.Count: > 0 } payload && payload.SessionVersion == _sessionVersion);

    private void LocalList_DragOver(object sender, DragEventArgs e)
    {
        if (!_isAvailable || _sftp is null || string.IsNullOrWhiteSpace(LocalBrowser.CurrentPath) ||
            !e.DataView.Contains(RemoteDragFormat) || _browserDrag is not { RemoteItems.Count: > 0 } payload ||
            payload.SessionVersion != _sessionVersion) return;
        if ((sender as FrameworkElement)?.DataContext is LocalFileItemViewModel { IsDirectory: true, IsReparsePoint: true })
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }
        var directory = (sender as FrameworkElement)?.DataContext is LocalFileItemViewModel { IsDirectory: true, IsReparsePoint: false } item
            ? item.FullPath : LocalBrowser.CurrentPath;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Loc.T($"{directory}에 복사", $"Copy to {directory}");
        e.Handled = true;
    }

    private async void LocalList_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled || !e.DataView.Contains(RemoteDragFormat)) return;
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is LocalFileItemViewModel { IsDirectory: true, IsReparsePoint: true })
        {
            ShowLocalStatus(Loc.T("연결된 폴더에는 드롭할 수 없습니다. 실제 폴더를 선택하세요.",
                "Cannot drop into a linked folder. Choose a real folder."));
            return;
        }
        var deferral = e.GetDeferral();
        // Pin the folder at drop time, before awaiting data or a collision dialog.
        var directory = (sender as FrameworkElement)?.DataContext is LocalFileItemViewModel { IsDirectory: true, IsReparsePoint: false } item
            ? item.FullPath : LocalBrowser.CurrentPath;
        try
        {
            var payload = await ReadBrowserDragAsync(e.DataView, RemoteDragFormat);
            if (payload is not null) await QueueRemoteDownloadsAsync(payload.RemoteItems, directory);
        }
        catch (Exception error) { ShowOperationError(Loc.T("드롭한 파일을 내려받을 수 없습니다", "Could not download dropped files"), error); }
        finally { deferral.Complete(); }
    }
}

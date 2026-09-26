using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using sutty.UI.Helpers;
using sutty.UI.Services;
using sutty.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace sutty.UI.Views;

/// <summary>One local tab's browser. Filesystem enumeration uses the existing read-only service.</summary>
public sealed partial class LocalBrowserPanel : UserControl, IDisposable
{
    private string _shellDirectory = "";
    private int _navigationVersion;
    private bool _hasDirectory;
    private bool _disposed;
    private bool _followsShell = true;
    private CancellationTokenSource? _navigationCancellation;

    public LocalFileBrowserViewModel Browser { get; } = new(new LocalFileBrowserService());

    public LocalBrowserPanel()
    {
        InitializeComponent();
        UpdateState();
    }

    public Task FollowWorkingDirectoryAsync(string directory, bool force = false)
    {
        if (_disposed) return Task.CompletedTask;
        _followsShell = true;
        FollowShellButton.Visibility = Visibility.Visible;
        if (!force && string.Equals(_shellDirectory, directory, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        _shellDirectory = directory ?? "";
        if (string.IsNullOrWhiteSpace(_shellDirectory))
        {
            CancelPendingNavigation();
            _hasDirectory = false;
            PathBox.Text = "";
            UpdateState();
            StatusText.Text = Loc.T("현재 셸 폴더를 확인하는 중입니다.", "Waiting for the current shell directory.");
            return Task.CompletedTask;
        }
        PathBox.Text = _shellDirectory;
        var path = _shellDirectory;
        return NavigateAsync(token => Browser.NavigateAsync(path, token), shellNavigation: true);
    }

    /// <summary>Browse this PC even when no shell can report a filesystem directory.</summary>
    public Task ShowLocalPcAsync()
    {
        if (_disposed) return Task.CompletedTask;
        _followsShell = false;
        _shellDirectory = "";
        FollowShellButton.Visibility = Visibility.Collapsed;
        // Refresh preserves manual navigation; the first visit uses the local home folder.
        return NavigateAsync(token => Browser.RefreshAsync(token));
    }

    public void CancelPendingNavigation()
    {
        ++_navigationVersion;
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = null;
        LoadingRing.IsActive = false;
    }

    private async Task NavigateAsync(Func<CancellationToken, Task<bool>> action, bool shellNavigation = false)
    {
        if (_disposed) return;
        CancelPendingNavigation();
        var version = _navigationVersion;
        var cancellation = new CancellationTokenSource();
        _navigationCancellation = cancellation;
        if (shellNavigation) _hasDirectory = false;
        UpdateState();
        LoadingRing.IsActive = true;
        try
        {
            var loaded = await action(cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || version != _navigationVersion) return;
            if (loaded) _hasDirectory = true;
            UpdateState();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_disposed && version == _navigationVersion)
            {
                LoadingRing.IsActive = false;
                StatusText.Text = error.Message;
            }
        }
    }

    public void RefreshLanguage()
    {
        Bindings.Update();
        UpdateState();
    }

    private void UpdateState()
    {
        if (_disposed) return;
        // The view-model can finish cancellation after this panel has switched tabs or
        // cleared an unknown shell directory. Only this panel's current request is loading.
        var loading = _navigationCancellation is { IsCancellationRequested: false } && Browser.IsLoading;
        FileList.Visibility = _hasDirectory ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = _hasDirectory && !loading && Browser.Items.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = loading;
        BackButton.IsEnabled = Browser.CanGoBack && !loading;
        ForwardButton.IsEnabled = Browser.CanGoForward && !loading;
        ParentButton.IsEnabled = _hasDirectory && Browser.CanNavigateParent && !loading;
        if (_hasDirectory) PathBox.Text = Browser.CurrentPath;
        StatusText.Text = _followsShell && !_hasDirectory && string.IsNullOrWhiteSpace(_shellDirectory)
            ? Loc.T("현재 셸 폴더를 확인하는 중입니다.", "Waiting for the current shell directory.")
            : Browser.ErrorMessage ?? "";
    }

    private async void Back_Click(object sender, RoutedEventArgs e) =>
        await NavigateAsync(token => Browser.GoBackAsync(token));
    private async void Forward_Click(object sender, RoutedEventArgs e) =>
        await NavigateAsync(token => Browser.GoForwardAsync(token));
    private async void Parent_Click(object sender, RoutedEventArgs e) =>
        await NavigateAsync(token => Browser.NavigateParentAsync(token));
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_hasDirectory) await NavigateAsync(token => Browser.RefreshAsync(token));
        else if (_followsShell) await FollowWorkingDirectoryAsync(_shellDirectory, force: true);
        else await ShowLocalPcAsync();
    }
    private async void FollowShell_Click(object sender, RoutedEventArgs e) =>
        await FollowWorkingDirectoryAsync(_shellDirectory, force: true);

    private async void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        var path = PathBox.Text;
        await NavigateAsync(token => Browser.NavigateAsync(path, token));
    }

    private void ShowHidden_Click(object sender, RoutedEventArgs e)
    {
        Browser.SetViewOptions(FileBrowserSort.Name, false, ShowHiddenToggle.IsChecked == true);
        UpdateState();
    }

    private async void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await OpenSelectedAsync();
    private async void FileList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await OpenSelectedAsync();
    }

    private async Task OpenSelectedAsync()
    {
        if (FileList.SelectedItem is not LocalFileItemViewModel item) return;
        if (item.IsDirectory)
            await NavigateAsync(token => Browser.NavigateAsync(item.FullPath, token));
        else
        {
            try { await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(item.FullPath)); }
            catch (Exception error) { StatusText.Text = error.Message; }
        }
    }

    private async void OpenExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasDirectory) return;
        try { await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(Browser.CurrentPath)); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void FileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<LocalFileItemViewModel>().ToArray();
        if (items.Length == 0 || !_hasDirectory || _disposed) { e.Cancel = true; return; }
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var storageItems = new List<IStorageItem>();
                foreach (var item in items)
                    storageItems.Add(item.IsDirectory
                        ? await StorageFolder.GetFolderFromPathAsync(item.FullPath)
                        : await StorageFile.GetFileFromPathAsync(item.FullPath));
                request.SetData(storageItems);
            }
            catch (Exception error)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_disposed) StatusText.Text = error.Message;
                });
            }
            finally { deferral.Complete(); }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPendingNavigation();
        Browser.Dispose();
        GC.SuppressFinalize(this);
    }
}

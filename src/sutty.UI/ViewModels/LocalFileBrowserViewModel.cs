using CommunityToolkit.Mvvm.ComponentModel;
using sutty.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace sutty.UI.ViewModels;

/// <summary>
/// Navigation state for the local half of the dual-pane Files workspace. The state
/// contains paths and display metadata only; it is never persisted with a session.
/// </summary>
public sealed class LocalFileBrowserViewModel : ObservableObject, IDisposable
{
    private readonly ILocalFileBrowserService _service;
    private CancellationTokenSource? _navigationCts;
    private int _navigationVersion;
    private string _currentPath = "";
    private bool _isLoading;
    private string? _errorMessage;
    private IReadOnlyList<LocalFileEntry> _entries = [];
    private readonly FileBrowserNavigationHistory _history = new(StringComparer.OrdinalIgnoreCase);
    private FileBrowserSort _sort;
    private bool _descending;
    private bool _showHidden;
    private bool _disposed;

    public LocalFileBrowserViewModel(ILocalFileBrowserService service) =>
        _service = service ?? throw new ArgumentNullException(nameof(service));

    public ObservableCollection<LocalFileItemViewModel> Items { get; } = [];
    public bool CanGoBack => _history.CanGoBack;
    public bool CanGoForward => _history.CanGoForward;

    public void SetViewOptions(FileBrowserSort sort, bool descending, bool showHidden)
    {
        _sort = sort;
        _descending = descending;
        _showHidden = showHidden;
        ApplyView();
    }

    private void ApplyView()
    {
        var visible = _entries.Where(entry => _showHidden || !entry.IsHidden);
        var foldersFirst = visible.OrderByDescending(entry => entry.IsDirectory);
        IOrderedEnumerable<LocalFileEntry> ordered = _sort switch
        {
            FileBrowserSort.Size => _descending ? foldersFirst.ThenByDescending(entry => entry.Size) : foldersFirst.ThenBy(entry => entry.Size),
            FileBrowserSort.Modified => _descending ? foldersFirst.ThenByDescending(entry => entry.Modified) : foldersFirst.ThenBy(entry => entry.Modified),
            _ => _descending ? foldersFirst.ThenByDescending(entry => entry.Name, StringComparer.OrdinalIgnoreCase) : foldersFirst.ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
        };
        Items.Clear();
        foreach (var entry in ordered.ThenBy(entry => entry.Name, StringComparer.Ordinal))
            Items.Add(new LocalFileItemViewModel(entry));
    }

    public Task<bool> GoBackAsync(CancellationToken cancellationToken = default) =>
        _history.BackPath is { } path ? NavigateCoreAsync(path, -1, cancellationToken) : Task.FromResult(false);

    public Task<bool> GoForwardAsync(CancellationToken cancellationToken = default) =>
        _history.ForwardPath is { } path ? NavigateCoreAsync(path, 1, cancellationToken) : Task.FromResult(false);

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
                OnPropertyChanged(nameof(CanNavigateParent));
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool CanNavigateParent => !string.IsNullOrWhiteSpace(CurrentPath) &&
        !string.Equals(
            Path.GetPathRoot(CurrentPath)?.TrimEnd(Path.DirectorySeparatorChar),
            CurrentPath.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await NavigateAsync(_service.GetInitialPath(), cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      ArgumentException or NotSupportedException)
        {
            ErrorMessage = error.Message;
            return false;
        }
    }

    public Task<bool> RefreshAsync(CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(CurrentPath)
            ? InitializeAsync(cancellationToken)
            : NavigateAsync(CurrentPath, cancellationToken);

    public async Task<bool> NavigateParentAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentPath))
            return await InitializeAsync(cancellationToken);
        try
        {
            return await NavigateAsync(_service.GetParentPath(CurrentPath), cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      ArgumentException or NotSupportedException)
        {
            ErrorMessage = error.Message;
            return false;
        }
    }

    public Task<bool> NavigateAsync(
        string path,
        CancellationToken cancellationToken = default) => NavigateCoreAsync(path, 0, cancellationToken);

    private async Task<bool> NavigateCoreAsync(string path, int historyOffset, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelNavigation();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _navigationCts = cts;
        var version = ++_navigationVersion;
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var normalized = _service.NormalizeDirectoryPath(path);
            var entries = await _service.ListDirectoryAsync(normalized, cts.Token);
            if (cts.IsCancellationRequested || version != _navigationVersion)
                return false;

            _entries = entries;
            ApplyView();
            CurrentPath = normalized;
            _history.Record(normalized, historyOffset);
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      ArgumentException or NotSupportedException)
        {
            if (version == _navigationVersion)
                ErrorMessage = error.Message;
            return false;
        }
        finally
        {
            if (version == _navigationVersion)
                IsLoading = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _navigationVersion++;
        CancelNavigation();
        IsLoading = false;
        GC.SuppressFinalize(this);
    }

    private void CancelNavigation()
    {
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = null;
    }
}

public sealed class LocalFileItemViewModel
{
    public LocalFileItemViewModel(LocalFileEntry entry) =>
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));

    public LocalFileEntry Entry { get; }
    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
    public bool IsDirectory => Entry.IsDirectory;
    public bool IsFile => !Entry.IsDirectory;
    public long Size => Entry.Size;
    public string SizeText => IsDirectory ? "" : FormatSize(Entry.Size);
    public string ModifiedText => Entry.Modified.ToString("yyyy-MM-dd HH:mm");
    public string TypeText => IsDirectory
        ? "DIR"
        : string.IsNullOrWhiteSpace(Path.GetExtension(Name))
            ? "FILE"
            : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
    public bool IsReparsePoint => Entry.IsReparsePoint;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.#} GB",
    };
}

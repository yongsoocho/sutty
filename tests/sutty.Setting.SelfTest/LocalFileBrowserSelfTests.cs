using sutty.UI.Services;
using sutty.UI.ViewModels;

internal static class LocalFileBrowserSelfTests
{
    public static async Task RunAsync(string scratch)
    {
        var root = Path.Combine(scratch, "local-browser");
        var folder = Path.Combine(root, "folder-a");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(root, "file-b.txt"), "sutty");

        var service = new LocalFileBrowserService();
        var normalized = service.NormalizeDirectoryPath(root);
        var entries = await service.ListDirectoryAsync(normalized);
        Assert(entries.Count == 2, "local browser lists files and folders");
        Assert(entries[0].IsDirectory && entries[0].Name == "folder-a",
            "local browser sorts folders before files");
        Assert(!entries[1].IsDirectory && entries[1].Size == 5,
            "local browser exposes file size without file content");
        Assert(service.GetParentPath(folder) == normalized,
            "local browser resolves parent navigation");
        Assert(LocalFilePathRules.TryResolveDirectChild(root, "safe.txt", out var safePath) &&
               Path.GetDirectoryName(safePath) == normalized,
            "remote download path remains a direct local child");
        Assert(!LocalFilePathRules.TryResolveDirectChild(root, "..", out _) &&
               !LocalFilePathRules.TryResolveDirectChild(root, "folder/escape.txt", out _),
            "remote download path rejects traversal and separators");
        Assert(!LocalFilePathRules.TryResolveDirectChild(root, "CON", out _) &&
               !LocalFilePathRules.TryResolveDirectChild(root, "con.txt", out _) &&
               !LocalFilePathRules.TryResolveDirectChild(root, "trailing.", out _) &&
               !LocalFilePathRules.TryResolveDirectChild(root, "trailing ", out _),
            "remote download path rejects Windows devices and normalized trailing names");

        using var viewModel = new LocalFileBrowserViewModel(service);
        Assert(await viewModel.NavigateAsync(root), "local browser view-model navigation");
        Assert(viewModel.CurrentPath == normalized && viewModel.Items.Count == 2,
            "local browser view-model publishes one directory snapshot");
        Assert(!await viewModel.NavigateAsync("relative-folder"),
            "local browser rejects relative paths");
        Assert(viewModel.CurrentPath == normalized && viewModel.Items.Count == 2,
            "failed navigation preserves the last good snapshot");

        await VerifyViewAndHistoryAsync(service, root, folder);
        await VerifyCancellationAndSupersededNavigationAsync(root);
        VerifyRemoteFavorites(scratch);

        Console.WriteLine("Local file browser self-tests passed.");
    }

    private static async Task VerifyViewAndHistoryAsync(LocalFileBrowserService service, string root, string folder)
    {
        var secondFolder = Path.Combine(root, "folder-z");
        Directory.CreateDirectory(secondFolder);
        var largeFile = Path.Combine(root, "a-large.txt");
        await File.WriteAllTextAsync(largeFile, "12345678901234567890");
        File.SetLastWriteTime(largeFile, new DateTime(2020, 1, 1));
        await File.WriteAllTextAsync(Path.Combine(root, ".hidden"), "hidden");
        using var browser = new LocalFileBrowserViewModel(service);
        await browser.NavigateAsync(root);
        Assert(browser.Items.Count == 4 && browser.Items.All(item => item.Name != ".hidden"),
            "hidden entries are filtered without altering the folder snapshot");
        browser.SetViewOptions(FileBrowserSort.Size, descending: true, showHidden: false);
        Assert(browser.Items.Take(2).All(item => item.IsDirectory) && browser.Items[2].Name == "a-large.txt",
            "size descending retains folders first and orders file sizes");
        browser.SetViewOptions(FileBrowserSort.Modified, descending: false, showHidden: true);
        Assert(browser.Items.Count == 5 && browser.Items[2].Name == "a-large.txt",
            "modified sorting and hidden toggle reveal the same complete snapshot");
        browser.SetViewOptions(FileBrowserSort.Name, descending: true, showHidden: false);
        Assert(browser.Items[0].Name == "folder-z" && browser.Items[2].Name == "file-b.txt",
            "descending names sort within folders and files");

        await browser.NavigateAsync(folder);
        Assert(browser.CanGoBack && !browser.CanGoForward, "successful navigation adds a history entry");
        await browser.GoBackAsync();
        Assert(browser.CurrentPath == root && browser.CanGoForward, "back navigation restores previous directory");
        await browser.RefreshAsync();
        Assert(browser.CanGoForward, "refresh does not discard the forward history");
        await browser.NavigateAsync(Path.Combine(root, "not-present"));
        Assert(browser.CanGoForward && browser.CurrentPath == root, "failed navigation leaves the history cursor unchanged");
        await browser.GoForwardAsync();
        Assert(browser.CurrentPath == folder, "forward remains available after refresh or failure");
        await browser.GoBackAsync();
        await browser.NavigateAsync(secondFolder);
        Assert(!browser.CanGoForward, "a new branch discards only the old forward history");

        var history = new FileBrowserNavigationHistory(StringComparer.Ordinal);
        for (var index = 0; index < 120; index++) history.Record($"/folder-{index}");
        var count = 0;
        while (history.BackPath is { } previous) { history.Record(previous, -1); count++; }
        Assert(count == 99, "history retains at most 100 successful folder locations");
    }

    private static async Task VerifyCancellationAndSupersededNavigationAsync(string root)
    {
        var service = new DelayedBrowserService(root);
        using var browser = new LocalFileBrowserViewModel(service);
        await browser.NavigateAsync(root);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert(!await browser.NavigateAsync(Path.Combine(root, "canceled"), canceled.Token) && browser.CurrentPath == root,
            "cancellation preserves the last successful snapshot and does not create history");
        var stale = browser.NavigateAsync(Path.Combine(root, "delayed"));
        var latest = Path.Combine(root, "latest");
        await browser.NavigateAsync(latest);
        service.CompleteDelayed();
        Assert(!await stale && browser.CurrentPath == latest && browser.Items[0].Name == "latest",
            "a late completion cannot replace a newer folder selection");

        service.ResetDelayed();
        var disposed = browser.NavigateAsync(Path.Combine(root, "delayed"));
        browser.Dispose();
        service.CompleteDelayed();
        Assert(!await disposed && browser.CurrentPath == latest && !browser.IsLoading,
            "shutdown cancels owned navigation and blocks late UI publication");
    }

    private static void VerifyRemoteFavorites(string scratch)
    {
        var path = Path.Combine(scratch, "favorite-folders.json");
        var store = new sutty.Setting.RemotePathFavoritesStore(path);
        Assert(store.GetPaths("profile:a").Count == 0, "favorites start empty without a settings migration");
        store.SetFavorite("profile:a", "/var/한글 공백", true);
        store.SetFavorite("profile:a", "/var/한글 공백", true);
        store.SetFavorite("profile:b", "/srv/project", true);
        var reloaded = new sutty.Setting.RemotePathFavoritesStore(path);
        Assert(reloaded.GetPaths("profile:a").SequenceEqual(new[] { "/var/한글 공백" }) &&
               reloaded.GetPaths("profile:b").SequenceEqual(new[] { "/srv/project" }),
            "favorites persist separately per host, preserve Unicode, and deduplicate paths");
        reloaded.SetFavorite("profile:a", "/var/한글 공백", false);
        Assert(reloaded.GetPaths("profile:a").Count == 0 && reloaded.GetPaths("profile:b").Count == 1,
            "removing a favorite leaves other hosts intact");
        var saved = File.ReadAllText(path);
        AssertThrows<InvalidDataException>(() => reloaded.SetFavorite("profile:a", "relative/path", true),
            "favorite paths must be absolute");
        Assert(File.ReadAllText(path) == saved, "invalid favorite input preserves the existing document");
        const string newer = "{\"schemaVersion\":99,\"hosts\":{}}";
        File.WriteAllText(path, newer);
        AssertThrows<InvalidDataException>(() => reloaded.SetFavorite("profile:a", "/safe", true),
            "future favorite schemas fail closed");
        Assert(File.ReadAllText(path) == newer, "an unsupported favorites file is not overwritten");
        const string damaged = "{broken";
        File.WriteAllText(path, damaged);
        AssertThrows<System.Text.Json.JsonException>(() => reloaded.SetFavorite("profile:a", "/safe", true),
            "damaged favorites remain recoverable");
        Assert(File.ReadAllText(path) == damaged, "damaged favorites are preserved verbatim");
    }

    private static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Self-test failed: {message}.");
    }

    private sealed class DelayedBrowserService(string root) : ILocalFileBrowserService
    {
        private TaskCompletionSource<IReadOnlyList<LocalFileEntry>> _delayed = NewCompletion();
        public string GetInitialPath() => root;
        public string NormalizeDirectoryPath(string path) => path;
        public string GetParentPath(string path) => root;
        public Task<IReadOnlyList<LocalFileEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Path.GetFileName(path) == "delayed" ? _delayed.Task :
                Task.FromResult<IReadOnlyList<LocalFileEntry>>([new(Path.GetFileName(path), path, false, 1, DateTime.UtcNow, false)]);
        }
        public void CompleteDelayed() => _delayed.SetResult([new("delayed", root, false, 2, DateTime.UtcNow, false)]);
        public void ResetDelayed() => _delayed = NewCompletion();
        private static TaskCompletionSource<IReadOnlyList<LocalFileEntry>> NewCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"Self-test failed: {name}.");
    }
}

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

        Console.WriteLine("Local file browser self-tests passed.");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"Self-test failed: {name}.");
    }
}

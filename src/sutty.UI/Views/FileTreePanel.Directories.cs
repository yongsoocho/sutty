using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Sessions;
using sutty.UI.Helpers;
using sutty.UI.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace sutty.UI.Views;

public sealed partial class FileTreePanel
{
    private sealed class BrowserLocation
    {
        public string LocalPath = "";
        public string RemotePath = "/";
        public readonly HashSet<string> LocalExpanded = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> RemoteExpanded = new(StringComparer.Ordinal);
    }

    private sealed record DirectoryBranch(string Path);
    private const int DirectoryPageSize = 256;
    private sealed record DirectoryPage((string Name, string Path)[] Entries, int Offset);
    private readonly ConditionalWeakTable<TreeViewNode, DirectoryPage> _directoryPages = new();
    private readonly ConditionalWeakTable<ISshSession, BrowserLocation> _locations = new();
    private readonly ConditionalWeakTable<TreeViewNode, DirectoryBranch> _directoryBranches = new();
    private readonly HashSet<TreeViewNode> _loadingDirectories = [];
    private readonly LocalFileBrowserService _localDirectories = new();
    private BrowserLocation _location = new();
    private bool _clearingDirectoryRoot;

    private TreeViewNode DirectoryNode(string name, string path)
    {
        var node = new TreeViewNode { Content = name, HasUnrealizedChildren = true };
        _directoryBranches.Add(node, new DirectoryBranch(path));
        return node;
    }

    private void ClearDirectoryRoot(TreeView tree)
    {
        _clearingDirectoryRoot = true;
        try { tree.RootNodes.Clear(); }
        finally { _clearingDirectoryRoot = false; }
    }

    private void RefreshDirectoryRoot(TreeView tree)
    {
        if (tree.RootNodes.Count == 0 || !_directoryBranches.TryGetValue(tree.RootNodes[0], out var branch)) return;
        ClearDirectoryRoot(tree);
        SetDirectoryRoot(tree, branch.Path);
    }

    private void SetDirectoryRoot(TreeView tree, string? path)
    {
        if (string.IsNullOrEmpty(path)) { ClearDirectoryRoot(tree); return; }
        var local = tree == LocalDirectoryTree;
        var expanded = local ? _location.LocalExpanded : _location.RemoteExpanded;
        if (tree.RootNodes.Count == 1 && _directoryBranches.TryGetValue(tree.RootNodes[0], out var current) &&
            string.Equals(path, current.Path, local ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return;
        ClearDirectoryRoot(tree);
        var root = DirectoryNode(path, path);
        tree.RootNodes.Add(root);
        if (expanded.Contains(path)) root.IsExpanded = true;
    }

    private async void DirectoryTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        var node = args.Node;
        if (node is null || !_directoryBranches.TryGetValue(node, out var branch)) return;
        var local = sender == LocalDirectoryTree;
        var expanded = local ? _location.LocalExpanded : _location.RemoteExpanded;
        expanded.Add(branch.Path);
        if (!node.HasUnrealizedChildren || !_loadingDirectories.Add(node)) return;
        var version = _sessionVersion;
        var token = _sessionCts.Token;
        var root = sender.RootNodes.FirstOrDefault();
        bool Current() => version == _sessionVersion && !token.IsCancellationRequested &&
            root is not null && sender.RootNodes.Contains(root);
        try
        {
            IEnumerable<(string Name, string Path)> children;
            if (local)
            {
                var entries = await _localDirectories.ListDirectoryAsync(branch.Path, token);
                children = entries.Where(entry => entry.IsDirectory && !entry.IsReparsePoint &&
                    (!entry.IsHidden || LocalHiddenToggle.IsChecked == true)).Select(entry => (entry.Name, entry.FullPath));
            }
            else
            {
                if (_sftp is not { } sftp) return;
                var entries = await sftp.ListDirectoryAsync(branch.Path, token);
                children = entries.Where(entry => entry.IsDirectory && !entry.IsSymbolicLink &&
                    (!entry.Name.StartsWith('.') || RemoteHiddenToggle.IsChecked == true)).Select(entry => (entry.Name, entry.FullPath));
            }
            if (!Current()) return;
            node.Children.Clear();
            node.HasUnrealizedChildren = false;
            var ordered = children.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
            AppendDirectoryPage(node, new DirectoryPage(ordered, 0), expanded);
            SelectLoadedDirectory(sender, local ? _location.LocalPath : _location.RemotePath);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!Current()) return;
            node.Children.Clear();
            node.Children.Add(new TreeViewNode { Content = Loc.T("폴더를 읽을 수 없음", "Cannot read folder") + " · " + error.Message });
            node.HasUnrealizedChildren = true;
        }
        finally { _loadingDirectories.Remove(node); }
    }

    private void AppendDirectoryPage(TreeViewNode parent, DirectoryPage page, HashSet<string> expanded)
    {
        var end = Math.Min(page.Offset + DirectoryPageSize, page.Entries.Length);
        for (var index = page.Offset; index < end; index++)
        {
            var (name, path) = page.Entries[index];
            var child = DirectoryNode(name, path);
            parent.Children.Add(child);
            if (expanded.Contains(path)) child.IsExpanded = true;
        }
        if (end >= page.Entries.Length) return;
        var count = Math.Min(DirectoryPageSize, page.Entries.Length - end);
        var more = new TreeViewNode { Content = Loc.T($"다음 폴더 {count}개 표시", $"Show next {count} folders") };
        _directoryPages.Add(more, page with { Offset = end });
        parent.Children.Add(more);
    }

    private void DirectoryTree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (!_clearingDirectoryRoot && args.Node is { } node && _directoryBranches.TryGetValue(node, out var branch))
            (sender == LocalDirectoryTree ? _location.LocalExpanded : _location.RemoteExpanded).Remove(branch.Path);
    }

    private async void DirectoryTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode node) return;
        if (_directoryPages.TryGetValue(node, out var page) && node.Parent is { } parent)
        {
            parent.Children.Remove(node);
            var local = sender == LocalDirectoryTree;
            AppendDirectoryPage(parent, page, local ? _location.LocalExpanded : _location.RemoteExpanded);
            SelectLoadedDirectory(sender, local ? _location.LocalPath : _location.RemotePath);
            return;
        }
        if (!_directoryBranches.TryGetValue(node, out var branch)) return;
        if (sender == LocalDirectoryTree) await NavigateLocalAsync(branch.Path);
        else await NavigateToPathAsync(branch.Path);
    }

    private void SelectLoadedDirectory(TreeView tree, string path)
    {
        var comparison = tree == LocalDirectoryTree ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var pending = new Stack<TreeViewNode>(tree.RootNodes);
        while (pending.TryPop(out var node))
        {
            if (_directoryBranches.TryGetValue(node, out var branch) && string.Equals(path, branch.Path, comparison))
            {
                tree.SelectedNode = node;
                return;
            }
            if (node.IsExpanded)
                foreach (var child in node.Children) pending.Push(child);
        }
        tree.SelectedNode = null;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Sftp;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace sutty.UI.Views;

public sealed partial class FileTreePanel
{
    private async void CompareFolders_Click(object sender, RoutedEventArgs e)
    {
        if (RootNodes.Count == 0 || LocalBrowser.IsLoading || LoadingRing.IsActive) return;
        var root = RootNodes[0];
        var version = _navigationVersion;
        var localPath = LocalBrowser.CurrentPath;
        var remotePath = _currentPath;
        bool Current() => _isAvailable && version == _navigationVersion && RootNodes.Count > 0 &&
            ReferenceEquals(RootNodes[0], root) && LocalBrowser.CurrentPath == localPath &&
            !LocalBrowser.IsLoading && !LoadingRing.IsActive;
        try
        {
            var local = LocalBrowser.Items.Select(item => new DirectoryEntryMetadata(item.Name,
                item.IsReparsePoint ? DirectoryEntryKind.Other : item.IsDirectory ? DirectoryEntryKind.Directory : DirectoryEntryKind.File,
                item.Size, item.Entry.Modified)).ToArray();
            var remote = RemoteItems.Select(item => new DirectoryEntryMetadata(item.Name,
                item.Entry.IsSymbolicLink ? DirectoryEntryKind.Other : item.IsDirectory ? DirectoryEntryKind.Directory :
                item.Entry.IsRegularFile ? DirectoryEntryKind.File : DirectoryEntryKind.Other, item.Entry.Size, item.Entry.Modified)).ToArray();
            var rows = await Task.Run(() => DirectoryComparison.Compare(local, remote));
            if (!Current()) return;

            var list = new ListView { Height = 256, SelectionMode = ListViewSelectionMode.Single };
            var changesOnly = new CheckBox { Content = Loc.T("차이와 미확인 항목만", "Differences and unverified items only"), IsChecked = true };
            var status = new TextBlock();
            var pane = new StackPanel { Spacing = 6 };
            pane.Children.Add(new TextBlock { Text = localPath + "\n" + remotePath, TextWrapping = TextWrapping.Wrap });
            pane.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap,
                Text = Loc.T("현재 표시된 이름·크기·수정 시각(초)을 비교합니다. 하위 폴더와 파일 내용은 검사하지 않습니다.",
                    "Compares visible names, sizes and modification times to the second. Folder contents and file contents are not checked.") });
            pane.Children.Add(changesOnly);
            pane.Children.Add(status);
            pane.Children.Add(list);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = Loc.T("현재 폴더 비교", "Compare current folders"), Content = pane,
                PrimaryButtonText = Loc.T("패널에서 선택", "Select in panes"), CloseButtonText = Loc.T("닫기", "Close"),
                DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false,
            };
            IReadOnlyList<DirectoryComparisonRow> visible = [];
            void UpdateRows()
            {
                visible = changesOnly.IsChecked == true
                    ? rows.Where(row => row.Match is not (DirectoryMatch.SameMetadata or DirectoryMatch.Folder)).ToArray() : rows;
                list.ItemsSource = visible.Select(row => $"{MatchLabel(row.Match)}  ·  {row.Name}").ToArray();
                list.SelectedIndex = -1;
                dialog.IsPrimaryButtonEnabled = false;
                status.Text = Loc.T($"표시 {visible.Count:N0} / 전체 {rows.Count:N0}", $"Showing {visible.Count:N0} of {rows.Count:N0}");
            }
            changesOnly.Click += (_, _) => UpdateRows();
            list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedIndex >= 0;
            UpdateRows();
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedIndex < 0 || !Current()) return;
            var name = visible[list.SelectedIndex].Name;
            var selectedLocal = LocalBrowser.Items.FirstOrDefault(item => item.Name == name);
            var selectedRemote = RemoteItems.FirstOrDefault(item => item.Name == name);
            LocalList.SelectedItems.Clear();
            FileTree.SelectedItems.Clear();
            if (selectedLocal is not null) { LocalList.SelectedItems.Add(selectedLocal); LocalList.ScrollIntoView(selectedLocal); }
            if (selectedRemote is not null) { FileTree.SelectedItems.Add(selectedRemote); FileTree.ScrollIntoView(selectedRemote); }
        }
        catch (Exception error) { ShowOperationError(Loc.T("폴더 비교 실패", "Folder comparison failed"), error); }
    }

    private static string MatchLabel(DirectoryMatch match) => match switch
    {
        DirectoryMatch.OnlyLocal => Loc.T("로컬에만 있음", "Only local"),
        DirectoryMatch.OnlyRemote => Loc.T("원격에만 있음", "Only remote"),
        DirectoryMatch.Different => Loc.T("다름", "Different"),
        DirectoryMatch.SameMetadata => Loc.T("크기·시각 일치", "Same size and time"),
        DirectoryMatch.Folder => Loc.T("양쪽 폴더", "Folder on both sides"),
        _ => Loc.T("미확인", "Unverified"),
    };
}

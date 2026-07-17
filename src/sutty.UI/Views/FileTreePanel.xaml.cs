using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Models;
using sutty.Core.Sessions;
using sutty.Core.Sftp;
using sutty.UI.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace sutty.UI.Views
{
    /// <summary>
    /// 연결된 서버의 파일 트리를 보여주는 오른쪽 패널 (FileZilla의 리모트 트리에 해당).
    /// - 디렉터리는 처음 펼칠 때 서버에서 읽어오는 지연 로딩
    /// - 탐색기에서 파일을 끌어다 놓으면 해당 위치로 업로드 (진행도 + 취소 지원)
    /// 활성 세션이 없으면 데모(mock) 서버 트리를 보여준다.
    /// </summary>
    public sealed partial class FileTreePanel : UserControl
    {
        public ObservableCollection<FileNode> RootNodes { get; } = [];

        private ISftpService? _sftp;

        public FileTreePanel()
        {
            InitializeComponent();
        }

        public async Task LoadAsync(ISshSession? session)
        {
            // 활성 세션이 없으면 데모(mock) 트리를 보여준다
            _sftp = session?.Sftp ?? new MockSftpService("demo");

            ShowStatus(null);
            LoadingRing.IsActive = true;
            RootNodes.Clear();

            var root = new FileNode(new RemoteFileEntry { Name = "/", FullPath = "/", IsDirectory = true });
            try
            {
                await LoadChildrenAsync(root);
                root.IsExpanded = true;
                RootNodes.Add(root);
            }
            catch (Exception ex)
            {
                ShowStatus($"Failed to list files: {ex.Message}");
            }
            LoadingRing.IsActive = false;
        }

        private void ShowStatus(string? message)
        {
            StatusText.Text = message ?? "";
            StatusText.Visibility = string.IsNullOrEmpty(message)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private async Task LoadChildrenAsync(FileNode dir)
        {
            if (_sftp is null) return;

            dir.Children.Clear();
            foreach (var entry in await _sftp.ListDirectoryAsync(dir.FullPath))
            {
                dir.Children.Add(new FileNode(entry)
                {
                    Parent = dir,
                    HasUnrealizedChildren = entry.IsDirectory, // 펼칠 때 로딩
                });
            }
        }

        // ── 지연 로딩: 디렉터리를 처음 펼칠 때만 서버에서 읽는다 ──

        private async void FileTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
        {
            if (args.Item is not FileNode node || !node.HasUnrealizedChildren)
                return;

            node.HasUnrealizedChildren = false; // 중복 로딩 방지
            try
            {
                await LoadChildrenAsync(node);
            }
            catch
            {
                node.HasUnrealizedChildren = true; // 실패하면 다시 시도할 수 있게
            }
        }

        // ── 드래그 & 드롭 업로드 ──

        private void Node_DragOver(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

            e.AcceptedOperation = DataPackageOperation.Copy;
            if ((sender as FrameworkElement)?.DataContext is FileNode node)
                e.DragUIOverride.Caption = $"Upload to {node.DirectoryPath}";
            e.Handled = true;
        }

        private async void Node_Drop(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

            e.Handled = true;
            await HandleDropAsync(e, (sender as FrameworkElement)?.DataContext as FileNode);
        }

        // 노드가 아닌 빈 공간에 떨어지면 루트(/)로 업로드
        private void Tree_DragOver(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Upload to /";
        }

        private async void Tree_Drop(object sender, DragEventArgs e)
        {
            if (e.Handled || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            await HandleDropAsync(e, null);
        }

        private async Task HandleDropAsync(DragEventArgs e, FileNode? targetNode)
        {
            if (_sftp is null || RootNodes.Count == 0) return;

            var items = await e.DataView.GetStorageItemsAsync();

            // 대상 디렉터리 노드: 파일 위에 떨어지면 그 부모 디렉터리
            var dirNode = targetNode is null ? RootNodes[0]
                : targetNode.IsDirectory ? targetNode
                : targetNode.Parent ?? RootNodes[0];

            // 아직 안 펼친 디렉터리면 먼저 내용을 읽어온다
            if (dirNode.HasUnrealizedChildren)
            {
                dirNode.HasUnrealizedChildren = false;
                await LoadChildrenAsync(dirNode);
            }
            dirNode.IsExpanded = true;

            foreach (var item in items)
            {
                if (item is StorageFile file && !string.IsNullOrEmpty(file.Path))
                    _ = UploadAsync(file, dirNode); // 파일마다 병렬 업로드, 각자 진행도 표시
            }
        }

        private async Task UploadAsync(StorageFile file, FileNode dirNode)
        {
            if (_sftp is null) return;

            long size = 0;
            try { size = (long)(await file.GetBasicPropertiesAsync()).Size; } catch { /* 크기 모름 */ }

            var cts = new CancellationTokenSource();
            var node = new FileNode(new RemoteFileEntry
            {
                Name = file.Name,
                FullPath = RemotePath.Combine(dirNode.FullPath, file.Name),
                Size = size,
                Modified = DateTime.Now,
            })
            {
                Parent = dirNode,
                IsUploading = true,
                UploadCts = cts,
            };

            // 같은 이름 파일이 이미 보이면 교체 (덮어쓰기)
            var existing = dirNode.Children.FirstOrDefault(c => !c.IsDirectory && c.Name == file.Name);
            if (existing is not null)
                dirNode.Children.Remove(existing);
            dirNode.Children.Add(node);

            try
            {
                // Progress<T>는 UI 스레드에서 만들었으므로 콜백도 UI 스레드로 온다
                var progress = new Progress<double>(p => node.Progress = p);
                await _sftp.UploadFileAsync(file.Path, dirNode.FullPath, progress, cts.Token);

                node.Progress = 1;
                node.IsUploading = false;
            }
            catch (OperationCanceledException)
            {
                dirNode.Children.Remove(node); // 취소 → 목록에서 제거
            }
            catch (Exception ex)
            {
                dirNode.Children.Remove(node);
                ShowStatus($"Upload failed: {ex.Message}");
            }
            finally
            {
                node.UploadCts = null;
                cts.Dispose();
            }
        }

        private void CancelUpload_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is FileNode node)
                node.UploadCts?.Cancel();
        }
    }
}

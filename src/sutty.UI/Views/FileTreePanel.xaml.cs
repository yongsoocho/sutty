using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Models;
using sutty.Core.Sessions;
using sutty.Core.Sftp;
using sutty.UI.Helpers;
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
    /// 연결된 서버의 파일 트리를 터미널 옆 sidecar 패널로 보여준다.
    /// - 디렉터리는 처음 펼칠 때 서버에서 읽어오는 지연 로딩
    /// - 탐색기에서 파일을 끌어다 놓으면 해당 위치로 업로드 (진행도 + 취소 지원)
    /// 활성 세션이 없으면 "세션 없음" 안내만 보여준다.
    /// </summary>
    public sealed partial class FileTreePanel : UserControl
    {
        public void RefreshLanguage()
        {
            Bindings.Update();
            if (_session is not { } session)
                return;

            if (session.State == SessionState.Failed)
            {
                ShowStatus(Loc.T("SSH 연결에 실패했습니다.", "SSH connection failed."));
            }
            else if (session.State == SessionState.Connected &&
                     session.SftpState == SftpConnectionState.Connecting)
            {
                ShowStatus(Loc.T("SFTP에 연결하는 중입니다...", "Connecting to SFTP..."));
            }
            else if (session.State == SessionState.Connected &&
                     session.SftpState == SftpConnectionState.Unavailable &&
                     string.IsNullOrWhiteSpace(session.LastSftpError))
            {
                ShowStatus(Loc.T("서버에서 SFTP subsystem을 사용할 수 없습니다.",
                    "The SFTP subsystem is unavailable on this server."));
            }
        }

        public ObservableCollection<FileNode> RootNodes { get; } = [];

        private ISftpService? _sftp;
        private ISshSession? _session;

        public FileTreePanel()
        {
            InitializeComponent();
            Unloaded += (_, _) => DetachSession();
        }

        /// <summary>
        /// 활성 세션의 서버 파일 트리를 보여준다.
        /// 세션이 없으면 안내 문구를, 아직 연결 중이면 연결 완료 후 로드한다.
        /// </summary>
        public async Task LoadAsync(ISshSession? session)
        {
            // 이전 세션의 연결 대기 콜백 정리
            DetachSession();
            _session = session;

            if (session is null)
            {
                _sftp = null;
                RootNodes.Clear();
                ShowStatus(null);
                LoadingRing.IsActive = false;
                SftpUnavailableState.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = Visibility.Visible;
                return;
            }

            EmptyState.Visibility = Visibility.Collapsed;
            SftpUnavailableState.Visibility = Visibility.Collapsed;
            session.StateChanged += OnSessionStateChanged;
            session.SftpStateChanged += OnSftpStateChanged;

            if (session.State != SessionState.Connected)
            {
                // 연결되면 다시 로드
                _sftp = null;
                RootNodes.Clear();
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
                    await LoadTreeAsync(rootName: "/");
                    break;

                case SftpConnectionState.Unavailable:
                    _sftp = null;
                    RootNodes.Clear();
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
                    LoadingRing.IsActive = true;
                    ShowStatus(Loc.T("SFTP에 연결하는 중입니다...", "Connecting to SFTP..."));
                    break;
            }
        }

        private void OnSessionStateChanged(object? sender, SessionState state)
        {
            if (sender is not ISshSession session || session != _session) return;
            DispatcherQueue.TryEnqueue(() => _ = LoadAsync(session));
        }

        private void OnSftpStateChanged(object? sender, SftpConnectionState state)
        {
            if (sender is not ISshSession session || session != _session) return;
            DispatcherQueue.TryEnqueue(() => _ = LoadAsync(session));
        }

        private void DetachSession()
        {
            if (_session is null) return;
            _session.StateChanged -= OnSessionStateChanged;
            _session.SftpStateChanged -= OnSftpStateChanged;
        }

        private async Task LoadTreeAsync(string rootName)
        {
            var sftp = _sftp;
            if (sftp is null) return;

            ShowStatus(null);
            LoadingRing.IsActive = true;
            RootNodes.Clear();

            var root = new FileNode(new RemoteFileEntry { Name = rootName, FullPath = "/", IsDirectory = true });
            try
            {
                await LoadChildrenAsync(root, sftp);
                if (!ReferenceEquals(_sftp, sftp)) return;
                root.IsExpanded = true;
                RootNodes.Add(root);
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_sftp, sftp))
                    ShowStatus($"Failed to list files: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_sftp, sftp))
                    LoadingRing.IsActive = false;
            }
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
            var sftp = _sftp;
            if (sftp is null) return;
            await LoadChildrenAsync(dir, sftp);
        }

        private async Task LoadChildrenAsync(FileNode dir, ISftpService sftp)
        {
            var entries = await sftp.ListDirectoryAsync(dir.FullPath);
            if (!ReferenceEquals(_sftp, sftp)) return;

            dir.Children.Clear();
            foreach (var entry in entries)
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
            var sftp = _sftp;
            if (sftp is null || RootNodes.Count == 0) return;

            var items = await e.DataView.GetStorageItemsAsync();
            if (!ReferenceEquals(_sftp, sftp) || RootNodes.Count == 0) return;

            // 대상 디렉터리 노드: 파일 위에 떨어지면 그 부모 디렉터리
            var dirNode = targetNode is null ? RootNodes[0]
                : targetNode.IsDirectory ? targetNode
                : targetNode.Parent ?? RootNodes[0];

            // 아직 안 펼친 디렉터리면 먼저 내용을 읽어온다
            if (dirNode.HasUnrealizedChildren)
            {
                dirNode.HasUnrealizedChildren = false;
                await LoadChildrenAsync(dirNode, sftp);
                if (!ReferenceEquals(_sftp, sftp)) return;
            }
            dirNode.IsExpanded = true;

            foreach (var item in items)
            {
                if (item is StorageFile file && !string.IsNullOrEmpty(file.Path))
                    _ = UploadAsync(file, dirNode, sftp); // 파일마다 병렬 업로드, 각자 진행도 표시
            }
        }

        private async Task UploadAsync(StorageFile file, FileNode dirNode, ISftpService sftp)
        {
            if (!ReferenceEquals(_sftp, sftp)) return;

            long size = 0;
            try { size = (long)(await file.GetBasicPropertiesAsync()).Size; } catch { /* 크기 모름 */ }
            if (!ReferenceEquals(_sftp, sftp)) return;

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
                await sftp.UploadFileAsync(file.Path, dirNode.FullPath, progress, cts.Token);

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

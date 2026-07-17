using Microsoft.UI.Xaml.Controls;
using sutty.Core.Models;
using sutty.Core.Sessions;
using sutty.Core.Sftp;
using sutty.UI.ViewModels;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace sutty.UI.Views
{
    /// <summary>
    /// 연결된 서버의 파일 트리를 보여주는 오른쪽 패널 (FileZilla의 리모트 트리에 해당).
    /// 활성 세션이 없으면 데모(mock) 서버 트리를 보여준다.
    /// </summary>
    public sealed partial class FileTreePanel : UserControl
    {
        public ObservableCollection<FileNode> RootNodes { get; } = [];

        public FileTreePanel()
        {
            InitializeComponent();
        }

        public async Task LoadAsync(ISshSession? session)
        {
            ISftpService sftp;
            if (session is not null)
            {
                SubtitleText.Text = $"{session.Info.Title} · {session.Info.Host}:{session.Info.Port}";
                sftp = session.Sftp;
            }
            else
            {
                SubtitleText.Text = "No active session — showing demo server (mock)";
                sftp = new MockSftpService("demo");
            }

            LoadingRing.IsActive = true;
            RootNodes.Clear();

            var root = await BuildNodeAsync(sftp,
                new RemoteFileEntry { Name = "/", FullPath = "/", IsDirectory = true },
                depth: 0);
            RootNodes.Add(root);

            LoadingRing.IsActive = false;
        }

        // mock 트리는 작으므로 전체를 한 번에 만든다.
        // 실제 SFTP로 바꿀 때는 Expanding 이벤트에서 지연 로딩으로 전환할 것.
        private static async Task<FileNode> BuildNodeAsync(ISftpService sftp, RemoteFileEntry entry, int depth)
        {
            var node = new FileNode(entry) { IsExpandedInitially = depth == 0 };

            if (entry.IsDirectory && depth < 8)
            {
                foreach (var child in await sftp.ListDirectoryAsync(entry.FullPath))
                    node.Children.Add(await BuildNodeAsync(sftp, child, depth + 1));
            }
            return node;
        }
    }
}

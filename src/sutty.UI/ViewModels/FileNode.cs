using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Models;
using sutty.Core.Sftp;
using System.Collections.ObjectModel;
using System.Threading;

namespace sutty.UI.ViewModels;

/// <summary>
/// 파일 트리(TreeView)의 노드 하나. RemoteFileEntry를 UI 표시용으로 감싼다.
/// 업로드 중이면 진행도와 취소 토큰을 함께 가진다.
/// </summary>
public sealed partial class FileNode : ObservableObject
{
    public RemoteFileEntry Entry { get; }
    public FileNode? Parent { get; set; }
    public int SessionVersion { get; set; }
    public ObservableCollection<FileNode> Children { get; } = [];

    // [ObservableProperty]는 WinUI3에서 AOT 경고(MVVMTK0045)가 있어 수동 SetProperty 사용

    private bool _isExpanded;
    /// <summary>펼침 상태 (TreeViewItem.IsExpanded와 양방향 바인딩). 폴더 아이콘도 함께 바뀐다.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(FolderClosedVisible));
                OnPropertyChanged(nameof(FolderOpenVisible));
            }
        }
    }

    // 닫힌 폴더(E8B7) / 열린 폴더(E838) 아이콘 전환용
    public bool FolderClosedVisible => IsDirectory && !IsExpanded;
    public bool FolderOpenVisible => IsDirectory && IsExpanded;

    private bool _hasUnrealizedChildren;
    /// <summary>true면 아직 자식을 서버에서 안 읽어옴 → 펼칠 때 지연 로딩.</summary>
    public bool HasUnrealizedChildren
    {
        get => _hasUnrealizedChildren;
        set => SetProperty(ref _hasUnrealizedChildren, value);
    }

    private bool _isUploading;
    public bool IsUploading
    {
        get => _isUploading;
        set => SetProperty(ref _isUploading, value);
    }

    private double _progress;
    /// <summary>업로드 진행도 0.0 ~ 1.0.</summary>
    public double Progress
    {
        get => _progress;
        set
        {
            if (SetProperty(ref _progress, value))
                OnPropertyChanged(nameof(ProgressText));
        }
    }

    public CancellationTokenSource? UploadCts { get; set; }

    public FileNode(RemoteFileEntry entry) => Entry = entry;

    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
    public bool IsDirectory => Entry.IsDirectory;
    public bool IsFile => !Entry.IsDirectory; // 아이콘 Visibility 바인딩용
    public bool CanModify => Parent is not null; // 현재 탐색 루트는 rename/delete 금지
    public bool CanDownload => IsFile || CanModify; // 탐색 루트 전체 다운로드는 실수 방지를 위해 제외
    public bool CanEdit => IsFile && Entry.IsRegularFile && !Entry.IsSymbolicLink;
    public string ModifiedText => Entry.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

    /// <summary>업로드 대상 디렉터리: 디렉터리면 자기 자신, 파일이면 부모 경로.</summary>
    public string DirectoryPath => IsDirectory ? FullPath : RemotePath.GetDirectory(FullPath);

    public string ProgressText => $"{Progress:P0}";

    // 아이콘(E8B7 폴더 / E8A5 파일)과 색은 FileTreePanel.xaml에서 ThemeResource로 지정

    public string SizeText => IsDirectory ? "" : FormatSize(Entry.Size);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.#} GB",
    };
}

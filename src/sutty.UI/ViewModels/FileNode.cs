using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Models;
using System.Collections.ObjectModel;

namespace sutty.UI.ViewModels;

/// <summary>파일 트리(TreeView)의 노드 하나. RemoteFileEntry를 UI 표시용으로 감싼다.</summary>
public sealed class FileNode
{
    public RemoteFileEntry Entry { get; }
    public ObservableCollection<FileNode> Children { get; } = [];

    /// <summary>루트("/")만 처음부터 펼쳐 놓는다.</summary>
    public bool IsExpandedInitially { get; set; }

    public FileNode(RemoteFileEntry entry) => Entry = entry;

    public string Name => Entry.Name;

    // Segoe Fluent Icons: E8B7 = Folder, E8A5 = Document
    public string Glyph => Entry.IsDirectory ? "" : "";

    public Brush GlyphBrush =>
        (Brush)Application.Current.Resources[Entry.IsDirectory ? "AccentBlue" : "TextMuted"];

    public string SizeText => Entry.IsDirectory ? "" : FormatSize(Entry.Size);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.#} GB",
    };
}

namespace sutty.Core.Models;

/// <summary>SFTP 디렉터리 목록의 한 항목 (파일 또는 디렉터리).</summary>
public sealed class RemoteFileEntry
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
}

namespace sutty.Core.Sftp;

public enum DirectoryEntryKind { File, Directory, Other }
public enum DirectoryMatch { OnlyLocal, OnlyRemote, Different, SameMetadata, Folder, Unverified }
public sealed record DirectoryEntryMetadata(string Name, DirectoryEntryKind Kind, long Size, DateTime? Modified);
public sealed record DirectoryComparisonRow(string Name, DirectoryMatch Match);

public static class DirectoryComparison
{
    public static IReadOnlyList<DirectoryComparisonRow> Compare(
        IEnumerable<DirectoryEntryMetadata> local, IEnumerable<DirectoryEntryMetadata> remote)
    {
        var remaining = remote.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var result = new List<DirectoryComparisonRow>();
        foreach (var entry in local)
        {
            var match = remaining.Remove(entry.Name, out var other) ? Match(entry, other) : DirectoryMatch.OnlyLocal;
            result.Add(new DirectoryComparisonRow(entry.Name, match));
        }
        result.AddRange(remaining.Keys.Select(name => new DirectoryComparisonRow(name, DirectoryMatch.OnlyRemote)));
        result.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        return result;
    }

    private static DirectoryMatch Match(DirectoryEntryMetadata local, DirectoryEntryMetadata remote)
    {
        if (local.Kind == DirectoryEntryKind.Other || remote.Kind == DirectoryEntryKind.Other) return DirectoryMatch.Unverified;
        if (local.Kind != remote.Kind) return DirectoryMatch.Different;
        if (local.Kind == DirectoryEntryKind.Directory) return DirectoryMatch.Folder;
        if (local.Size != remote.Size) return DirectoryMatch.Different;
        if (local.Modified is not { } left || remote.Modified is not { } right) return DirectoryMatch.Unverified;
        return SftpFileMetadata.WholeSecondUtc(left) == SftpFileMetadata.WholeSecondUtc(right)
            ? DirectoryMatch.SameMetadata : DirectoryMatch.Different;
    }
}

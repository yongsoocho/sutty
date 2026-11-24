using sutty.Core.Sftp;

internal static class DirectoryComparisonTests
{
    internal static void Run(Action<bool, string> assert)
    {
        var stamp = new DateTime(2026, 1, 2, 6, 0, 0, DateTimeKind.Utc);
        DirectoryEntryMetadata File(string name) => new(name, DirectoryEntryKind.File, 12, stamp);
        var local = new[] { File("현황"), File("Size"), File("time"), File("a"), File("folder") with { Kind = DirectoryEntryKind.Directory },
            File("conflict"), File("link") with { Kind = DirectoryEntryKind.Other }, File("unknown") };
        var remote = new[] { File("현황") with { Modified = stamp.ToLocalTime().AddMilliseconds(900) }, File("Size") with { Size = 10 },
            File("time") with { Modified = stamp.AddSeconds(1) }, File("A"), File("folder") with { Kind = DirectoryEntryKind.Directory },
            File("conflict") with { Kind = DirectoryEntryKind.Directory }, File("link"), File("unknown") with { Modified = null } };
        var matches = DirectoryComparison.Compare(local, remote).ToDictionary(row => row.Name, row => row.Match);
        assert(matches["현황"] == DirectoryMatch.SameMetadata && matches["Size"] == DirectoryMatch.Different &&
               matches["time"] == DirectoryMatch.Different, "compare names, size and UTC timestamps to whole seconds");
        assert(matches["A"] == DirectoryMatch.OnlyRemote && matches["a"] == DirectoryMatch.OnlyLocal,
            "preserve case-sensitive remote names");
        assert(matches["folder"] == DirectoryMatch.Folder && matches["conflict"] == DirectoryMatch.Different &&
               matches["link"] == DirectoryMatch.Unverified && matches["unknown"] == DirectoryMatch.Unverified,
            "folder contents, links and missing metadata never imply identical contents");
    }
}

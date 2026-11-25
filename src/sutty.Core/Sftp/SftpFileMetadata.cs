using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace sutty.Core.Sftp;

internal static class SftpFileMetadata
{
    // SFTP v3 encodes modification times in whole UTC seconds.
    internal static DateTime WholeSecondUtc(DateTime time)
    {
        var utc = time.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond));
    }

    internal static void SetLocalWriteTime(string stagedPath, DateTime sourceTime)
    {
        var expected = TransferTime(sourceTime);
        File.SetLastWriteTimeUtc(stagedPath, expected);
        if (WholeSecondUtc(File.GetLastWriteTimeUtc(stagedPath)) != expected)
            throw new InvalidOperationException("로컬 파일 시스템에서 수정 시각을 보존할 수 없습니다. / The local filesystem could not preserve the modification time.");
    }

    internal static void ProtectStagedUpload(SftpClient client, string path, SftpFileAttributes? destination)
    {
        if (destination is null) return;
        if (!destination.IsRegularFile)
            throw new InvalidOperationException("일반 파일만 교체할 수 있습니다. / Only regular files can be replaced.");
        var staged = client.GetAttributes(path);
        var sharedMode = Mode(staged) % 100;
        if (sharedMode == 0 || (sharedMode == Mode(destination) % 100 && staged.GroupId == destination.GroupId))
            return;

        // A default umask can give other accounts access that the destination denies.
        // Keep an interrupted overwrite private until its final metadata is verified.
        staged.SetPermissions(600);
        SetRemoteAttributes(client, path, staged);
        if (Mode(client.GetAttributes(path)) != 600)
            throw new InvalidOperationException("임시 파일의 접근 권한을 제한할 수 없습니다. / The server could not restrict access to the staged file.");
    }

    internal static void PrepareRemoteFile(SftpClient client, string stagedPath,
        DateTime sourceTime, SftpFileAttributes? destination)
    {
        var expectedTime = TransferTime(sourceTime);
        var staged = client.GetAttributes(stagedPath);
        if (!staged.IsRegularFile || destination is { IsRegularFile: false })
            throw new InvalidOperationException("일반 파일의 속성만 보존할 수 있습니다. / Only regular files can be replaced with verified permissions.");

        var expectedMode = destination is null ? Mode(staged) : Mode(destination);
        var expectedOwner = destination?.UserId ?? staged.UserId;
        var expectedGroup = destination?.GroupId ?? staged.GroupId;
        if (staged.LastWriteTimeUtc == expectedTime && Mode(staged) == expectedMode &&
            staged.UserId == expectedOwner && staged.GroupId == expectedGroup) return;

        // Change only the staged inode. SSH.NET sends the fields which differ from
        // this stat, including the existing access time alongside the new mtime.
        staged.LastWriteTimeUtc = expectedTime;
        staged.UserId = expectedOwner;
        staged.GroupId = expectedGroup;
        staged.SetPermissions(expectedMode);
        SetRemoteAttributes(client, stagedPath, staged);

        // Some servers acknowledge SETSTAT while ignoring fields they cannot represent.
        var actual = client.GetAttributes(stagedPath);
        if (!actual.IsRegularFile || actual.UserId != expectedOwner || actual.GroupId != expectedGroup ||
            Mode(actual) != expectedMode || actual.LastWriteTimeUtc != expectedTime)
            throw new InvalidOperationException("서버에서 권한 또는 수정 시각을 확인할 수 없습니다. / The server did not retain the requested file permissions or modification time.");
    }

    private static DateTime TransferTime(DateTime sourceTime)
    {
        var time = WholeSecondUtc(sourceTime);
        if (time < DateTime.UnixEpoch || time > DateTime.UnixEpoch.AddSeconds(uint.MaxValue))
            throw new InvalidOperationException("SFTP에서 표현할 수 없는 수정 시각입니다. / SFTP cannot represent the source modification time.");
        return time;
    }

    private static void SetRemoteAttributes(SftpClient client, string path, SftpFileAttributes attributes)
    {
        try { client.SetAttributes(path, attributes); }
        catch (SftpException error)
        {
            throw new InvalidOperationException($"서버에서 권한 또는 수정 시각을 적용할 수 없습니다. / The server could not preserve file permissions or modification time: {error.Message}", error);
        }
    }

    // SSH.NET SetPermissions accepts octal digits written as a decimal number (e.g. 750).
    private static short Mode(SftpFileAttributes file)
    {
        static int Digit(bool four, bool two, bool one) => (four ? 4 : 0) + (two ? 2 : 0) + (one ? 1 : 0);
        return (short)(1000 * Digit(file.IsUIDBitSet, file.IsGroupIDBitSet, file.IsStickyBitSet) +
            100 * Digit(file.OwnerCanRead, file.OwnerCanWrite, file.OwnerCanExecute) +
            10 * Digit(file.GroupCanRead, file.GroupCanWrite, file.GroupCanExecute) +
            Digit(file.OthersCanRead, file.OthersCanWrite, file.OthersCanExecute));
    }
}

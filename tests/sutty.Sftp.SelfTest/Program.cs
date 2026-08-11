using sutty.Core.Sftp;

Assert(RemotePath.Normalize(@"/srv/a\b") == @"/srv/a\b",
    "POSIX backslash filename is preserved");
Assert(RemotePath.Combine("/srv", @"a\b") == @"/srv/a\b",
    "POSIX path combine preserves backslash");
Assert(RemotePath.GetDirectory(@"/srv/a\b") == "/srv",
    "POSIX path parent treats only slash as separator");
Assert(RemotePath.Normalize("/srv/one/../two/./") == "/srv/two",
    "dot segments normalize without escaping root");

var scratch = Path.Combine(Path.GetTempPath(), $"sutty-sftp-self-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);
try
{
    var source = Path.Combine(scratch, "source.txt");
    var remote = Path.Combine(scratch, "remote");
    var downloads = Path.Combine(scratch, "downloads");
    Directory.CreateDirectory(remote);
    Directory.CreateDirectory(downloads);
    await File.WriteAllTextAsync(source, "first");

    var files = new LocalFileService();
    var uploadProgress = new List<double>();
    await files.UploadFileAsync(source, remote, progress: new InlineProgress<double>(uploadProgress.Add));
    var remoteFile = Path.Combine(remote, "source.txt");
    Assert(await File.ReadAllTextAsync(remoteFile) == "first", "upload writes complete file");
    Assert(uploadProgress.Count >= 2 && uploadProgress[0] == 0.0,
        "upload progress starts at zero");
    Assert(uploadProgress[^1] == 1.0,
        "completed upload progress is 100 percent");
    Assert(uploadProgress.Zip(uploadProgress.Skip(1), (left, right) => right >= left).All(value => value),
        "upload progress is monotonic");

    var emptySource = Path.Combine(scratch, "empty.txt");
    await File.WriteAllBytesAsync(emptySource, []);
    var emptyProgress = new List<double>();
    await files.UploadFileAsync(
        emptySource, remote, progress: new InlineProgress<double>(emptyProgress.Add));
    Assert(emptyProgress.SequenceEqual([0.0, 1.0]),
        "zero-byte upload reports zero then 100 percent");

    await File.WriteAllTextAsync(source, "second");
    await AssertThrowsAsync<IOException>(
        () => files.UploadFileAsync(source, remote, overwrite: false),
        "upload collision does not overwrite");
    Assert(await File.ReadAllTextAsync(remoteFile) == "first",
        "rejected upload preserves destination");

    await files.UploadFileAsync(source, remote, overwrite: true);
    Assert(await File.ReadAllTextAsync(remoteFile) == "second",
        "confirmed upload replaces destination");

    var localDownload = Path.Combine(downloads, "saved.txt");
    await File.WriteAllTextAsync(localDownload, "keep");
    await AssertThrowsAsync<IOException>(
        () => files.DownloadFileAsync(remoteFile, localDownload, overwrite: false),
        "download collision does not overwrite");
    Assert(await File.ReadAllTextAsync(localDownload) == "keep",
        "rejected download preserves destination");

    await files.DownloadFileAsync(remoteFile, localDownload, overwrite: true);
    Assert(await File.ReadAllTextAsync(localDownload) == "second",
        "confirmed download replaces destination");

    var cancelledPath = Path.Combine(downloads, "cancelled.txt");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await AssertThrowsAsync<OperationCanceledException>(
        () => files.DownloadFileAsync(remoteFile, cancelledPath, ct: cancelled.Token),
        "cancelled transfer reports cancellation");
    Assert(!File.Exists(cancelledPath), "cancelled transfer leaves no destination or partial file");

    Console.WriteLine("SFTP path and safe local-transfer self-tests passed.");
}
finally
{
    Directory.Delete(scratch, recursive: true);
}

static void Assert(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException($"Self-test failed: {description}.");
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string description)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(
        $"Self-test failed: {description} did not throw {typeof(TException).Name}.");
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

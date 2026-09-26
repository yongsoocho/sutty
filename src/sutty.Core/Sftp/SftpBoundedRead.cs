namespace sutty.Core.Sftp;

internal static class SftpBoundedRead
{
    public static byte[] Read(Stream input, int maximumBytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ct.ThrowIfCancellationRequested();
        if (input.Length > maximumBytes)
            throw new IOException("The remote file exceeds the content verification limit.");

        using var content = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Read at most one byte beyond the limit, even if the file grows after stat.
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, (long)maximumBytes - content.Length + 1));
            if (read == 0) return content.ToArray();
            if (content.Length + read > maximumBytes)
                throw new IOException("The remote file exceeds the content verification limit.");
            content.Write(buffer, 0, read);
        }
    }
}

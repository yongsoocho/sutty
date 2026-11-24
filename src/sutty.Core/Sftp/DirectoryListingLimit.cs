namespace sutty.Core.Sftp;

public static class DirectoryListingLimit
{
    public const int MaximumEntries = 100_000;

    public static void CheckNext(int existingCount)
    {
        if (existingCount >= MaximumEntries)
            throw new IOException("폴더 항목이 100,000개를 초과합니다. 하위 경로를 직접 입력하세요. / This folder exceeds 100,000 entries. Enter a subfolder path directly.");
    }
}

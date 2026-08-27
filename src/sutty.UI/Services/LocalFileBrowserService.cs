using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace sutty.UI.Services;

/// <summary>
/// Read-only local filesystem surface used by the Files workspace. Mutating local
/// operations deliberately stay outside this service so selecting or browsing an
/// item can never delete, rename, or overwrite it.
/// </summary>
public interface ILocalFileBrowserService
{
    string GetInitialPath();
    string NormalizeDirectoryPath(string path);
    string GetParentPath(string path);
    Task<IReadOnlyList<LocalFileEntry>> ListDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default);
}

/// <summary>Credential-free metadata for one item in a local directory.</summary>
public sealed record LocalFileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime Modified,
    bool IsReparsePoint);

/// <summary>Fail-closed Windows path planning for Remote → Local transfers.</summary>
public static class LocalFilePathRules
{
    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
         "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
         "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
        StringComparer.OrdinalIgnoreCase);

    public static bool TryResolveDirectChild(
        string directory,
        string remoteName,
        out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(directory) || !IsSafeWindowsFileName(remoteName))
        {
            return false;
        }

        try
        {
            var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var candidate = Path.GetFullPath(Path.Combine(normalizedDirectory, remoteName));
            if (!string.Equals(Path.GetDirectoryName(candidate), normalizedDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            path = candidate;
            return true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or
                                      NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSafeWindowsFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 ||
            name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                '/', '\\']) >= 0 || name.Any(char.IsControl))
        {
            return false;
        }

        var deviceStem = name.Split('.', 2)[0].TrimEnd(' ', '.');
        return !ReservedWindowsDeviceNames.Contains(deviceStem);
    }
}

public sealed class LocalFileBrowserService : ILocalFileBrowserService
{
    public string GetInitialPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var downloads = Path.Combine(userProfile, "Downloads");
            if (Directory.Exists(downloads))
                return NormalizeDirectoryPath(downloads);
            if (Directory.Exists(userProfile))
                return NormalizeDirectoryPath(userProfile);
        }

        var current = Environment.CurrentDirectory;
        if (Directory.Exists(current))
            return NormalizeDirectoryPath(current);

        var root = DriveInfo.GetDrives()
            .FirstOrDefault(drive => drive.IsReady)?.RootDirectory.FullName;
        if (!string.IsNullOrWhiteSpace(root))
            return NormalizeDirectoryPath(root);

        throw new DirectoryNotFoundException("No readable local folder is available.");
    }

    public string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A local folder path is required.", nameof(path));

        var candidate = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(candidate))
            throw new ArgumentException("The local folder path must be absolute.", nameof(path));

        var normalized = Path.GetFullPath(candidate);
        if (!Directory.Exists(normalized))
            throw new DirectoryNotFoundException($"The local folder does not exist: {normalized}");

        var root = Path.GetPathRoot(normalized);
        if (!string.IsNullOrWhiteSpace(root) && string.Equals(
                normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return Path.TrimEndingDirectorySeparator(normalized);
    }

    public string GetParentPath(string path)
    {
        var normalized = NormalizeDirectoryPath(path);
        return Directory.GetParent(normalized)?.FullName ?? normalized;
    }

    public async Task<IReadOnlyList<LocalFileEntry>> ListDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeDirectoryPath(path);
        return await Task.Run<IReadOnlyList<LocalFileEntry>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = new DirectoryInfo(normalized);
            var entries = new List<LocalFileEntry>();

            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var isDirectory = item is DirectoryInfo;
                    var size = item is FileInfo file ? Math.Max(0, file.Length) : 0;
                    entries.Add(new LocalFileEntry(
                        item.Name,
                        item.FullName,
                        isDirectory,
                        size,
                        item.LastWriteTime,
                        (item.Attributes & FileAttributes.ReparsePoint) != 0));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A single disappearing or inaccessible item must not make the
                    // containing folder unusable. A refresh can pick it up later.
                }
            }

            return entries
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }
}

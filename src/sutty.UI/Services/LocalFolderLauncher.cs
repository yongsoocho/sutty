using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;

namespace sutty.UI.Services;

internal static class LocalFolderLauncher
{
    public static async Task OpenAsync(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new IOException("An absolute folder path is required.");
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        if (!await Launcher.LaunchFolderAsync(folder)) throw new IOException("Explorer could not open the folder.");
    }
}

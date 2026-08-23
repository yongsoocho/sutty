using System;
using System.Linq;
using System.Reflection;

namespace sutty.UI.Helpers;

/// <summary>Build metadata shown in About and command-line launch dialogs.</summary>
public static class AppReleaseInfo
{
    private static readonly string InformationalVersion = ResolveInformationalVersion();
    private static readonly string FullBuildMetadata = ResolveFullBuildMetadata();

    public static string Version { get; } = InformationalVersion.Split('+', 2)[0];

    public static string BuildMetadata { get; } = FullBuildMetadata.Length > 12
        ? FullBuildMetadata[..12]
        : FullBuildMetadata;

    /// <summary>Full source revision when the SDK embedded a hexadecimal commit id.</summary>
    public static string Commit { get; } =
        FullBuildMetadata.Length is >= 7 and <= 64 &&
        FullBuildMetadata.All(Uri.IsHexDigit)
            ? FullBuildMetadata.ToLowerInvariant()
            : "";

    public static string DisplayVersion => $"Sutty {Version}";

    public static string ReleaseTag => $"v{Version}";

    private static string ResolveInformationalVersion()
    {
        var assembly = typeof(AppReleaseInfo).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Trim();
        if (!string.IsNullOrWhiteSpace(informational))
            return informational;

        return assembly.GetName().Version is { } version
            ? version.ToString(3)
            : "0.0.0-unknown";
    }

    private static string ResolveFullBuildMetadata()
    {
        var separator = InformationalVersion.IndexOf('+');
        if (separator < 0 || separator == InformationalVersion.Length - 1)
            return "";

        return InformationalVersion[(separator + 1)..].Trim();
    }
}

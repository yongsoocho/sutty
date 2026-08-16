using System.Reflection;

namespace sutty.UI.Helpers;

/// <summary>Build metadata shown in About and command-line launch dialogs.</summary>
public static class AppReleaseInfo
{
    private static readonly string InformationalVersion = ResolveInformationalVersion();

    public static string Version { get; } = InformationalVersion.Split('+', 2)[0];

    public static string BuildMetadata { get; } = ResolveBuildMetadata();

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

    private static string ResolveBuildMetadata()
    {
        var separator = InformationalVersion.IndexOf('+');
        if (separator < 0 || separator == InformationalVersion.Length - 1)
            return "";

        var metadata = InformationalVersion[(separator + 1)..].Trim();
        return metadata.Length > 12 ? metadata[..12] : metadata;
    }
}

using System.Collections;
using System.Text;

namespace sutty.Core.Terminal;

/// <summary>
/// Runtime-only prompt boundaries and filesystem location. User profiles and
/// machine environment settings are never edited. Child sessions may override them.
/// </summary>
internal static class LocalShellIntegration
{
    public static string PowerShellArguments(bool loadProfile, string directoryNonce)
    {
        var script = $$"""
            $global:__SuttyOriginalPrompt = $function:prompt
            function global:prompt {
                [Console]::Write([string][char]27 + ']133;A' + [char]7)
                $suttyPromptText = [string[]]@(& $global:__SuttyOriginalPrompt)
                $suttyLocation = $ExecutionContext.SessionState.Path.CurrentLocation
                $suttyPath = if ($suttyLocation.Provider.Name -eq 'FileSystem') { $suttyLocation.ProviderPath } else { '' }
                $suttyEncodedPath = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$suttyPath))
                [Console]::Write([string][char]27 + ']777;sutty-cwd;{{directoryNonce}};base64;' + $suttyEncodedPath + [char]7)
                [string]::Join('', $suttyPromptText) + [char]27 + ']133;B' + [char]7
            }
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $" -NoLogo{(loadProfile ? "" : " -NoProfile")} -NoExit -EncodedCommand {encoded}";
    }

    public static string CreateCommandPromptEnvironment(string directoryNonce)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
                variables[name] = value;
        }

        var prompt = variables.GetValueOrDefault("PROMPT", "$P$G");
        variables["PROMPT"] = "$E]133;A$E\\$E]777;sutty-cwd;" + directoryNonce + ";$P$E\\" +
            prompt + "$E]133;B$E\\";
        return string.Join('\0', variables.Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
    }
}

namespace sutty.Core.Plugins;

/// <summary>Data made available to a command suggestion provider.</summary>
public sealed record CommandSuggestionRequest(
    string Input,
    IReadOnlyList<string> RecentCommands,
    IReadOnlyList<string> SavedCommands);

/// <summary>A suggestion returned by a named provider.</summary>
public sealed record CommandSuggestion(string Text, string ProviderId);

/// <summary>
/// Safe extension point for suggestions. Providers receive text only; they are not given an
/// SSH session, credentials, file-system access, or permission to execute commands.
/// </summary>
public interface ICommandSuggestionProvider
{
    string Id { get; }
    IEnumerable<string> GetSuggestions(CommandSuggestionRequest request);
}

/// <summary>
/// Combines built-in and future signed providers behind a fault boundary. This does not load
/// arbitrary assemblies; provider registration remains under application control.
/// </summary>
public sealed class CommandSuggestionEngine
{
    private readonly List<ICommandSuggestionProvider> _providers = [];

    public CommandSuggestionEngine(IEnumerable<ICommandSuggestionProvider>? providers = null)
    {
        if (providers is not null)
            _providers.AddRange(providers.Where(provider => provider is not null));

        if (_providers.Count == 0)
            _providers.Add(new PrefixCommandSuggestionProvider());
    }

    public CommandSuggestion? Suggest(CommandSuggestionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Input))
            return null;

        foreach (var provider in _providers)
        {
            IEnumerable<string> suggestions;
            try
            {
                suggestions = provider.GetSuggestions(request) ?? [];
            }
            catch
            {
                // Suggestions are optional. A provider cannot break command entry.
                continue;
            }

            try
            {
                foreach (var suggestion in suggestions)
                {
                    if (!string.IsNullOrWhiteSpace(suggestion) &&
                        suggestion.Length > request.Input.Length &&
                        suggestion.StartsWith(request.Input, StringComparison.OrdinalIgnoreCase))
                    {
                        return new CommandSuggestion(suggestion, provider.Id);
                    }
                }
            }
            catch
            {
                // Deferred enumerators are isolated by the same provider boundary.
            }
        }

        return null;
    }
}

/// <summary>Suggests the newest matching session command, then a saved playbook command.</summary>
public sealed class PrefixCommandSuggestionProvider : ICommandSuggestionProvider
{
    public string Id => "builtin.prefix";

    public IEnumerable<string> GetSuggestions(CommandSuggestionRequest request)
    {
        for (var index = request.RecentCommands.Count - 1; index >= 0; index--)
            yield return request.RecentCommands[index];

        foreach (var saved in request.SavedCommands)
            yield return saved;
    }
}

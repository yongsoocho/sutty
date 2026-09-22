namespace sutty.Core.Terminal;

/// <summary>Optional, prompt-reported local filesystem location for a terminal.</summary>
public interface ILocalWorkingDirectoryTerminal
{
    /// <summary>Empty until a supported shell reports a filesystem location.</summary>
    string WorkingDirectory { get; }

    /// <summary>Raised on the terminal reader/lifecycle thread when the location changes.</summary>
    event EventHandler<string>? WorkingDirectoryChanged;
}

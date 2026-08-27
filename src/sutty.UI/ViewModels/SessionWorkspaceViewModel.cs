using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace sutty.UI.ViewModels;

/// <summary>Pages that share the context of one SSH session.</summary>
public enum SessionWorkspaceSection
{
    Terminal,
    Files,
    Commands,
    Tunnels,
}

/// <summary>
/// View-independent identity and page-selection state for one SSH workspace. It deliberately
/// stores no credentials, session service, or WinUI element.
/// </summary>
public sealed class SessionWorkspaceViewModel : ObservableObject
{
    private SessionWorkspaceSection _currentSection = SessionWorkspaceSection.Terminal;

    public SessionWorkspaceViewModel(
        Guid sessionId,
        string displayName,
        string username,
        string host,
        int port,
        SessionWorkspaceSection initialSection = SessionWorkspaceSection.Terminal)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A non-empty session id is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("A host is required.", nameof(host));
        if (port is < 1 or > 65_535)
            throw new ArgumentOutOfRangeException(nameof(port), "The port must be between 1 and 65535.");
        if (!Enum.IsDefined(initialSection))
            throw new ArgumentOutOfRangeException(nameof(initialSection));

        SessionId = sessionId;
        Host = host.Trim();
        Username = username?.Trim() ?? string.Empty;
        Port = port;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Host : displayName.Trim();
        ConnectionIdentity = Username.Length == 0
            ? $"{Host}:{Port}"
            : $"{Username}@{Host}:{Port}";
        _currentSection = initialSection;
    }

    public Guid SessionId { get; }

    public string DisplayName { get; }

    public string Username { get; }

    public string Host { get; }

    public int Port { get; }

    /// <summary>A credential-free label suitable for every workspace page header.</summary>
    public string ConnectionIdentity { get; }

    public SessionWorkspaceSection CurrentSection
    {
        get => _currentSection;
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (!SetProperty(ref _currentSection, value))
                return;

            OnPropertyChanged(nameof(IsTerminalSelected));
            OnPropertyChanged(nameof(IsFilesSelected));
            OnPropertyChanged(nameof(IsCommandsSelected));
            OnPropertyChanged(nameof(IsTunnelsSelected));
        }
    }

    public bool IsTerminalSelected => CurrentSection == SessionWorkspaceSection.Terminal;

    public bool IsFilesSelected => CurrentSection == SessionWorkspaceSection.Files;

    public bool IsCommandsSelected => CurrentSection == SessionWorkspaceSection.Commands;

    public bool IsTunnelsSelected => CurrentSection == SessionWorkspaceSection.Tunnels;

    public void SelectSection(SessionWorkspaceSection section) => CurrentSection = section;

    /// <summary>
    /// Maps the persisted legacy terminal-mode value to the equivalent workspace page.
    /// "Repl" remains an internal compatibility value; the user-facing page is Commands.
    /// </summary>
    public static SessionWorkspaceSection ResolveInitialSection(string? terminalMode) =>
        string.Equals(terminalMode, "Repl", StringComparison.Ordinal)
            ? SessionWorkspaceSection.Commands
            : SessionWorkspaceSection.Terminal;
}

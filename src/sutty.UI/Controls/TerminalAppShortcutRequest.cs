namespace sutty.UI.Controls;

/// <summary>Validated application commands captured inside the terminal WebView.</summary>
public enum TerminalAppShortcutAction
{
    Navigate,
    SelectTab,
    NewTab,
    Settings,
}

/// <summary>
/// A package-local terminal shortcut request. Number is used only by Navigate and
/// SelectTab; the renderer validates its range before publishing the request.
/// </summary>
public readonly record struct TerminalAppShortcutRequest(
    TerminalAppShortcutAction Action,
    int Number = 0);

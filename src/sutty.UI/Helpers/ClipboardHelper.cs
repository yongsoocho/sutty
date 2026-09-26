using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace sutty.UI.Helpers;

internal static class ClipboardHelper
{
    public static async Task<bool> CopyTextAsync(
        string? text, bool allowEmpty = false, CancellationToken cancellationToken = default)
    {
        if (text is null || (!allowEmpty && text.Length == 0))
            return false;

        // Another process can briefly hold the Windows clipboard open. Retry on
        // the UI context without blocking it, and stop if this capture is reset.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CopyText(text, allowEmpty))
                return true;
            if (attempt < 3)
                await Task.Delay(40 * (attempt + 1), cancellationToken);
        }
        return false;
    }

    public static void ShowCopyFeedback(FrameworkElement source, string message)
    {
        if (!source.IsLoaded || source.XamlRoot is null)
            return;

        if (ToolTipService.GetToolTip(source) is ToolTip previous)
            previous.IsOpen = false;
        var tooltip = new ToolTip
        {
            PlacementTarget = source,
            Content = new TextBlock { Text = message, MaxWidth = 280, TextWrapping = TextWrapping.Wrap },
        };
        ToolTipService.SetToolTip(source, tooltip);
        tooltip.IsOpen = true;
        var timer = source.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tooltip.IsOpen = false;
        timer.Start();
    }

    public static bool CopyText(string? text, bool allowEmpty = false)
    {
        if (text is null || (!allowEmpty && text.Length == 0))
            return false;

        try
        {
            if (text.Length == 0)
            {
                Clipboard.Clear();
                return true;
            }

            var package = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy,
            };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string?> GetTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
                return null;

            return await content.GetTextAsync();
        }
        catch
        {
            return null;
        }
    }

    public static void InsertAtSelection(TextBox textBox, string value)
    {
        var start = Math.Clamp(textBox.SelectionStart, 0, textBox.Text.Length);
        var selectedLength = Math.Clamp(
            textBox.SelectionLength,
            0,
            textBox.Text.Length - start);
        textBox.Text = string.Concat(
            textBox.Text.AsSpan(0, start),
            value,
            textBox.Text.AsSpan(start + selectedLength));
        textBox.SelectionStart = start + value.Length;
        textBox.SelectionLength = 0;
    }

    public static string NormalizeTerminalPaste(string text) =>
        text.Replace("\r\n", "\r", StringComparison.Ordinal)
            .Replace('\n', '\r');
}

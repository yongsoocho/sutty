using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace sutty.UI.Helpers;

internal static class ClipboardHelper
{
    public static bool CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
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

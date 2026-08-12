using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using sutty.Core.Diagnostics;
using sutty.UI.Helpers;
using System;

namespace sutty.UI.ViewModels;

public sealed class ConnectionLogItemVm
{
    public ConnectionLogItemVm(ConnectionLogEntry entry)
    {
        Entry = entry;
        TimestampText = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
        LevelText = entry.Severity switch
        {
            ConnectionLogSeverity.Verbose => Loc.T("상세", "VERBOSE"),
            ConnectionLogSeverity.Debug => Loc.T("디버그", "DEBUG"),
            ConnectionLogSeverity.Information => Loc.T("정보", "INFO"),
            ConnectionLogSeverity.Warning => Loc.T("경고", "WARN"),
            ConnectionLogSeverity.Error => Loc.T("오류", "ERROR"),
            ConnectionLogSeverity.Critical => Loc.T("위험", "CRITICAL"),
            _ => entry.Severity.ToString().ToUpperInvariant(),
        };
        Message = Loc.T(entry.MessageKo, entry.MessageEn);
    }

    public ConnectionLogEntry Entry { get; }
    public string TimestampText { get; }
    public string LevelText { get; }
    public string Message { get; }
    public string Category => Entry.Category;
    public string SessionText => string.IsNullOrWhiteSpace(Entry.SessionTitle)
        ? Entry.Endpoint
        : $"{Entry.SessionTitle} · {Entry.Endpoint}";
    public string Detail => Entry.Detail ?? "";
    public bool HasDetail => !string.IsNullOrWhiteSpace(Entry.Detail);

    public Brush LevelBrush => (Brush)Application.Current.Resources[Entry.Severity switch
    {
        ConnectionLogSeverity.Warning => "StatusAmber",
        ConnectionLogSeverity.Error or ConnectionLogSeverity.Critical => "StatusRed",
        ConnectionLogSeverity.Information => "StatusGreen",
        ConnectionLogSeverity.Debug => "AccentViolet",
        _ => "TextFaint",
    }];

    public string FullText
    {
        get
        {
            var heading = $"[{TimestampText}] [{LevelText}] {SessionText}\n{Category}: {Message}";
            return HasDetail ? $"{heading}\n\n{Detail}" : heading;
        }
    }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return TimestampText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               LevelText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               SessionText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               Entry.MessageKo.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               Entry.MessageEn.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               Detail.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}

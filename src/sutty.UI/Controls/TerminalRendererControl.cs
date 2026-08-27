using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using sutty.Core.Terminal;
using sutty.Setting;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace sutty.UI.Controls;

/// <summary>
/// Package-local xterm.js renderer hosted in a locked-down WebView2. The control has no
/// network dependency and exposes only a small, validated JSON bridge for PTY bytes,
/// input, resize, search, and clipboard requests.
/// </summary>
public sealed class TerminalRendererControl : UserControl
{
    private const int ProtocolVersion = 1;
    private const int MaxQueuedOutputBytes = 4 * 1024 * 1024;
    private const int MaxBridgeJsonCharacters = 6 * 1024 * 1024;
    private const string VirtualHost = "sutty-terminal.local";

    private readonly WebView2 _webView = new();
    private readonly Queue<byte[]> _pendingWrites = [];
    private int _queuedOutputBytes;
    private int _inFlightBytes;
    private long _inFlightId;
    private long _nextWriteId;
    private long _droppedOutputBytes;
    private bool _resetPending;
    private string? _resetText;
    private bool _rendererReady;
    private bool _initializing;
    private bool _failed;
    private TerminalBridgeMessage? _pendingOptions;

    public TerminalRendererControl()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        IsTabStop = true;
        Content = _webView;
        Loaded += TerminalRendererControl_Loaded;
        ActualThemeChanged += (_, _) => ApplyCurrentSettings();
    }

    public event EventHandler<string>? InputReceived;
    public event EventHandler<TerminalSize>? TerminalSizeChanged;
    public event EventHandler<string>? TitleChanged;
    public event EventHandler<TerminalAppShortcutRequest>? AppShortcutRequested;
    public event EventHandler? RendererReady;
    public event EventHandler<string>? RendererFailed;

    public bool IsRendererReady => _rendererReady;
    public TerminalSize ViewportSize { get; private set; } = new(120, 40);

    public void Write(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty || _failed)
            return;

        var copy = data.ToArray();
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => QueueWrite(copy));
            return;
        }

        QueueWrite(copy);
    }

    public void Reset(string? notice = null)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => Reset(notice));
            return;
        }

        _pendingWrites.Clear();
        _queuedOutputBytes = 0;
        _resetPending = true;
        _resetText = notice;
        SendNextWrite();
    }

    public void ApplyCurrentSettings()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ApplyCurrentSettings);
            return;
        }

        var settings = SettingsService.Current;
        var appIsDark = ActualTheme != ElementTheme.Light;
        var preset = TerminalThemeCatalog.Resolve(settings.TerminalTheme, appIsDark);
        _pendingOptions = new TerminalBridgeMessage
        {
            Type = "options",
            FontFamily = BuildFontFamily(settings.TerminalFontFamily),
            FontSize = Math.Clamp(settings.TerminalFontSize, 8, 32),
            CursorStyle = NormalizeCursorStyle(settings.TerminalCursorStyle),
            CursorBlink = settings.TerminalCursorBlink,
            Scrollback = Math.Clamp(settings.TerminalScrollbackLines, 100, 50_000),
            ScreenReaderMode = settings.TerminalScreenReaderMode,
            Language = settings.Language,
            Theme = preset.Palette,
        };

        if (_rendererReady)
            Post(_pendingOptions);
    }

    public void FocusTerminal()
    {
        Focus(FocusState.Programmatic);
        if (_rendererReady)
            Post(new TerminalBridgeMessage { Type = "focus" });
    }

    public void FindNext(string? text = null)
    {
        if (_rendererReady)
            Post(new TerminalBridgeMessage { Type = "findNext", Text = text });
    }

    public void FindPrevious(string? text = null)
    {
        if (_rendererReady)
            Post(new TerminalBridgeMessage { Type = "findPrevious", Text = text });
    }

    private async void TerminalRendererControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (_rendererReady)
        {
            FocusTerminal();
            return;
        }

        if (_initializing || _failed)
            return;

        _initializing = true;
        try
        {
            var assetFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");
            var entryPoint = Path.Combine(assetFolder, "index.html");
            if (!File.Exists(entryPoint))
                throw new FileNotFoundException("The packaged terminal renderer is missing.", entryPoint);

            await _webView.EnsureCoreWebView2Async();
            var core = _webView.CoreWebView2 ??
                throw new InvalidOperationException("WebView2 initialization returned no CoreWebView2 instance.");

            core.Settings.IsScriptEnabled = true;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            core.NavigationStarting += Core_NavigationStarting;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.WebMessageReceived += Core_WebMessageReceived;
            core.ProcessFailed += (_, args) => Fail($"WebView2 process failed: {args.ProcessFailedKind}");
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                assetFolder,
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate($"https://{VirtualHost}/index.html");
        }
        catch (Exception error)
        {
            Fail(error.Message);
            Debug.WriteLine($"Terminal renderer initialization failed: {error}");
        }
        finally
        {
            _initializing = false;
        }
    }

    private static void Core_NavigationStarting(
        CoreWebView2 sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, VirtualHost, StringComparison.OrdinalIgnoreCase))
        {
            args.Cancel = true;
        }
    }

    private async void Core_WebMessageReceived(
        CoreWebView2 sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var json = args.WebMessageAsJson;
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBridgeJsonCharacters)
                return;

            var message = JsonSerializer.Deserialize(
                json,
                TerminalBridgeJsonContext.Default.TerminalBridgeMessage);
            if (message is null || message.Version != ProtocolVersion)
                return;

            switch (message.Type)
            {
                case "ready":
                    _rendererReady = true;
                    ApplyCurrentSettings();
                    SendNextWrite();
                    RendererReady?.Invoke(this, EventArgs.Empty);
                    FocusTerminal();
                    break;

                case "writeComplete":
                    if (message.Id != 0 && message.Id == _inFlightId)
                    {
                        _inFlightId = 0;
                        _inFlightBytes = 0;
                        SendNextWrite();
                    }
                    break;

                case "input":
                    if (message.Data is { Length: > 0 and <= 4 * 1024 * 1024 })
                        InputReceived?.Invoke(this, message.Data);
                    break;

                case "resize":
                    if (message.Columns is >= 20 and <= 500 && message.Rows is >= 5 and <= 200)
                    {
                        ViewportSize = new TerminalSize(
                            (uint)message.Columns,
                            (uint)message.Rows,
                            (uint)Math.Clamp(message.PixelWidth, 0, 32_768),
                            (uint)Math.Clamp(message.PixelHeight, 0, 32_768));
                        TerminalSizeChanged?.Invoke(this, ViewportSize);
                    }
                    break;

                case "copy":
                    if (message.Text is { Length: <= 4 * 1024 * 1024 })
                        ClipboardHelper.CopyText(message.Text);
                    break;

                case "pasteRequest":
                    var text = await ClipboardHelper.GetTextAsync();
                    if (!string.IsNullOrEmpty(text) && text.Length <= 4 * 1024 * 1024)
                        Post(new TerminalBridgeMessage { Type = "paste", Text = text });
                    break;

                case "appShortcut":
                    var shortcut = message.Action switch
                    {
                        "navigate" when message.Number is >= 1 and <= 7 =>
                            new TerminalAppShortcutRequest(
                                TerminalAppShortcutAction.Navigate,
                                message.Number),
                        "selectTab" when message.Number is >= 1 and <= 9 =>
                            new TerminalAppShortcutRequest(
                                TerminalAppShortcutAction.SelectTab,
                                message.Number),
                        "newTab" => new TerminalAppShortcutRequest(
                            TerminalAppShortcutAction.NewTab),
                        "settings" => new TerminalAppShortcutRequest(
                            TerminalAppShortcutAction.Settings),
                        _ => (TerminalAppShortcutRequest?)null,
                    };
                    if (shortcut is { } request)
                        AppShortcutRequested?.Invoke(this, request);
                    break;

                case "title":
                    if (message.Text is { Length: <= 256 })
                        TitleChanged?.Invoke(this, message.Text);
                    break;

                case "error":
                    if (!string.IsNullOrWhiteSpace(message.Text))
                        RendererFailed?.Invoke(this, message.Text[..Math.Min(message.Text.Length, 2048)]);
                    break;
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine($"Rejected terminal bridge message: {error}");
        }
    }

    private void QueueWrite(byte[] data)
    {
        if (_failed || data.Length == 0)
            return;

        if (data.Length > MaxQueuedOutputBytes)
        {
            _droppedOutputBytes += _queuedOutputBytes + data.LongLength;
            _pendingWrites.Clear();
            _queuedOutputBytes = 0;
            RequestOverflowReset();
            return;
        }

        if (_queuedOutputBytes + _inFlightBytes + data.Length > MaxQueuedOutputBytes)
        {
            _droppedOutputBytes += _queuedOutputBytes;
            _pendingWrites.Clear();
            _queuedOutputBytes = 0;
            RequestOverflowReset();
        }

        _pendingWrites.Enqueue(data);
        _queuedOutputBytes += data.Length;
        SendNextWrite();
    }

    private void RequestOverflowReset()
    {
        _resetPending = true;
        _resetText = Loc.T(
            $"\r\n[sutty: 터미널 출력 대기열 한도를 초과하여 {_droppedOutputBytes:N0}바이트를 버리고 화면을 재설정했습니다.]\r\n",
            $"\r\n[sutty: terminal output exceeded the bounded queue; dropped {_droppedOutputBytes:N0} bytes and reset the screen.]\r\n");
    }

    private void SendNextWrite()
    {
        if (!_rendererReady || _failed || _inFlightId != 0)
            return;

        if (_resetPending)
        {
            Post(new TerminalBridgeMessage { Type = "reset", Text = _resetText });
            _resetPending = false;
            _resetText = null;
            _droppedOutputBytes = 0;
        }

        if (!_pendingWrites.TryDequeue(out var data))
            return;

        _queuedOutputBytes -= data.Length;
        _inFlightBytes = data.Length;
        _inFlightId = ++_nextWriteId;
        Post(new TerminalBridgeMessage
        {
            Type = "write",
            Id = _inFlightId,
            Data = Convert.ToBase64String(data),
        });
    }

    private void Post(TerminalBridgeMessage message)
    {
        if (_webView.CoreWebView2 is null || _failed)
            return;

        message.Version = ProtocolVersion;
        var json = JsonSerializer.Serialize(
            message,
            TerminalBridgeJsonContext.Default.TerminalBridgeMessage);
        _webView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void Fail(string message)
    {
        if (_failed)
            return;

        _failed = true;
        _rendererReady = false;
        _pendingWrites.Clear();
        _queuedOutputBytes = 0;
        RendererFailed?.Invoke(this, message);
    }

    private static string BuildFontFamily(string? fontFamily)
    {
        var primary = string.IsNullOrWhiteSpace(fontFamily)
            ? "Cascadia Mono"
            : fontFamily.Trim()[..Math.Min(fontFamily.Trim().Length, 128)];
        return $"'{primary.Replace("'", string.Empty)}', 'Cascadia Mono', Consolas, monospace";
    }

    private static string NormalizeCursorStyle(string? cursorStyle) =>
        cursorStyle?.ToLowerInvariant() switch
        {
            "block" => "block",
            "bar" => "bar",
            _ => "underline",
        };
}

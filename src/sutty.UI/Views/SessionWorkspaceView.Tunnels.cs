using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Sessions;
using sutty.UI.Helpers;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace sutty.UI.Views;

public sealed partial class SessionWorkspaceView
{
    private bool _tunnelBusy;
    private Task? _activeTunnelOperation;

    public sealed class TunnelRow(TunnelSnapshot snapshot, bool connected, bool busy)
    {
        public Guid Id => snapshot.Id;
        public string Description => DescribeTunnel(snapshot.Definition);
        public string Status => snapshot.State switch
        {
            TunnelState.Running => Loc.T("수신 중", "Listening"),
            TunnelState.Starting => Loc.T("시작 중…", "Starting…"),
            TunnelState.Stopping => Loc.T("중지 중…", "Stopping…"),
            TunnelState.Failed when snapshot.IsListening =>
                Loc.T("오류 · 수신 포트는 열려 있음", "Error · listener is still open"),
            TunnelState.Failed => Loc.T("실패 · 수신하지 않음", "Failed · not listening"),
            _ => Loc.T("중지됨", "Stopped"),
        };
        public string Error => snapshot.ErrorCode switch
        {
            "start-failed" => Loc.T(
                "시작할 수 없습니다. 포트 충돌, 바인드 주소와 서버의 포워딩 허용 설정을 확인한 뒤 다시 시도하세요.",
                "Could not start. Check for a port conflict, the bind address, and server forwarding permissions, then retry."),
            "stop-failed" => Loc.T(
                "중지 중 오류가 발생했습니다. 다시 중지하거나 세션을 종료하세요.",
                "Stopping failed. Try stopping again or close the session."),
            "listener-failed" => Loc.T(
                "터널 연결 오류가 발생했습니다. 목적지 주소·포트와 서버 접근 권한을 확인하세요. 필요하면 중지한 뒤 다시 시작하세요.",
                "A tunnel connection failed. Check the destination address, port, and server access. Stop and restart if needed."),
            _ => "",
        };
        public Visibility ErrorVisibility => snapshot.ErrorCode is null ? Visibility.Collapsed : Visibility.Visible;
        public bool CanStart => connected && !busy && snapshot.State is TunnelState.Stopped or TunnelState.Failed;
        public bool CanStop => !busy && (snapshot.IsListening || snapshot.State == TunnelState.Failed);
        public string StartName => Loc.T("터널 시작: ", "Start tunnel: ") + Description;
        public string StopName => Loc.T("터널 중지: ", "Stop tunnel: ") + Description;
    }

    private void Tunnels_Changed(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(RefreshTunnels);

    private void Tunnels_SessionStateChanged(object? sender, SessionState state) =>
        DispatcherQueue.TryEnqueue(RefreshTunnels);

    private void RefreshTunnels()
    {
        if (_detached) return;
        var capable = SessionView.Session as IPortForwardingSession;
        var connected = SessionView.Session.State == SessionState.Connected;
        Tunnels.Clear();
        if (capable is not null)
            foreach (var tunnel in capable.Tunnels)
                Tunnels.Add(new TunnelRow(tunnel, connected, _tunnelBusy));
        TunnelsButton.Visibility = capable is null ? Visibility.Collapsed : Visibility.Visible;
        AddTunnelButton.IsEnabled = capable is not null && connected && !_tunnelBusy && Tunnels.Count < 32;
        ToolTipService.SetToolTip(AddTunnelButton, !connected
            ? Loc.T("SSH 연결 후 추가할 수 있습니다.", "Connect SSH before adding a tunnel.")
            : Tunnels.Count >= 32
                ? Loc.T("세션당 터널 32개 한도입니다.", "The session limit is 32 tunnels.")
                : Loc.T("이번 세션에만 터널 정의를 추가합니다.", "Add a tunnel definition for this session."));
        NoTunnelsState.Visibility = Tunnels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void TunnelStart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id } || _tunnelBusy || _detached ||
            SessionView.Session is not IPortForwardingSession tunnels) return;
        var tunnel = tunnels.Tunnels.FirstOrDefault(t => t.Id == id);
        if (tunnel is null) return;
        var allowExternal = false;
        if (ForwardingExposurePolicy.IsExternalBind(tunnel.Definition.BindHost))
        {
            allowExternal = await ConfirmTunnelExposureAsync(tunnel.Definition);
            if (!allowExternal || _detached) return;
        }
        await RunTunnelOperationAsync(() => tunnels.StartTunnelAsync(id, allowExternal, _lifetime.Token));
    }

    private async void TunnelStop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Guid id } && !_tunnelBusy && !_detached &&
            SessionView.Session is IPortForwardingSession tunnels)
            await RunTunnelOperationAsync(() => tunnels.StopTunnelAsync(id, _lifetime.Token));
    }

    private async Task RunTunnelOperationAsync(Func<Task> operation)
    {
        if (_tunnelBusy || _detached) return;
        _tunnelBusy = true;
        TunnelOperationMessage.Text = "";
        RefreshTunnels();
        try
        {
            _activeTunnelOperation = operation();
            await _activeTunnelOperation;
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!_detached)
                TunnelOperationMessage.Text = Loc.T(
                    "터널 작업을 완료하지 못했습니다. 아래 오류와 SSH 연결 상태를 확인하세요.",
                    "The tunnel operation could not finish. Check the error below and the SSH connection state.");
        }
        finally { _activeTunnelOperation = null; _tunnelBusy = false; RefreshTunnels(); }
    }

    private async Task<bool> ConfirmTunnelExposureAsync(TunnelDefinition definition)
    {
        if (_detached || XamlRoot is not { } root) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.T("외부 포트 노출 경고", "External port exposure warning"),
            Content = ViewModel.ConnectionIdentity + "\n\n" + DescribeTunnel(definition) + "\n\n" + Loc.T(
                "루프백 밖의 주소로 수신하면 다른 장치가 접근할 수 있습니다. Remote는 서버에서 수신하며 서버 설정에 따라 바인드가 제한될 수 있습니다. 방화벽과 접근 범위를 확인한 경우에만 시작하세요.",
                "A listener beyond loopback may be reachable from other devices. Remote listens on the server, which may restrict the bind address. Start only after checking the firewall and intended access."),
            PrimaryButtonText = Loc.T("위험을 이해하고 시작", "I understand, start"),
            CloseButtonText = Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void AddTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (_tunnelBusy || _detached || XamlRoot is not { } root ||
            SessionView.Session is not IPortForwardingSession tunnels) return;
        var type = new ComboBox { Header = Loc.T("터널 종류", "Tunnel type"), SelectedIndex = 0 };
        type.Items.Add("Local · " + Loc.T("이 PC에서 수신", "Listen on this PC"));
        type.Items.Add("Remote · " + Loc.T("SSH 서버에서 수신", "Listen on SSH server"));
        type.Items.Add("Dynamic · SOCKS");
        type.SelectedIndex = 0;
        var bind = TunnelInput(Loc.T("바인드 주소", "Bind address"), "127.0.0.1");
        var bindPort = TunnelInput(Loc.T("수신 포트", "Listening port"), "8080");
        var destination = TunnelInput(Loc.T("목적지 주소", "Destination address"), "127.0.0.1");
        var destinationPort = TunnelInput(Loc.T("목적지 포트", "Destination port"), "80");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 10, MinWidth = 360 };
        content.Children.Add(new TextBlock { Text = ViewModel.ConnectionIdentity, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(type);
        content.Children.Add(bind);
        content.Children.Add(bindPort);
        content.Children.Add(destination);
        content.Children.Add(destinationPort);
        content.Children.Add(error);
        AutomationProperties.SetName(type, Loc.T("터널 종류", "Tunnel type"));
        type.SelectionChanged += (_, _) =>
        {
            var visibility = type.SelectedIndex == 2 ? Visibility.Collapsed : Visibility.Visible;
            destination.Visibility = destinationPort.Visibility = visibility;
        };
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = Loc.T("세션 터널 추가", "Add session tunnel"),
            Content = new ScrollViewer { Content = content, MaxHeight = 500 },
            PrimaryButtonText = Loc.T("추가", "Add"),
            CloseButtonText = Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        SshPortForwardingRule? rule = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            rule = new SshPortForwardingRule
            {
                Type = (SshPortForwardingType)type.SelectedIndex,
                BindHost = bind.Text.Trim(),
                BindPort = int.TryParse(bindPort.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var bp) ? bp : 0,
                DestinationHost = destination.Text.Trim(),
                DestinationPort = int.TryParse(destinationPort.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var dp) ? dp : 0,
            };
            try { TunnelDefinition.FromRule(rule); }
            catch (ArgumentException)
            {
                args.Cancel = true;
                error.Text = Loc.T("주소와 포트(1–65535)를 확인하세요.", "Check the addresses and ports (1–65535).");
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || rule is null || _detached) return;
        // Add is intentionally stopped: adding a definition cannot open a public listener.
        await RunTunnelOperationAsync(async () => { await tunnels.AddTunnelAsync(rule, _lifetime.Token); });
    }

    private static TextBox TunnelInput(string name, string value)
    {
        var textBox = new TextBox { Header = name, Text = value };
        AutomationProperties.SetName(textBox, name);
        return textBox;
    }

    private static string DescribeTunnel(TunnelDefinition rule)
    {
        var host = rule.BindHost.Contains(':') ? $"[{rule.BindHost.Trim('[', ']')}]" : rule.BindHost;
        return rule.Type == SshPortForwardingType.Dynamic
            ? $"SOCKS   {host}:{rule.BindPort}"
            : $"{rule.Type.ToString().ToUpperInvariant()}   {host}:{rule.BindPort} → {rule.DestinationHost}:{rule.DestinationPort}";
    }
}

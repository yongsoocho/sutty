using sutty.Core.Models;
using sutty.UI.ViewModels;
using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace sutty.UI.Views;

/// <summary>Central Home surface that preserves Quick Connect and history state.</summary>
public sealed partial class HomeDashboardPanel : UserControl
{
    public event HomeConnectRequestedEventHandler? ConnectRequested;

    public event EventHandler<HostInfoModel>? HistoryConnectRequested;

    public event LocalCommandLaunchRequestedEventHandler? LocalCommandLaunchRequested;

    public HomeDashboardPanel()
    {
        InitializeComponent();
        QuickConnect.ConnectRequested += ForwardConnectAsync;
        QuickConnect.LocalCommandLaunchRequested += ForwardLocalCommandLaunchAsync;
        HostHistory.ConnectRequested += (_, host) => HistoryConnectRequested?.Invoke(this, host);
    }

    public IntPtr OwnerWindowHandle
    {
        get => QuickConnect.OwnerWindowHandle;
        set => QuickConnect.OwnerWindowHandle = value;
    }

    public void FocusQuickConnect() => QuickConnect.FocusHost();

    public void ClearTransientSecrets() => QuickConnect.ClearTransientSecrets();

    public void ApplyConnectionDraft(SshConnectionInfo draft) => QuickConnect.ApplyConnectionDraft(draft);

    public void ApplyConnectionDefaults(bool applyPort, bool applyKeepAlive) =>
        QuickConnect.ApplyConnectionDefaults(applyPort, applyKeepAlive);

    public void RefreshHosts() => HostHistory.RefreshFromStore();

    public void RefreshLanguage()
    {
        QuickConnect.RefreshLanguage();
        HostHistory.RefreshLanguage();
    }

    private async Task ForwardConnectAsync(object? sender, SshConnectionInfo info)
    {
        if (ConnectRequested is not { } callbacks)
            return;
        foreach (HomeConnectRequestedEventHandler callback in callbacks.GetInvocationList())
            await callback(this, info);
    }

    private async Task<bool> ForwardLocalCommandLaunchAsync(
        object? sender,
        LocalCommandLaunchRequest request)
    {
        if (LocalCommandLaunchRequested is not { } callbacks)
            return false;

        var launched = false;
        foreach (LocalCommandLaunchRequestedEventHandler callback in callbacks.GetInvocationList())
            launched |= await callback(this, request);
        return launched;
    }
}

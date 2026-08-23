using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Security;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace sutty.UI.Views;

/// <summary>
/// User-owned Known Hosts management surface. Storage access remains in this panel so the
/// Settings shell only owns navigation and language refresh.
/// </summary>
public sealed partial class KnownHostsManagerPanel : UserControl
{
    private readonly IKnownHostsStore _store;
    private int _refreshVersion;

    public KnownHostsManagerPanel()
        : this(KnownHostsStore.Default)
    {
    }

    internal KnownHostsManagerPanel(IKnownHostsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        InitializeComponent();
    }

    public ObservableCollection<KnownHostItemVm> Hosts { get; } = [];

    public ObservableCollection<KnownHostActivityItemVm> Activity { get; } = [];

    public void RefreshFromStore() => _ = RefreshFromStoreAsync(announce: false);

    public void RefreshLanguage()
    {
        Bindings.Update();
        RefreshFromStore();
    }

    private void KnownHostsManagerPanel_Loaded(object sender, RoutedEventArgs e) =>
        RefreshFromStore();

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        _ = RefreshFromStoreAsync(announce: true);

    private async Task RefreshFromStoreAsync(bool announce)
    {
        var version = Interlocked.Increment(ref _refreshVersion);
        RefreshButton.IsEnabled = false;

        try
        {
            var snapshot = await Task.Run(() => _store.GetSnapshot(100));
            if (version != Volatile.Read(ref _refreshVersion))
                return;

            Hosts.Clear();
            foreach (var record in snapshot.Hosts)
                Hosts.Add(new KnownHostItemVm(record));

            Activity.Clear();
            foreach (var record in snapshot.Activity)
                Activity.Add(new KnownHostActivityItemVm(record));

            ErrorState.Visibility = Visibility.Collapsed;
            ContentState.Visibility = Visibility.Visible;
            HostsEmptyState.Visibility = Hosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HostsList.Visibility = Hosts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ActivityEmptyState.Visibility = Activity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ActivityList.Visibility = Activity.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            HostCountText.Text = Hosts.Count.ToString("N0");
            ActivityCountText.Text = Activity.Count.ToString("N0");
            AutomationProperties.SetName(
                HostCountText,
                Loc.T($"신뢰한 Host key {Hosts.Count}개", $"{Hosts.Count} trusted host keys"));
            AutomationProperties.SetName(
                ActivityCountText,
                Loc.T($"표시한 보안 활동 {Activity.Count}개", $"{Activity.Count} security activities shown"));

            if (announce)
                ShowStatus("Known Hosts를 새로고침했습니다.", "Known Hosts refreshed.", "StatusGreen");
        }
        catch (Exception error) when (IsExpectedStoreFailure(error))
        {
            if (version != Volatile.Read(ref _refreshVersion))
                return;

            Debug.WriteLine($"Known Hosts refresh failed: {error.GetType().Name}");
            ContentState.Visibility = Visibility.Collapsed;
            ErrorState.Visibility = Visibility.Visible;
            ErrorText.Text = Loc.T(
                "Known Hosts 파일이 손상되었거나 사용할 수 없습니다. 파일 권한을 확인한 뒤 다시 시도하세요. 안전을 위해 저장된 key를 추측하거나 자동 복구하지 않았습니다.",
                "The Known Hosts file is corrupt or unavailable. Check file permissions and try again. Saved keys were not guessed or repaired automatically.");
            ShowStatus(
                "Known Hosts를 읽지 못했습니다.",
                "Known Hosts could not be loaded.",
                "StatusRed");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            if (version != Volatile.Read(ref _refreshVersion))
                return;

            Debug.WriteLine($"Unexpected Known Hosts refresh failure: {error.GetType().Name}");
            ContentState.Visibility = Visibility.Collapsed;
            ErrorState.Visibility = Visibility.Visible;
            ErrorText.Text = Loc.T(
                "Known Hosts를 읽는 동안 예기치 않은 오류가 발생했습니다. 안전을 위해 저장된 key를 사용하지 않았습니다.",
                "An unexpected error occurred while reading Known Hosts. Saved keys were not used as a safety precaution.");
            ShowStatus(
                "Known Hosts를 읽지 못했습니다.",
                "Known Hosts could not be loaded.",
                "StatusRed");
        }
        finally
        {
            if (version == Volatile.Read(ref _refreshVersion))
                RefreshButton.IsEnabled = true;
        }
    }

    private async void DeleteHost_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: KnownHostRecord record } button)
            return;

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = Loc.T(
                "이 Host key의 신뢰를 삭제하시겠습니까? 다음 연결에서는 서버 key를 다시 직접 확인해야 합니다.",
                "Remove trust for this host key? You must verify the server key again on the next connection."),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = record.Endpoint.Value,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeResources.Brush(this, "TextPrimary"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"{record.Key.Algorithm}\n{record.Key.Sha256Fingerprint}",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 10.5,
            Foreground = ThemeResources.Brush(this, "AccentTeal"),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            Title = Loc.T("Known Host 삭제", "Delete Known Host"),
            Content = content,
            PrimaryButtonText = Loc.T("신뢰 삭제", "Remove trust"),
            CloseButtonText = Loc.T("취소", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        button.IsEnabled = false;
        try
        {
            var removed = await Task.Run(() => _store.Remove(record.Endpoint, record.Key));
            await RefreshFromStoreAsync(announce: false);
            ShowStatus(
                removed
                    ? "선택한 Host key의 신뢰를 삭제했습니다."
                    : "선택한 Host key는 이미 삭제되었습니다.",
                removed
                    ? "Trust for the selected host key was removed."
                    : "The selected host key had already been removed.",
                removed ? "StatusGreen" : "StatusAmber");
        }
        catch (HostKeyChangedException error)
        {
            Debug.WriteLine($"Known Host removal rejected because the key changed: {error.Endpoint.Value}");
            await RefreshFromStoreAsync(announce: false);
            ShowStatus(
                "표시된 뒤 Host key 기록이 변경되어 삭제하지 않았습니다. 새 목록을 확인한 뒤 다시 시도하세요.",
                "The host key record changed after it was displayed, so it was not removed. Review the refreshed list and try again.",
                "StatusAmber");
        }
        catch (Exception error) when (IsExpectedStoreFailure(error))
        {
            Debug.WriteLine($"Known Host removal failed: {error.GetType().Name}");
            ShowStatus(
                "Host key를 삭제하지 못했습니다. 파일 권한을 확인하고 다시 시도하세요.",
                "The host key could not be removed. Check file permissions and try again.",
                "StatusRed");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            Debug.WriteLine($"Unexpected Known Host removal failure: {error.GetType().Name}");
            await RefreshFromStoreAsync(announce: false);
            ShowStatus(
                "예기치 않은 오류로 Host key를 삭제하지 못했습니다.",
                "The host key could not be removed because of an unexpected error.",
                "StatusRed");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void ShowStatus(string korean, string english, string brushKey)
    {
        StatusText.Text = Loc.T(korean, english);
        StatusText.Foreground = ThemeResources.Brush(this, brushKey);
        StatusText.Visibility = Visibility.Visible;
    }

    private static bool IsExpectedStoreFailure(Exception error) => error is
        IOException or
        UnauthorizedAccessException or
        SecurityException or
        CryptographicException or
        ArgumentException or
        InvalidOperationException;

}

public sealed class KnownHostItemVm
{
    public KnownHostItemVm(KnownHostRecord record)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
    }

    public KnownHostRecord Record { get; }
    public string Host => Record.Endpoint.Host;
    public string PortText => $"PORT {Record.Endpoint.Port}";
    public string Algorithm => Record.Key.Algorithm;
    public string Fingerprint => Record.Key.Sha256Fingerprint;
    public string TrustedAtText => FormatDate(Record.TrustedAtUtc);
    public string LastUsedAtText => FormatDate(Record.LastUsedAtUtc);
    public string AccessibilityName => Loc.T(
        $"신뢰한 SSH Host {Record.Endpoint.Host}, Port {Record.Endpoint.Port}, {Algorithm}, 최초 신뢰 {TrustedAtText}, 마지막 사용 {LastUsedAtText}",
        $"Trusted SSH host {Record.Endpoint.Host}, port {Record.Endpoint.Port}, {Algorithm}, first trusted {TrustedAtText}, last used {LastUsedAtText}");
    public string DeleteAccessibilityName => Loc.T(
        $"{Record.Endpoint.Value} Host key 신뢰 삭제",
        $"Remove trust for host key {Record.Endpoint.Value}");

    private static string FormatDate(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed class KnownHostActivityItemVm
{
    public KnownHostActivityItemVm(KnownHostActivityRecord record)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        TypeText = record.Type switch
        {
            KnownHostActivityType.Trusted => Loc.T("신뢰 추가", "TRUSTED"),
            KnownHostActivityType.Rotated => Loc.T("Key 회전", "ROTATED"),
            KnownHostActivityType.Removed => Loc.T("신뢰 삭제", "REMOVED"),
            _ => record.Type.ToString().ToUpperInvariant(),
        };
        KeyChangeText = DescribeKeyChange(record);
        ReasonText = DescribeReason(record.Reason);
        TimestampText = record.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    public KnownHostActivityRecord Record { get; }
    public string TypeText { get; }
    public string TimestampText { get; }
    public string EndpointText => Record.Endpoint.Value;
    public string KeyChangeText { get; }
    public string ReasonText { get; }
    public string AccessibilityName => Loc.T(
        $"Known Hosts 활동, {TypeText}, {EndpointText}, {TimestampText}, {ReasonText}",
        $"Known Hosts activity, {TypeText}, {EndpointText}, {TimestampText}, {ReasonText}");

    private static string DescribeKeyChange(KnownHostActivityRecord record) => record.Type switch
    {
        KnownHostActivityType.Trusted =>
            $"{record.CurrentAlgorithm}\n{record.CurrentSha256Fingerprint}",
        KnownHostActivityType.Rotated => Loc.T(
            $"기존: {record.PreviousAlgorithm} {record.PreviousSha256Fingerprint}\n새 key: {record.CurrentAlgorithm} {record.CurrentSha256Fingerprint}",
            $"Previous: {record.PreviousAlgorithm} {record.PreviousSha256Fingerprint}\nNew: {record.CurrentAlgorithm} {record.CurrentSha256Fingerprint}"),
        KnownHostActivityType.Removed => Loc.T(
            $"삭제됨: {record.PreviousAlgorithm} {record.PreviousSha256Fingerprint}",
            $"Removed: {record.PreviousAlgorithm} {record.PreviousSha256Fingerprint}"),
        _ => "",
    };

    private static string DescribeReason(string reason) => reason switch
    {
        "Initial explicit trust" => Loc.T("사용자가 처음 신뢰함", "Initial explicit trust"),
        "User removed trust" => Loc.T("사용자가 신뢰를 삭제함", "User removed trust"),
        _ => reason,
    };
}

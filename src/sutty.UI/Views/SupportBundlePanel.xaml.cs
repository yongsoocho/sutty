using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Diagnostics;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace sutty.UI.Views;

/// <summary>
/// UI-only description of a support-bundle target. Session and endpoint labels are
/// deliberately kept outside <see cref="SupportBundleContext"/> so they cannot enter
/// the serialized bundle contract.
/// </summary>
public sealed class SupportBundleTarget
{
    public SupportBundleTarget(
        string targetLabel,
        string sessionTitle,
        string endpoint,
        string statusText,
        string stableErrorCode,
        string correlationId,
        int eventCount,
        SupportBundleContext context)
    {
        TargetLabel = targetLabel;
        SessionTitle = sessionTitle;
        Endpoint = endpoint;
        StatusText = statusText;
        StableErrorCode = stableErrorCode;
        CorrelationId = correlationId;
        EventCount = eventCount;
        Context = context;
    }

    // WinUI's generated type metadata requires public setters even though these
    // instances are treated as immutable snapshots by SupportBundlePanel.
    public string TargetLabel { get; set; }
    public string SessionTitle { get; set; }
    public string Endpoint { get; set; }
    public string StatusText { get; set; }
    public string StableErrorCode { get; set; }
    public string CorrelationId { get; set; }
    public int EventCount { get; set; }
    public SupportBundleContext Context { get; set; }
}

public sealed partial class SupportBundlePanel : UserControl
{
    private int _creationInProgress;

    public SupportBundlePanel()
    {
        InitializeComponent();
    }

    public IntPtr OwnerWindowHandle { get; set; }

    public Func<IReadOnlyList<SupportBundleTarget>>? TargetsProvider { get; set; }

    public ObservableCollection<SupportBundleTarget> Targets { get; } = [];

    public void RefreshLanguage()
    {
        Bindings.Update();
        RefreshTargets();
    }

    public void RefreshTargets()
    {
        var selectedCorrelationId =
            (TargetComboBox.SelectedItem as SupportBundleTarget)?.CorrelationId;
        var snapshot = TargetsProvider?.Invoke() ?? [];

        Targets.Clear();
        foreach (var target in snapshot)
            Targets.Add(target);

        TargetComboBox.SelectedItem = Targets.FirstOrDefault(target => string.Equals(
            target.CorrelationId,
            selectedCorrelationId,
            StringComparison.Ordinal)) ?? Targets.FirstOrDefault();
        UpdateTargetPreview();
    }

    private void SupportBundlePanel_Loaded(object sender, RoutedEventArgs e) =>
        RefreshTargets();

    private void TargetComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) => UpdateTargetPreview();

    private async void CreateBundleButton_Click(object sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref _creationInProgress, 1) != 0)
            return;

        CreateBundleButton.IsEnabled = false;
        TargetComboBox.IsEnabled = false;
        try
        {
            if (OwnerWindowHandle == IntPtr.Zero)
            {
                ShowStatus(
                    "파일 선택 창을 열 수 없습니다.",
                    "The file picker is unavailable.",
                    "StatusRed");
                return;
            }

            var requestedTarget = TargetComboBox.SelectedItem as SupportBundleTarget;
            var requestedCorrelationId = requestedTarget?.CorrelationId;
            RefreshTargets();
            var target = TargetComboBox.SelectedItem as SupportBundleTarget;
            if (target is null)
            {
                ShowStatus(
                    "지원 번들을 만들 SSH 연결 기록이 없습니다. 연결을 한 번 시도한 뒤 다시 실행하세요.",
                    "There is no SSH connection attempt to describe. Attempt a connection, then try again.",
                    "StatusAmber");
                return;
            }
            if (requestedCorrelationId is not null &&
                !string.Equals(
                    target.CorrelationId,
                    requestedCorrelationId,
                    StringComparison.Ordinal))
            {
                ShowStatus(
                    "선택한 연결을 더 이상 사용할 수 없습니다. 대상을 다시 확인하세요.",
                    "The selected connection is no longer available. Review the target again.",
                    "StatusAmber");
                return;
            }
            if (requestedTarget is not null && TargetChanged(requestedTarget, target))
            {
                ShowStatus(
                    "대상 연결의 상태가 변경되었습니다. 새 미리보기를 확인한 뒤 다시 저장하세요.",
                    "The selected connection changed. Review the new preview and save again.",
                    "StatusAmber");
                return;
            }

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = $"Sutty-support-{DateTime.Now:yyyyMMdd-HHmmss}",
            };
            picker.FileTypeChoices.Add("ZIP", [".zip"]);
            InitializeWithWindow.Initialize(picker, OwnerWindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is null || string.IsNullOrWhiteSpace(file.Path))
                return;

            var currentCorrelationId =
                (TargetComboBox.SelectedItem as SupportBundleTarget)?.CorrelationId;
            if (!string.Equals(
                    currentCorrelationId,
                    target.CorrelationId,
                    StringComparison.Ordinal))
            {
                RefreshTargets();
                ShowStatus(
                    "저장 중 대상 연결이 변경되었습니다. 미리보기를 확인한 뒤 다시 저장하세요.",
                    "The target changed while saving. Review the preview and save again.",
                    "StatusAmber");
                return;
            }

            // The picker can remain open while the selected session changes. Resolve
            // the same correlation once more so the preview and bundle use the newest
            // credential-free context. A closed tab may legitimately fall back to the
            // retained context captured before the picker opened.
            var refreshedTarget = (TargetsProvider?.Invoke() ?? [])
                .FirstOrDefault(candidate => string.Equals(
                    candidate.CorrelationId,
                    target.CorrelationId,
                    StringComparison.Ordinal));
            if (refreshedTarget is null || TargetChanged(target, refreshedTarget))
            {
                RefreshTargets();
                ShowStatus(
                    "대상 연결의 상태가 변경되었습니다. 미리보기를 확인한 뒤 다시 저장하세요.",
                    "The selected connection changed. Review the preview and save again.",
                    "StatusAmber");
                return;
            }
            var context = refreshedTarget.Context;

            ShowStatus("지원 번들을 만드는 중…", "Creating support bundle…", "TextMuted");
            var result = await Task.Run(() =>
                new SupportBundleService(ConnectionDiagnosticEventStore.Shared)
                    .Create(file.Path, context, overwrite: true));
            ShowStatus(
                $"{file.Name} 저장 완료 · SHA-256 {result.Sha256[..12]}…",
                $"Saved {file.Name} · SHA-256 {result.Sha256[..12]}…",
                "StatusGreen");
        }
        catch (OperationCanceledException)
        {
            // Closing the picker or cancelling bundle creation is not an error.
        }
        catch (Exception error) when (error is SupportBundleDiagnosticCodeMismatchException or
                                             SupportBundleDiagnosticSnapshotChangedException)
        {
            RefreshTargets();
            ShowStatus(
                "대상 연결의 진단 상태가 변경되었습니다. 미리보기를 확인한 뒤 다시 저장하세요.",
                "The selected connection diagnostics changed. Review the preview and save again.",
                "StatusAmber");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            Debug.WriteLine($"Support bundle creation failed: {error.GetType().Name}");
            ShowStatus(
                "지원 번들을 저장하지 못했습니다. 저장 위치와 권한을 확인하세요.",
                "The support bundle could not be saved. Check the destination and permissions.",
                "StatusRed");
        }
        finally
        {
            Volatile.Write(ref _creationInProgress, 0);
            TargetComboBox.IsEnabled = true;
            CreateBundleButton.IsEnabled =
                TargetComboBox.SelectedItem is SupportBundleTarget;
        }
    }

    private static bool TargetChanged(
        SupportBundleTarget before,
        SupportBundleTarget after) =>
        !string.Equals(before.TargetLabel, after.TargetLabel, StringComparison.Ordinal) ||
        !string.Equals(before.SessionTitle, after.SessionTitle, StringComparison.Ordinal) ||
        !string.Equals(before.Endpoint, after.Endpoint, StringComparison.Ordinal) ||
        !string.Equals(before.StatusText, after.StatusText, StringComparison.Ordinal) ||
        !string.Equals(before.StableErrorCode, after.StableErrorCode, StringComparison.Ordinal) ||
        before.EventCount != after.EventCount ||
        !Equals(before.Context, after.Context);

    private void UpdateTargetPreview()
    {
        if (TargetComboBox.SelectedItem is not SupportBundleTarget target)
        {
            TargetPreview.Visibility = Visibility.Collapsed;
            CreateBundleButton.IsEnabled = false;
            return;
        }

        TargetSessionText.Text = target.SessionTitle;
        TargetEndpointText.Text = target.Endpoint;
        TargetStatusText.Text = target.StatusText;
        TargetErrorCodeText.Text = target.StableErrorCode;
        TargetCorrelationText.Text = target.CorrelationId;
        TargetEventCountText.Text = target.EventCount.ToString("N0");
        TargetPreview.Visibility = Visibility.Visible;
        CreateBundleButton.IsEnabled = Volatile.Read(ref _creationInProgress) == 0;
    }

    private void ShowStatus(string korean, string english, string brushKey)
    {
        StatusText.Text = Loc.T(korean, english);
        StatusText.Foreground = ThemeResources.Brush(this, brushKey);
        StatusText.Visibility = Visibility.Visible;
    }
}

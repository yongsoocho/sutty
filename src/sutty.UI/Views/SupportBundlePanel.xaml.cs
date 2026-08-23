using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Diagnostics;
using sutty.UI.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace sutty.UI.Views;

public sealed partial class SupportBundlePanel : UserControl
{
    public SupportBundlePanel()
    {
        InitializeComponent();
    }

    public IntPtr OwnerWindowHandle { get; set; }

    public Func<SupportBundleContext?>? ContextProvider { get; set; }

    public void RefreshLanguage() => Bindings.Update();

    private async void CreateBundleButton_Click(object sender, RoutedEventArgs e)
    {
        if (OwnerWindowHandle == IntPtr.Zero)
        {
            ShowStatus(
                "파일 선택 창을 열 수 없습니다.",
                "The file picker is unavailable.",
                "StatusRed");
            return;
        }

        try
        {
            var context = ContextProvider?.Invoke();
            if (context is null)
            {
                ShowStatus(
                    "지원 번들을 만들 SSH 탭이 없습니다. 연결을 한 번 시도한 뒤 다시 실행하세요.",
                    "There is no SSH tab to describe. Attempt a connection, then try again.",
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

            CreateBundleButton.IsEnabled = false;
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
            CreateBundleButton.IsEnabled = true;
        }
    }

    private void ShowStatus(string korean, string english, string brushKey)
    {
        StatusText.Text = Loc.T(korean, english);
        StatusText.Foreground = ThemeResources.Brush(this, brushKey);
        StatusText.Visibility = Visibility.Visible;
    }
}

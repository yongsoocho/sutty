using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Sftp;
using sutty.Setting;
using System;
using System.Diagnostics;
using System.IO;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace sutty.UI.Views;

public sealed partial class SettingsPanel
{
    private void EditorSettings_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Current.ExternalEditorExecutable = EditorExecutableBox.Text;
        SettingsService.Current.ExternalEditorArguments = EditorArgumentsBox.Text;
        QueueChanges(SettingChangeKind.Sftp);
    }

    private async void SelectExternalEditor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            InitializeWithWindow.Initialize(picker, OwnerWindowHandle);
            if (await picker.PickSingleFileAsync() is { } file) EditorExecutableBox.Text = file.Path;
        }
        catch (Exception)
        {
            ShowSaveStatus("편집기를 선택하지 못했습니다. 절대 경로를 직접 입력하세요.", "Could not select an editor. Enter its absolute path.", "StatusRed");
        }
    }

    private void OpenEditRecovery_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(RemoteEditSession.DefaultStorageRoot);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
            start.ArgumentList.Add(RemoteEditSession.DefaultStorageRoot);
            using var process = Process.Start(start);
        }
        catch (Exception)
        {
            ShowSaveStatus("보관 폴더를 열 수 없습니다.", "Could not open the recovery folder.", "StatusRed");
        }
    }
}

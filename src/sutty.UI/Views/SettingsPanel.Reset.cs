using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Command;
using sutty.Setting;
using sutty.UI.Helpers;
using System;
using System.Threading.Tasks;

namespace sutty.UI.Views;

public sealed partial class SettingsPanel
{
    private async void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_resetInProgress) return;
        SetResetInProgress(true);
        try
        {
            if (!await ConfirmResetAsync(
                    Loc.T("설정을 초기화할까요?", "Reset settings?"),
                    Loc.T(
                        "테마·언어·터미널·연결 기본값·창 설정 등 모든 앱 설정을 기본값으로 되돌립니다. 저장 Host와 SQLite 데이터는 유지됩니다. 계속할까요?",
                        "Restore all app preferences, including themes, language, terminal, connection defaults, and window settings. Saved hosts and SQLite data are kept. Continue?")))
                return;

            // A queued autosave or control rebind must not restore pre-reset values.
            _saveTimer.Stop();
            var previousPending = _pendingChanges;
            _pendingChanges = SettingChangeKind.None;
            _loading = true;
            var result = SettingsService.ResetToDefaults();
            if (!result.Succeeded)
            {
                _loading = false;
                _pendingChanges = previousPending;
                ShowResetStatus(
                    "설정을 초기화하지 못했습니다. 파일 접근 권한과 저장 공간을 확인하세요.",
                    "Could not reset settings. Check file access and available storage.", false);
                return;
            }

            LoadSettingsControls(SettingsService.Current);
            _loading = false;
            Bindings.Update();
            KnownHostsPanel.RefreshLanguage();
            ConnectionLogsPanel.RefreshLanguage();
            SupportBundlePanel.RefreshLanguage();
            ThemeChanged?.Invoke(this, SettingsService.Current.Theme);
            SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(SettingChangeKind.All));
            Saved?.Invoke(this, EventArgs.Empty);
            ShowSaveStatus("설정을 초기화했습니다.", "Settings reset.", "StatusGreen");
            ShowResetStatus("설정을 기본값으로 초기화했습니다.", "Settings restored to defaults.", true);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Settings reset failed: {error.GetType().Name}");
            ShowResetStatus("설정 초기화 중 오류가 발생했습니다.", "An error occurred while resetting settings.", false);
        }
        finally
        {
            _loading = false;
            SetResetInProgress(false);
        }
    }

    private async void ResetDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (_resetInProgress) return;
        SetResetInProgress(true);
        try
        {
            if (!await ConfirmResetAsync(
                    Loc.T("SQLite 데이터를 초기화할까요?", "Reset SQLite data?"),
                    Loc.T(
                        "저장 Host, 접속 기록, 명령 템플릿, 연결 즐겨찾기와 최근 기록 등 sutty.db의 모든 사용자 데이터를 삭제합니다. 이 작업은 되돌릴 수 없습니다. 앱 설정·키 신뢰·암호화 금고·전송 복구 파일과 열린 세션은 유지됩니다. 계속할까요?",
                        "Delete all user data in sutty.db, including saved hosts, connection history, command templates, launcher favorites, and launcher history. This cannot be undone. App settings, trusted keys, encrypted vault, transfer recovery files, and open sessions are kept. Continue?")))
                return;

            await Task.Run(LocalDatabaseMaintenance.Reset);
            SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(
                SettingChangeKind.Database | SettingChangeKind.HostProfiles | SettingChangeKind.History));
            ShowResetStatus("SQLite 데이터를 초기화했습니다.", "SQLite data reset.", true);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"SQLite reset failed: {error.GetType().Name}");
            ShowResetStatus(
                "SQLite 초기화를 완료하지 못했습니다. 데이터베이스 접근 상태를 확인한 후 다시 시도하세요.",
                "Could not complete the SQLite reset. Check database access and try again.", false);
        }
        finally
        {
            SetResetInProgress(false);
        }
    }

    private async Task<bool> ConfirmResetAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.T("예", "Yes"),
            PrimaryButtonStyle = (Style)Resources["SettingsDangerConfirmationButton"],
            CloseButtonText = Loc.T("아니오", "No"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SetResetInProgress(bool value)
    {
        _resetInProgress = value;
        ResetSettingsButton.IsEnabled = !value;
        ResetDatabaseButton.IsEnabled = !value;
    }

    private void ShowResetStatus(string korean, string english, bool succeeded)
    {
        ResetStatusText.Text = Loc.T(korean, english);
        ResetStatusText.Foreground = ThemeResources.Brush(this, succeeded ? "StatusGreen" : "StatusRed");
        ResetStatusText.Visibility = Visibility.Visible;
    }
}

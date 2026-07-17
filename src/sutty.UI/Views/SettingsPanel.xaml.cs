using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Setting;
using sutty.UI.Helpers;
using System;
using System.Linq;

namespace sutty.UI.Views
{
    /// <summary>
    /// 설정 창 내용. 왼쪽 네비게이션으로 섹션(테마/터미널/연결/창/동작)을 전환한다.
    /// - Appearance의 테마는 선택 즉시 적용 + 저장
    /// - 나머지는 왼쪽 아래 Save 버튼으로 저장
    /// </summary>
    public sealed partial class SettingsPanel : UserControl
    {
        /// <summary>Save를 눌러 설정이 저장되면 발생 (창 크기 즉시 적용 등에 사용).</summary>
        public event EventHandler? Saved;

        /// <summary>테마가 바뀌면 새 테마 이름과 함께 발생. MainWindow가 받아서 적용한다.</summary>
        public event EventHandler<string>? ThemeChanged;

        private bool _loading = true;

        public SettingsPanel()
        {
            InitializeComponent();

            var s = SettingsService.Current;
            RequestedTheme = ThemeManager.IsDark(s.Theme) ? ElementTheme.Dark : ElementTheme.Light;

            // 테마 목록 채우기
            foreach (var preset in ThemeManager.Presets)
                ThemeRadios.Items.Add(preset.Name);
            ThemeRadios.SelectedItem = ThemeManager.Find(s.Theme).Name;

            FontFamilyBox.Text = s.TerminalFontFamily;
            FontSizeBox.Value = s.TerminalFontSize;
            DefaultPortBox.Value = s.DefaultSshPort;
            KeepAliveBox.Value = s.DefaultKeepAliveSeconds;
            ConfirmCloseToggle.IsOn = s.ConfirmOnTabClose;

            // 창 크기 — 0(기본값 사용)이면 빈 칸으로
            MainWidthBox.Value = s.MainWindowWidth > 0 ? s.MainWindowWidth : double.NaN;
            MainHeightBox.Value = s.MainWindowHeight > 0 ? s.MainWindowHeight : double.NaN;
            SettingWidthBox.Value = s.SettingWindowWidth > 0 ? s.SettingWindowWidth : double.NaN;
            SettingHeightBox.Value = s.SettingWindowHeight > 0 ? s.SettingWindowHeight : double.NaN;

            SettingsNav.SelectedItem = SettingsNav.MenuItems.First();
            _loading = false;
        }

        // ── 섹션 전환 ──

        private void SettingsNav_SelectionChanged(
            NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem item) return;
            var tag = item.Tag as string;

            AppearancePane.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
            TerminalPane.Visibility = tag == "Terminal" ? Visibility.Visible : Visibility.Collapsed;
            ConnectionPane.Visibility = tag == "Connection" ? Visibility.Visible : Visibility.Collapsed;
            WindowPane.Visibility = tag == "Window" ? Visibility.Visible : Visibility.Collapsed;
            BehaviorPane.Visibility = tag == "Behavior" ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── 테마: 선택 즉시 적용 + 저장 ──

        private void ThemeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ThemeRadios.SelectedItem is not string themeName) return;

            var s = SettingsService.Current;
            s.Theme = themeName;
            SettingsService.Save();

            RequestedTheme = ThemeManager.IsDark(themeName) ? ElementTheme.Dark : ElementTheme.Light;
            ThemeChanged?.Invoke(this, themeName);
        }

        // ── 저장 (현재 화면 값 포함 전체) ──

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // Current를 직접 수정해야 Theme 등 이 화면에 없는 설정이 보존된다
            var s = SettingsService.Current;

            if (!string.IsNullOrWhiteSpace(FontFamilyBox.Text))
                s.TerminalFontFamily = FontFamilyBox.Text.Trim();
            if (!double.IsNaN(FontSizeBox.Value))
                s.TerminalFontSize = (int)FontSizeBox.Value;
            if (!double.IsNaN(DefaultPortBox.Value))
                s.DefaultSshPort = (int)DefaultPortBox.Value;
            if (!double.IsNaN(KeepAliveBox.Value))
                s.DefaultKeepAliveSeconds = (int)KeepAliveBox.Value;
            s.ConfirmOnTabClose = ConfirmCloseToggle.IsOn;

            if (!double.IsNaN(MainWidthBox.Value))
                s.MainWindowWidth = (int)MainWidthBox.Value;
            if (!double.IsNaN(MainHeightBox.Value))
                s.MainWindowHeight = (int)MainHeightBox.Value;
            if (!double.IsNaN(SettingWidthBox.Value))
                s.SettingWindowWidth = (int)SettingWidthBox.Value;
            if (!double.IsNaN(SettingHeightBox.Value))
                s.SettingWindowHeight = (int)SettingHeightBox.Value;

            SettingsService.Save();
            SavedText.Visibility = Visibility.Visible;
            Saved?.Invoke(this, EventArgs.Empty);
        }
    }
}

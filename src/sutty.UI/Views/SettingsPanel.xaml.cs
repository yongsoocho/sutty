using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Setting;
using sutty.UI.Helpers;
using System;
using System.IO;
using System.Linq;

namespace sutty.UI.Views
{
    [Flags]
    public enum SettingChangeKind
    {
        None = 0,
        Language = 1 << 0,
        Theme = 1 << 1,
        TerminalAppearance = 1 << 2,
        TerminalMode = 1 << 3,
        ConnectionPort = 1 << 4,
        ConnectionKeepAlive = 1 << 5,
        Connection = ConnectionPort | ConnectionKeepAlive,
        History = 1 << 6,
        Window = 1 << 7,
        TerminalFeatures = 1 << 8,
        All = Language | Theme | TerminalAppearance | TerminalMode | TerminalFeatures |
              Connection | History | Window,
    }

    public sealed class SettingsChangedEventArgs : EventArgs
    {
        public SettingsChangedEventArgs(SettingChangeKind changes)
        {
            Changes = changes;
        }

        public SettingChangeKind Changes { get; }
    }

    /// <summary>
    /// Settings editor. Discrete selections are committed immediately; free-form and
    /// numeric edits are committed after a short debounce to avoid writing once per key.
    /// </summary>
    public sealed partial class SettingsPanel : UserControl
    {
        /// <summary>Raised after settings have been written. Kept for existing consumers.</summary>
        public event EventHandler? Saved;

        /// <summary>Raised with the affected setting groups after an automatic or manual save.</summary>
        public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

        /// <summary>Raised when the selected theme changes.</summary>
        public event EventHandler<string>? ThemeChanged;

        private readonly DispatcherQueueTimer _saveTimer;
        private bool _loading = true;
        private SettingChangeKind _pendingChanges;

        public SettingsPanel()
        {
            InitializeComponent();

            _saveTimer = DispatcherQueue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(250);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => CommitPendingChanges();

            var settings = SettingsService.Current;
            RequestedTheme = ThemeManager.IsDark(settings.Theme)
                ? ElementTheme.Dark
                : ElementTheme.Light;

            foreach (var preset in ThemeManager.Presets)
                ThemeRadios.Items.Add(preset.Name);

            ThemeRadios.SelectedItem = ThemeManager.Find(settings.Theme).Name;
            DarkModeToggle.IsOn = ThemeManager.IsDark(settings.Theme);
            LanguageCombo.SelectedIndex = settings.Language == "en" ? 1 : 0;

            FontFamilyBox.Text = settings.TerminalFontFamily;
            FontSizeBox.Value = settings.TerminalFontSize;
            TerminalModeRadios.SelectedIndex = settings.TerminalMode == "Terminal" ? 1 : 0;
            StructuredHighlightToggle.IsOn = settings.EnableStructuredTextHighlighting;
            SeverityHighlightToggle.IsOn = settings.EnableSeverityHighlighting;
            CommandSuggestionToggle.IsOn = settings.EnableCommandSuggestions;
            SuggestionTabToggle.IsOn = settings.AcceptSuggestionWithTab;
            DefaultPortBox.Value = settings.DefaultSshPort;
            KeepAliveBox.Value = settings.DefaultKeepAliveSeconds;
            HistoryDaysBox.Value = settings.HistoryRetentionDays;
            HistoryTopHostCountBox.Value = settings.HistoryTopHostCount;

            MainWidthBox.Value = PositiveOrNaN(settings.MainWindowWidth);
            MainHeightBox.Value = PositiveOrNaN(settings.MainWindowHeight);
            SettingWidthBox.Value = PositiveOrNaN(settings.SettingWindowWidth);
            SettingHeightBox.Value = PositiveOrNaN(settings.SettingWindowHeight);
            PanelWidthBox.Value = PositiveOrNaN(settings.RightPanelWidth);

            SettingsNav.SelectedItem = SettingsNav.MenuItems.First();
            _loading = false;
        }

        private static double PositiveOrNaN(int value) => value > 0 ? value : double.NaN;

        private void SettingsNav_SelectionChanged(
            NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem item)
                return;

            var tag = item.Tag as string;
            AppearancePane.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
            TerminalPane.Visibility = tag == "Terminal" ? Visibility.Visible : Visibility.Collapsed;
            ConnectionPane.Visibility = tag == "Connection" ? Visibility.Visible : Visibility.Collapsed;
            WindowPane.Visibility = tag == "Window" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ThemeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ThemeRadios.SelectedItem is not string themeName)
                return;

            ApplyThemeChoice(themeName);
        }

        private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
                return;

            SettingsService.Current.Language = LanguageCombo.SelectedIndex == 1 ? "en" : "ko";
            CommitChangesNow(SettingChangeKind.Language);
        }

        private void DarkModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            ApplyThemeChoice(DarkModeToggle.IsOn ? "Dark" : "Light");
        }

        private void ApplyThemeChoice(string themeName)
        {
            var preset = ThemeManager.Find(themeName);

            // Keep the preset list and the convenience dark/light switch in sync without
            // recursively committing either control's programmatic change.
            _loading = true;
            ThemeRadios.SelectedItem = preset.Name;
            DarkModeToggle.IsOn = preset.IsDark;
            _loading = false;

            SettingsService.Current.Theme = preset.Name;
            RequestedTheme = preset.IsDark ? ElementTheme.Dark : ElementTheme.Light;
            CommitChangesNow(SettingChangeKind.Theme);
        }

        private void TerminalModeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
                return;

            SettingsService.Current.TerminalMode =
                TerminalModeRadios.SelectedIndex == 1 ? "Terminal" : "Repl";
            CommitChangesNow(SettingChangeKind.TerminalMode);
        }

        private void TerminalFeatureToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            var settings = SettingsService.Current;
            settings.EnableStructuredTextHighlighting = StructuredHighlightToggle.IsOn;
            settings.EnableSeverityHighlighting = SeverityHighlightToggle.IsOn;
            settings.EnableCommandSuggestions = CommandSuggestionToggle.IsOn;
            settings.AcceptSuggestionWithTab = SuggestionTabToggle.IsOn;
            CommitChangesNow(SettingChangeKind.TerminalFeatures);
        }

        private void FontFamilyBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading)
                return;

            if (!string.IsNullOrWhiteSpace(FontFamilyBox.Text))
                SettingsService.Current.TerminalFontFamily = FontFamilyBox.Text.Trim();
            QueueChanges(SettingChangeKind.TerminalAppearance);
        }

        private void FontFamilyBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            if (string.IsNullOrWhiteSpace(FontFamilyBox.Text))
            {
                _loading = true;
                FontFamilyBox.Text = SettingsService.Current.TerminalFontFamily;
                _loading = false;
            }

            CommitChangesNow(SettingChangeKind.TerminalAppearance);
        }

        private void NumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loading)
                return;

            if (double.IsNaN(sender.Value))
                return;

            var settings = SettingsService.Current;
            var value = (int)sender.Value;
            if (ReferenceEquals(sender, FontSizeBox)) settings.TerminalFontSize = value;
            else if (ReferenceEquals(sender, DefaultPortBox)) settings.DefaultSshPort = value;
            else if (ReferenceEquals(sender, KeepAliveBox)) settings.DefaultKeepAliveSeconds = value;
            else if (ReferenceEquals(sender, HistoryDaysBox)) settings.HistoryRetentionDays = value;
            else if (ReferenceEquals(sender, HistoryTopHostCountBox)) settings.HistoryTopHostCount = value;
            else if (ReferenceEquals(sender, MainWidthBox)) settings.MainWindowWidth = value;
            else if (ReferenceEquals(sender, MainHeightBox)) settings.MainWindowHeight = value;
            else if (ReferenceEquals(sender, SettingWidthBox)) settings.SettingWindowWidth = value;
            else if (ReferenceEquals(sender, SettingHeightBox)) settings.SettingWindowHeight = value;
            else if (ReferenceEquals(sender, PanelWidthBox)) settings.RightPanelWidth = value;

            var kind = ReferenceEquals(sender, FontSizeBox)
                ? SettingChangeKind.TerminalAppearance
                : ReferenceEquals(sender, DefaultPortBox)
                    ? SettingChangeKind.ConnectionPort
                    : ReferenceEquals(sender, KeepAliveBox)
                        ? SettingChangeKind.ConnectionKeepAlive
                        : ReferenceEquals(sender, HistoryDaysBox) ||
                          ReferenceEquals(sender, HistoryTopHostCountBox)
                            ? SettingChangeKind.History
                            : SettingChangeKind.Window;

            QueueChanges(kind);
        }

        private void QueueChanges(SettingChangeKind changes)
        {
            _pendingChanges |= changes;
            ShowSaveStatus("저장 중…", "Saving…", "TextMuted");
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void CommitChangesNow(SettingChangeKind changes)
        {
            _pendingChanges |= changes;
            _saveTimer.Stop();
            CommitPendingChanges();
        }

        private void CommitPendingChanges()
        {
            var changes = _pendingChanges;
            if (_loading || changes == SettingChangeKind.None)
                return;

            var result = SettingsService.Save();
            if (!result.Succeeded)
            {
                // Keep every dirty flag so "Save now" or the next edit can retry the
                // complete in-memory settings snapshot. No persistence exception escapes
                // this dispatcher callback.
                _pendingChanges |= changes;
                ShowSaveFailure(result.Error);
                return;
            }

            _pendingChanges &= ~changes;

            if (changes.HasFlag(SettingChangeKind.Language))
                Bindings.Update();

            ShowSaveStatus("자동 저장됨", "Saved automatically", "StatusGreen");

            if (changes.HasFlag(SettingChangeKind.Theme))
                ThemeChanged?.Invoke(this, SettingsService.Current.Theme);

            SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(changes));
            Saved?.Invoke(this, EventArgs.Empty);
        }

        private void ShowSaveFailure(Exception? error)
        {
            var (ko, en) = error switch
            {
                UnauthorizedAccessException or System.Security.SecurityException =>
                    ("저장 실패 — 파일 접근 권한을 확인하세요.",
                        "Save failed — check file permissions."),
                IOException =>
                    ("저장 실패 — 설정 파일을 사용할 수 없습니다. 다시 시도하세요.",
                        "Save failed — the settings file is unavailable. Try again."),
                _ =>
                    ("저장 실패 — 다시 시도하세요.", "Save failed — try again."),
            };

            ShowSaveStatus(ko, en, "StatusRed");
            System.Diagnostics.Debug.WriteLine($"Settings save failed: {error}");
        }

        private void ShowSaveStatus(string korean, string english, string brushKey)
        {
            SavedText.Text = Loc.T(korean, english);
            SavedText.Foreground = ThemeResources.Brush(this, brushKey);
            SavedText.Visibility = Visibility.Visible;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingChanges != SettingChangeKind.None)
                CommitChangesNow(_pendingChanges);
        }

        private void SettingsPanel_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_pendingChanges != SettingChangeKind.None)
            {
                _saveTimer.Stop();
                CommitPendingChanges();
            }
        }
    }
}

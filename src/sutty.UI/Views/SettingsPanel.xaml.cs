using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Setting;
using sutty.UI.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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
        private IReadOnlyList<string> _installedFonts = [];

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
            PopulateTerminalAppearanceChoices(settings);
            ScrollbackBox.Value = settings.TerminalScrollbackLines;
            CursorBlinkToggle.IsOn = settings.TerminalCursorBlink;
            ScreenReaderToggle.IsOn = settings.TerminalScreenReaderMode;
            LoadShellProfileToggle.IsOn = settings.LoadLocalShellProfile;
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
            _ = LoadInstalledFontsAsync();
        }

        private static double PositiveOrNaN(int value) => value > 0 ? value : double.NaN;

        private void PopulateTerminalAppearanceChoices(AppSettings settings)
        {
            TerminalThemeCombo.Items.Clear();
            TerminalThemeCombo.Items.Add(new ComboBoxItem
            {
                Content = Loc.T("앱 테마에 맞춤", "Follow application theme"),
                Tag = TerminalThemeCatalog.FollowApplication,
            });
            foreach (var preset in TerminalThemeCatalog.Presets)
            {
                TerminalThemeCombo.Items.Add(new ComboBoxItem
                {
                    Content = preset.DisplayName,
                    Tag = preset.Id,
                });
            }

            TerminalThemeCombo.SelectedItem = TerminalThemeCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.TerminalTheme,
                    StringComparison.OrdinalIgnoreCase))
                ?? TerminalThemeCombo.Items[0];

            CursorStyleCombo.Items.Clear();
            AddCursorChoice("underline", Loc.T("밑줄 — 글자를 가리지 않음", "Underline — does not cover text"));
            AddCursorChoice("bar", Loc.T("얇은 세로선", "Thin bar"));
            AddCursorChoice("block", Loc.T("블록", "Block"));
            CursorStyleCombo.SelectedItem = CursorStyleCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.TerminalCursorStyle,
                    StringComparison.OrdinalIgnoreCase))
                ?? CursorStyleCombo.Items[0];
        }

        private void AddCursorChoice(string id, string displayName) =>
            CursorStyleCombo.Items.Add(new ComboBoxItem { Content = displayName, Tag = id });

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
            _loading = true;
            PopulateTerminalAppearanceChoices(SettingsService.Current);
            _loading = false;
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

        private void TerminalAppearanceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
                return;

            var settings = SettingsService.Current;
            if (TerminalThemeCombo.SelectedItem is ComboBoxItem themeItem &&
                themeItem.Tag is string terminalTheme)
            {
                settings.TerminalTheme = terminalTheme;
            }

            if (CursorStyleCombo.SelectedItem is ComboBoxItem cursorItem &&
                cursorItem.Tag is string cursorStyle)
            {
                settings.TerminalCursorStyle = cursorStyle;
            }

            CommitChangesNow(SettingChangeKind.TerminalAppearance);
        }

        private void TerminalAppearanceToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            SettingsService.Current.TerminalCursorBlink = CursorBlinkToggle.IsOn;
            SettingsService.Current.TerminalScreenReaderMode = ScreenReaderToggle.IsOn;
            SettingsService.Current.LoadLocalShellProfile = LoadShellProfileToggle.IsOn;
            CommitChangesNow(SettingChangeKind.TerminalAppearance);
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

        private async Task LoadInstalledFontsAsync()
        {
            try
            {
                var fonts = await InstalledFontCatalog.GetAsync();
                var current = SettingsService.Current.TerminalFontFamily.Trim();
                _installedFonts = string.IsNullOrWhiteSpace(current) ||
                                  fonts.Contains(current, StringComparer.CurrentCultureIgnoreCase)
                    ? fonts
                    : [current, .. fonts];
                FontFamilyBox.ItemsSource = _installedFonts;
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Installed font enumeration failed: {error.GetType().Name}");
            }
        }

        private void FontFamilyBox_TextChanged(
            AutoSuggestBox sender,
            AutoSuggestBoxTextChangedEventArgs args)
        {
            if (_loading)
                return;

            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                var query = sender.Text.Trim();
                sender.ItemsSource = _installedFonts
                    .Where(font => query.Length == 0 ||
                                   font.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    .OrderBy(font => !font.StartsWith(
                        query,
                        StringComparison.CurrentCultureIgnoreCase))
                    .ThenBy(font => font, StringComparer.CurrentCultureIgnoreCase)
                    .Take(100)
                    .ToArray();
            }

            ApplyFontFamilyText(commitImmediately: false);
        }

        private void FontFamilyBox_SuggestionChosen(
            AutoSuggestBox sender,
            AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            if (args.SelectedItem is string font)
                sender.Text = font;
            ApplyFontFamilyText(commitImmediately: true);
        }

        private void FontFamilyBox_QuerySubmitted(
            AutoSuggestBox sender,
            AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (args.ChosenSuggestion is string font)
                sender.Text = font;
            ApplyFontFamilyText(commitImmediately: true);
        }

        private void ApplyFontFamilyText(bool commitImmediately)
        {
            if (_loading || string.IsNullOrWhiteSpace(FontFamilyBox.Text))
                return;

            SettingsService.Current.TerminalFontFamily = FontFamilyBox.Text.Trim();
            if (commitImmediately)
                CommitChangesNow(SettingChangeKind.TerminalAppearance);
            else
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
            else if (ReferenceEquals(sender, ScrollbackBox)) settings.TerminalScrollbackLines = value;
            else if (ReferenceEquals(sender, DefaultPortBox)) settings.DefaultSshPort = value;
            else if (ReferenceEquals(sender, KeepAliveBox)) settings.DefaultKeepAliveSeconds = value;
            else if (ReferenceEquals(sender, HistoryDaysBox)) settings.HistoryRetentionDays = value;
            else if (ReferenceEquals(sender, HistoryTopHostCountBox)) settings.HistoryTopHostCount = value;
            else if (ReferenceEquals(sender, MainWidthBox)) settings.MainWindowWidth = value;
            else if (ReferenceEquals(sender, MainHeightBox)) settings.MainWindowHeight = value;
            else if (ReferenceEquals(sender, SettingWidthBox)) settings.SettingWindowWidth = value;
            else if (ReferenceEquals(sender, SettingHeightBox)) settings.SettingWindowHeight = value;
            else if (ReferenceEquals(sender, PanelWidthBox)) settings.RightPanelWidth = value;

            var kind = ReferenceEquals(sender, FontSizeBox) || ReferenceEquals(sender, ScrollbackBox)
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

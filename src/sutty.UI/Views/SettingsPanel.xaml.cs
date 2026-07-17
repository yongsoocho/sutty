using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Setting;

namespace sutty.UI.Views
{
    /// <summary>설정 창 내용. sutty.Setting의 SettingsService로 로드/저장한다.</summary>
    public sealed partial class SettingsPanel : UserControl
    {
        public SettingsPanel()
        {
            InitializeComponent();

            var s = SettingsService.Current;
            FontFamilyBox.Text = s.TerminalFontFamily;
            FontSizeBox.Value = s.TerminalFontSize;
            DefaultPortBox.Value = s.DefaultSshPort;
            KeepAliveBox.Value = s.DefaultKeepAliveSeconds;
            ConfirmCloseToggle.IsOn = s.ConfirmOnTabClose;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var current = SettingsService.Current;

            SettingsService.Save(new AppSettings
            {
                TerminalFontFamily = string.IsNullOrWhiteSpace(FontFamilyBox.Text)
                    ? current.TerminalFontFamily : FontFamilyBox.Text.Trim(),
                TerminalFontSize = double.IsNaN(FontSizeBox.Value)
                    ? current.TerminalFontSize : (int)FontSizeBox.Value,
                DefaultSshPort = double.IsNaN(DefaultPortBox.Value)
                    ? current.DefaultSshPort : (int)DefaultPortBox.Value,
                DefaultKeepAliveSeconds = double.IsNaN(KeepAliveBox.Value)
                    ? current.DefaultKeepAliveSeconds : (int)KeepAliveBox.Value,
                ConfirmOnTabClose = ConfirmCloseToggle.IsOn,
            });

            SavedText.Visibility = Visibility.Visible;
        }
    }
}

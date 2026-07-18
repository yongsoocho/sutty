using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using sutty.Command;
using sutty.Setting;
using sutty.UI.ViewModels;
using System;
using System.Collections.ObjectModel;

namespace sutty.UI.Views
{
    /// <summary>
    /// Multi command 오른쪽 패널.
    /// 위: 브로드캐스트 입력(멀티라인, Enter=실행), 아래: 저장된 playbook 목록.
    /// 실행되는 모든 명령은 BroadcastRequested로 나가 체크된 모든 세션에 전송된다.
    /// </summary>
    public sealed partial class MultiCommandPanel : UserControl
    {
        public ObservableCollection<CommandItemVm> Items { get; } = [];

        /// <summary>체크된 모든 세션에서 이 명령을 실행해 달라는 신호.</summary>
        public event EventHandler<string>? BroadcastRequested;

        public MultiCommandPanel()
        {
            InitializeComponent();

            var settings = SettingsService.Current;
            BroadcastBox.FontFamily = new FontFamily(settings.TerminalFontFamily + ", Consolas");

            foreach (var template in CommandStore.GetAll())
                Items.Add(new CommandItemVm(template));
            EmptyText.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // WinUI TextBox는 줄구분자로 '\r'을 쓴다
        private static string NormalizeNewlines(string text)
            => text.Replace("\r\n", "\n").Replace('\r', '\n');

        // ── 브로드캐스트 입력 (SessionView 입력줄과 같은 규칙) ──

        private void BroadcastBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;

            var shiftDown = Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shiftDown) return;

            var caret = Math.Min(BroadcastBox.SelectionStart, BroadcastBox.Text.Length);
            var currentLine = NormalizeNewlines(BroadcastBox.Text[..caret]).Split('\n')[^1].TrimEnd();
            if (currentLine.EndsWith('\\') || currentLine.EndsWith('`')) return;

            e.Handled = true;
            RunBroadcastFromBox();
        }

        private void RunBroadcast_Click(object sender, RoutedEventArgs e)
            => RunBroadcastFromBox();

        private void RunBroadcastFromBox()
        {
            var command = NormalizeNewlines(BroadcastBox.Text).Trim();
            if (command.Length == 0) return;

            BroadcastBox.Text = "";
            BroadcastRequested?.Invoke(this, command);
        }

        // ── 저장된 커맨드 실행 ──

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CommandItemVm vm) return;

            if (vm.ParamNumbers.Count > 0)
            {
                vm.PrepareParams();
                vm.ShowParams = !vm.ShowParams;
            }
            else
            {
                Execute(vm);
            }
        }

        private void Execute_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CommandItemVm vm)
                Execute(vm);
        }

        private void Execute(CommandItemVm vm)
        {
            var command = vm.BuildCommand();
            CommandStore.IncrementUsage(vm.Template.Id);
            vm.ShowParams = false;
            BroadcastRequested?.Invoke(this, command);
        }
    }
}

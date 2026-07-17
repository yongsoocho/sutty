using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Command;
using sutty.UI.ViewModels;
using System;
using System.Collections.ObjectModel;

namespace sutty.UI.Views
{
    /// <summary>
    /// 자주 쓰는 명령어 playbook (SQLite에 저장).
    /// $1, $2 자리표시자가 있으면 Run 시 값을 입력받아 치환한 뒤
    /// 현재 선택된 세션 탭에 명령을 흘려보낸다 (RunRequested).
    /// </summary>
    public sealed partial class CommandPanel : UserControl
    {
        public ObservableCollection<CommandItemVm> Items { get; } = [];

        /// <summary>치환 완료된 최종 명령을 현재 세션에서 실행해 달라는 신호.</summary>
        public event EventHandler<string>? RunRequested;

        public CommandPanel()
        {
            InitializeComponent();
            Load();
        }

        private void Load()
        {
            Items.Clear();
            foreach (var template in CommandStore.GetAll())
                Items.Add(new CommandItemVm(template));

            EmptyText.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var name = NewNameBox.Text.Trim();
            var text = NewTextBox.Text.Trim();
            if (name.Length == 0 || text.Length == 0) return;

            var template = CommandStore.Add(name, text);
            Items.Insert(0, new CommandItemVm(template));
            EmptyText.Visibility = Visibility.Collapsed;

            NewNameBox.Text = "";
            NewTextBox.Text = "";
            AddExpander.IsExpanded = false;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CommandItemVm vm) return;

            CommandStore.Delete(vm.Template.Id);
            Items.Remove(vm);
            EmptyText.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CommandItemVm vm) return;

            if (vm.ParamNumbers.Count > 0)
            {
                // 자리표시자가 있으면 입력칸을 펼친다 (다시 누르면 접힘)
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
            CommandStore.IncrementUsage(vm.Template.Id); // 다음에 위로 올라오게
            vm.ShowParams = false;
            RunRequested?.Invoke(this, command);
        }
    }
}

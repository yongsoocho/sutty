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
        public void RefreshLanguage() => Bindings.Update();

        private readonly System.Collections.Generic.List<CommandItemVm> _all = [];
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
            _all.Clear();
            foreach (var template in CommandStore.GetAll())
                _all.Add(new CommandItemVm(template));
            ApplyFilter("");
        }

        // ── 검색 ──

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                ApplyFilter(sender.Text);
        }

        private void ApplyFilter(string query)
        {
            var q = query.Trim();

            Items.Clear();
            foreach (var vm in _all)
            {
                if (q.Length == 0 ||
                    vm.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    vm.CommandText.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    Items.Add(vm);
                }
            }

            EmptyText.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var name = NewNameBox.Text.Trim();
            var text = NewTextBox.Text.Trim();
            if (name.Length == 0 || text.Length == 0) return;

            var template = CommandStore.Add(name, text);
            _all.Insert(0, new CommandItemVm(template));
            ApplyFilter(SearchBox.Text);

            NewNameBox.Text = "";
            NewTextBox.Text = "";
            AddExpander.IsExpanded = false;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CommandItemVm vm) return;

            CommandStore.Delete(vm.Template.Id);
            _all.Remove(vm);
            ApplyFilter(SearchBox.Text);
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

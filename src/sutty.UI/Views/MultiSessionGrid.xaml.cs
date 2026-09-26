using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.Core.Sessions;
using sutty.Core.Terminal;
using sutty.UI.Services;
using sutty.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace sutty.UI.Views
{
    /// <summary>Fixed nine-card display; pagination remains available for a future enabled mode.</summary>
    public sealed partial class MultiSessionGrid : UserControl
    {
        public const int SlotCount = 9;
        private IReadOnlyList<FrameworkElement> _views = [];
        private bool _watchSessionStates;
        private readonly MultiSessionSelectionState<FrameworkElement, MultiSlotVm> _selection = new(
            view => new MultiSlotVm
            {
                View = view as SessionView,
                LocalView = view as LocalTerminalView,
            },
            slot => slot.SessionKey as FrameworkElement,
            slot => slot.IsSelected,
            (slot, selected) => slot.IsSelected = selected,
            slot => slot.CanBroadcast);

        public bool IsPaginationEnabled => _selection.IsPaginationEnabled;

        /// <summary>The current nine cards, including non-selectable placeholders.</summary>
        public ObservableCollection<MultiSlotVm> Slots { get; } = [];

        public MultiSessionGrid()
        {
            InitializeComponent();
            _selection.SetPaginationEnabled(false);
            Loaded += Grid_Loaded;
            Unloaded += Grid_Unloaded;
            CellsScrollViewer.LayoutUpdated += Cells_LayoutUpdated;
            ShowPage();
        }

        public void RefreshLanguage()
        {
            Bindings.Update();
            SetSessions(_views);
        }

        private void Cells_LayoutUpdated(object? sender, object e)
        {
            // Viewport dimensions already exclude the ScrollViewer's actual chrome.
            // Fixed cell coordinates never drop a column at fractional widths or DPI.
            var geometry = MultiSessionGridGeometry.FromViewport(
                CellsScrollViewer.ViewportWidth, CellsScrollViewer.ViewportHeight);
            if (CellsRepeater.Width != geometry.Width) CellsRepeater.Width = geometry.Width;
            if (CellsRepeater.Height != geometry.Height) CellsRepeater.Height = geometry.Height;
        }

        /// <summary>Refresh cards while retaining the slot objects held by running broadcasts.</summary>
        public void SetSessions(IReadOnlyList<FrameworkElement> views)
        {
            _views = views.Where(view => view is SessionView or LocalTerminalView).ToArray();
            foreach (var slot in _selection.AllSlots)
            {
                slot.PropertyChanged -= Slot_PropertyChanged;
                UnwatchSessionState(slot);
            }

            _selection.SetSessions(_views);

            foreach (var slot in _selection.AllSlots)
            {
                slot.RefreshSessionDetails();
                slot.PropertyChanged += Slot_PropertyChanged;
                if (_watchSessionStates) WatchSessionState(slot);
            }
            ShowPage();
        }

        private void Grid_Loaded(object sender, RoutedEventArgs e)
        {
            if (_watchSessionStates) return;
            _watchSessionStates = true;
            foreach (var slot in _selection.AllSlots)
            {
                WatchSessionState(slot);
                slot.RefreshSessionDetails();
            }
        }

        private void Grid_Unloaded(object sender, RoutedEventArgs e)
        {
            _watchSessionStates = false;
            foreach (var slot in _selection.AllSlots)
                UnwatchSessionState(slot);
        }

        private void WatchSessionState(MultiSlotVm slot)
        {
            if (slot.View is { } ssh)
                ssh.Session.StateChanged += Session_StateChanged;
            if (slot.LocalView is { } local)
                local.Terminal.TerminalStateChanged += Terminal_StateChanged;
        }

        private void UnwatchSessionState(MultiSlotVm slot)
        {
            if (slot.View is { } ssh)
                ssh.Session.StateChanged -= Session_StateChanged;
            if (slot.LocalView is { } local)
                local.Terminal.TerminalStateChanged -= Terminal_StateChanged;
        }

        private void Session_StateChanged(object? sender, SessionState state) => RefreshSessionStates();

        private void Terminal_StateChanged(object? sender, TerminalState state) => RefreshSessionStates();

        private void RefreshSessionStates() => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_watchSessionStates) return;
            foreach (var slot in _selection.AllSlots)
                slot.RefreshSessionDetails();
        });

        private void ShowPage()
        {
            Slots.Clear();
            foreach (var slot in _selection.GetPageSlots())
                Slots.Add(slot ?? new MultiSlotVm());
            CellsScrollViewer.ChangeView(0, 0, null, true);
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            var total = _selection.AllSlots.Count;
            var selected = _selection.GetSelectedSlots().Count;
            var available = _selection.EligibleCount;
            var offPage = _selection.GetSelectedSlots().Count(slot => !Slots.Contains(slot));
            CountText.Text = Helpers.Loc.T(
                $"{selected} / {available}개 선택 · 열린 탭 {total}개",
                $"{selected} / {available} selected · {total} open tabs");
            var localCount = Slots.Count(slot => slot.LocalView is not null && slot.CanBroadcast);
            var sshCount = Slots.Count(slot => slot.View is not null && slot.CanBroadcast);
            var disconnectedCount = Slots.Count(slot => slot.HasSession && !slot.CanBroadcast);
            SelectionNoticeText.Text = Helpers.Loc.T(
                $"선택 가능: Sutty SSH {sshCount}개 · 로컬/외부 터미널 {localCount}개. 터미널 입력은 실행 전 별도 승인이 필요합니다. 미연결 {disconnectedCount}개는 제외됩니다.",
                $"Available: {sshCount} Sutty SSH · {localCount} local/external terminals. Terminal input needs separate approval before execution. Excludes {disconnectedCount} disconnected tabs.");
            if (_selection.HiddenSessionCount > 0)
                SelectionNoticeText.Text += "\n" + Helpers.Loc.T(
                    $"처음 9개 탭만 표시합니다. 나머지 {_selection.HiddenSessionCount}개는 전체 선택과 실행 대상에서 제외됩니다.",
                    $"Only the first 9 tabs are shown. The remaining {_selection.HiddenSessionCount} are excluded from Select all and execution.");
            else if (IsPaginationEnabled && offPage > 0)
                SelectionNoticeText.Text += "\n" + Helpers.Loc.T($"다른 페이지에서 {offPage}개 선택됨.", $"{offPage} selected on other pages.");
            PageText.Text = Helpers.Loc.T(
                $"{_selection.PageIndex + 1} / {_selection.PageCount} 페이지",
                $"Page {_selection.PageIndex + 1} / {_selection.PageCount}");
            PreviousPageButton.IsEnabled = _selection.PageIndex > 0;
            NextPageButton.IsEnabled = _selection.PageIndex + 1 < _selection.PageCount;
            PaginationControls.Visibility = IsPaginationEnabled ? Visibility.Visible : Visibility.Collapsed;
            SelectAllButton.IsEnabled = selected < available;
            ClearSelectionButton.IsEnabled = _selection.AllSlots.Any(slot => slot.IsSelected);
        }

        private void Slot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MultiSlotVm.IsSelected) or nameof(MultiSlotVm.CanBroadcast))
                UpdateSummary();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var slot in _selection.AllSlots) slot.RefreshSessionDetails();
            _selection.SetAllSelected(true);
            UpdateSummary();
        }

        private void SessionSelection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { Tag: MultiSlotVm slot } checkBox) return;
            _selection.SetSelected(slot, checkBox.IsChecked == true);
            // A disconnect can occur between pointer input and the setter. Keep the
            // checkbox consistent with the accepted model state even when rejected.
            checkBox.IsChecked = slot.IsSelected;
            UpdateSummary();
        }

        private void ClearSelection_Click(object sender, RoutedEventArgs e)
        {
            _selection.SetAllSelected(false);
            UpdateSummary();
        }

        private void PreviousPage_Click(object sender, RoutedEventArgs e)
        {
            _selection.MovePage(-1);
            ShowPage();
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            _selection.MovePage(1);
            ShowPage();
        }

        /// <summary>Checked eligible targets in the active display scope, never placeholders or hidden cards.</summary>
        public List<MultiSlotVm> GetTargetSlots() => _selection.GetSelectedSlots();

        public int GetOffPageTargetCount(IReadOnlyCollection<MultiSlotVm> targets) =>
            targets.Count(slot => !Slots.Contains(slot));
    }
}

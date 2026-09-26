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
    /// <summary>Nine session cards per page; checked targets can span every page.</summary>
    public sealed partial class MultiSessionGrid : UserControl
    {
        public const int SlotCount = 9;
        private const int ColumnCount = 3;
        private const double CellSpacing = 8;
        private const double MinimumCellWidth = 190;
        private const double MinimumCellHeight = 210;
        private double _cellHeight = MinimumCellHeight;
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
            (slot, selected) => slot.IsSelected = selected);

        /// <summary>The current nine cards, including non-selectable placeholders.</summary>
        public ObservableCollection<MultiSlotVm> Slots { get; } = [];

        public MultiSessionGrid()
        {
            InitializeComponent();
            Loaded += Grid_Loaded;
            Unloaded += Grid_Unloaded;
            ShowPage();
        }

        public void RefreshLanguage()
        {
            Bindings.Update();
            SetSessions(_views);
        }

        private void Cells_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Keep three columns in a narrow window. Scrolling provides access
            // instead of squeezing text or silently dropping session cards.
            var width = Math.Max(ColumnCount * MinimumCellWidth + 2 * CellSpacing,
                e.NewSize.Width - 16);
            var height = Math.Max(ColumnCount * MinimumCellHeight + 2 * CellSpacing,
                e.NewSize.Height - 16);
            CellsRepeater.Width = width;
            GridLayout.MinItemWidth = Math.Floor((width - 2 * CellSpacing) / ColumnCount);
            _cellHeight = (height - 2 * CellSpacing) / ColumnCount;
            GridLayout.MinItemHeight = _cellHeight;
            for (var index = 0; index < Slots.Count; index++)
            {
                if (CellsRepeater.TryGetElement(index) is FrameworkElement card)
                    card.Height = _cellHeight;
            }
        }

        private void Cells_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        {
            // A finite height also constrains the output ScrollViewer inside each card.
            if (args.Element is FrameworkElement card)
                card.Height = _cellHeight;
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
            var available = _selection.AllSlots.Count(slot => slot.CanBroadcast);
            var offPage = _selection.GetSelectedSlots().Count(slot => !Slots.Contains(slot));
            CountText.Text = Helpers.Loc.T(
                $"전체 {total}개 · SSH {selected}개 선택 · 다른 페이지 {offPage}개",
                $"{total} sessions · {selected} SSH selected · {offPage} on other pages");
            PageText.Text = Helpers.Loc.T(
                $"{_selection.PageIndex + 1} / {_selection.PageCount} 페이지",
                $"Page {_selection.PageIndex + 1} / {_selection.PageCount}");
            PreviousPageButton.IsEnabled = _selection.PageIndex > 0;
            NextPageButton.IsEnabled = _selection.PageIndex + 1 < _selection.PageCount;
            SelectAllButton.IsEnabled = selected < available;
            ClearSelectionButton.IsEnabled = selected > 0;
        }

        private void Slot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MultiSlotVm.IsSelected) or nameof(MultiSlotVm.CanBroadcast))
                UpdateSummary();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            _selection.SetAllSelected(true);
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

        /// <summary>Checked broadcast targets from every page, never placeholder cards.</summary>
        public List<MultiSlotVm> GetTargetSlots() => _selection.GetSelectedSlots();

        public int GetOffPageTargetCount(IReadOnlyCollection<MultiSlotVm> targets) =>
            targets.Count(slot => !Slots.Contains(slot));
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace sutty.UI.Views
{
    /// <summary>Sixteen selectable session cards, flowing into one to four columns.</summary>
    public sealed partial class MultiSessionGrid : UserControl
    {
        public const int SlotCount = 16;
        private const double CellSpacing = 8;
        private IReadOnlyList<FrameworkElement> _views = [];

        public ObservableCollection<MultiSlotVm> Slots { get; } = [];

        public MultiSessionGrid()
        {
            InitializeComponent();
        }

        public void RefreshLanguage()
        {
            Bindings.Update();
            SetSessions(_views);
        }

        private void Cells_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var width = Math.Max(1, e.NewSize.Width - 16);
            var columns = Math.Clamp((int)((width + CellSpacing) / (220 + CellSpacing)), 1, 4);
            GridLayout.MaximumRowsOrColumns = columns;
            GridLayout.MinItemWidth = Math.Max(1, (width - (columns - 1) * CellSpacing) / columns);
            GridLayout.MinItemHeight = 180;
        }

        /// <summary>Refresh cards while retaining the slot objects held by running broadcasts.</summary>
        public void SetSessions(IReadOnlyList<FrameworkElement> views)
        {
            _views = views.ToArray();
            // Keep the object, not only its current values: an in-flight command
            // still writes its eventual result to this same slot instance.
            var previous = Slots
                .Where(s => s.SessionKey is not null)
                .ToDictionary(s => s.SessionKey!);

            Slots.Clear();
            for (var i = 0; i < SlotCount; i++)
            {
                var tabContent = i < views.Count ? views[i] : null;
                var sessionView = tabContent as SessionView;
                var localView = tabContent as LocalTerminalView;
                var key = (object?)sessionView ?? localView;
                var slot = key is not null && previous.TryGetValue(key, out var existing)
                    ? existing
                    : new MultiSlotVm
                    {
                        View = sessionView,
                        LocalView = localView,
                        // New/replacement sessions require an explicit target selection.
                        IsSelected = false,
                    };
                // Closed sessions are simply absent from the new collection. Never
                // retarget their slot: an old command may still be finishing on it.
                slot.RefreshSessionDetails();
                Slots.Add(slot);
            }

            CountText.Text = Helpers.Loc.T(
                $"{views.Count} / {SlotCount}개 세션",
                $"{views.Count} / {SlotCount} sessions");
        }

        /// <summary>체크된(브로드캐스트 대상) 슬롯들.</summary>
        public List<MultiSlotVm> GetTargetSlots() =>
            Slots.Where(s => s.HasSession && s.IsSelected).ToList();
    }
}

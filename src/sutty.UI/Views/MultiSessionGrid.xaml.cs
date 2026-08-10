using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using sutty.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace sutty.UI.Views
{
    /// <summary>Multi command의 4×4 세션 그리드 (16칸 고정, 화면을 꽉 채우는 반응형).</summary>
    public sealed partial class MultiSessionGrid : UserControl
    {
        public const int SlotCount = 16;
        private const double CellSpacing = 8;

        public ObservableCollection<MultiSlotVm> Slots { get; } = [];

        public MultiSessionGrid()
        {
            InitializeComponent();
        }

        public void RefreshLanguage() => Bindings.Update();

        // 셀 크기를 가용 영역의 정확히 1/4로 → 4×4가 가로·세로 꽉 찬다
        private void Cells_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var cellWidth = (e.NewSize.Width - 3 * CellSpacing) / 4.0 - 1;
            var cellHeight = (e.NewSize.Height - 3 * CellSpacing) / 4.0 - 1;

            if (cellWidth > 60) GridLayout.MinItemWidth = cellWidth;
            if (cellHeight > 60) GridLayout.MinItemHeight = cellHeight;
        }

        /// <summary>열린 세션들로 슬롯을 다시 채운다. 세션별 체크 상태와 결과 미리보기는 유지.</summary>
        public void SetSessions(IReadOnlyList<SessionView> views)
        {
            // 이전 상태 기억 (세션 기준)
            var previous = Slots
                .Where(s => s.View is not null)
                .ToDictionary(s => s.View!, s => (s.IsSelected, s.LastOutput));

            Slots.Clear();
            for (var i = 0; i < SlotCount; i++)
            {
                var view = i < views.Count ? views[i] : null;
                var known = view is not null && previous.TryGetValue(view, out var prev);
                Slots.Add(new MultiSlotVm
                {
                    View = view,
                    IsSelected = !known || previous[view!].IsSelected,
                    LastOutput = known ? previous[view!].LastOutput : "",
                });
            }

            CountText.Text = $"{views.Count} / {SlotCount} sessions";
        }

        /// <summary>체크된(브로드캐스트 대상) 슬롯들.</summary>
        public List<MultiSlotVm> GetTargetSlots() =>
            Slots.Where(s => s.View is not null && s.IsSelected).ToList();
    }
}

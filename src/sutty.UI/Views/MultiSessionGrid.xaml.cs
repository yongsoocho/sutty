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

        // 셀 크기를 가용 영역의 정확히 1/4로 → 4×4가 가로·세로 꽉 찬다
        private void Cells_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var cellWidth = (e.NewSize.Width - 3 * CellSpacing) / 4.0 - 1;
            var cellHeight = (e.NewSize.Height - 3 * CellSpacing) / 4.0 - 1;

            if (cellWidth > 60) GridLayout.MinItemWidth = cellWidth;
            if (cellHeight > 60) GridLayout.MinItemHeight = cellHeight;
        }

        /// <summary>열린 세션들로 슬롯을 다시 채운다. 세션별 체크 상태와 결과 미리보기는 유지.</summary>
        public void SetSessions(IReadOnlyList<FrameworkElement> views)
        {
            _views = views.ToArray();
            // 이전 상태 기억 (세션 기준)
            var previous = Slots
                .Where(s => s.SessionKey is not null)
                .ToDictionary(s => s.SessionKey!, s => (s.IsSelected, s.LastOutput, s.ResultText));

            Slots.Clear();
            for (var i = 0; i < SlotCount; i++)
            {
                var tabContent = i < views.Count ? views[i] : null;
                var sessionView = tabContent as SessionView;
                var localView = tabContent as LocalTerminalView;
                var key = (object?)sessionView ?? localView;
                var known = key is not null && previous.TryGetValue(key, out var prev);
                Slots.Add(new MultiSlotVm
                {
                    View = sessionView,
                    LocalView = localView,
                    // 새 로컬/SSH 탭은 기본 선택하고, 기존 탭은 사용자의 체크를 유지한다.
                    IsSelected = known ? previous[key!].IsSelected : key is not null,
                    LastOutput = known ? previous[key!].LastOutput : "",
                    ResultText = known ? previous[key!].ResultText : "",
                });
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

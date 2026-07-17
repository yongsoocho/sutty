using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using sutty.UI.ViewModels;
using System;

namespace sutty.UI.Controls;


public sealed partial class HostCard : UserControl
{
    // ═══════════════════════════════════════════
    //  React 비유: props.host
    //  부모가 <HostCard Host="{x:Bind item}" /> 로 넘긴다.
    // ═══════════════════════════════════════════

    public static readonly DependencyProperty HostProperty =
        DependencyProperty.Register(
            nameof(Host),
            typeof(HostInfoModel),
            typeof(HostCard),
            new PropertyMetadata(null));

    public HostInfoModel Host
    {
        get => (HostInfoModel)GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    public HostCard()
    {
        this.InitializeComponent();
    }

    /// <summary>카드 클릭 → 이 호스트로 바로 연결하고 싶다는 신호.</summary>
    public event EventHandler<HostInfoModel>? Clicked;

    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Host is not null)
            Clicked?.Invoke(this, Host);
    }

    // ── 호버 효과 (배경색 살짝 밝게) — 현재 테마의 팔레트 브러시 사용 ──

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
        => CardRoot.Background = Helpers.ThemeResources.Brush(this, "CardBgHover");

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
        => CardRoot.Background = Helpers.ThemeResources.Brush(this, "CardBg");
}
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using sutty.UI.ViewModels;

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

    // 태그 유무 → Visibility 바인딩용
    public Visibility HasTags =>
        Host?.Tags.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public HostCard()
    {
        this.InitializeComponent();
    }

    // ── 호버 효과 (배경색 살짝 밝게) ──

    private static readonly SolidColorBrush _normalBg = new(Windows.UI.Color.FromArgb(255, 0x12, 0x1E, 0x31));
    private static readonly SolidColorBrush _hoverBg = new(Windows.UI.Color.FromArgb(255, 0x1A, 0x2B, 0x42));

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
        => CardRoot.Background = _hoverBg;

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
        => CardRoot.Background = _normalBg;
}
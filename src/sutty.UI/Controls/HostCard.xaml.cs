using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using sutty.UI.ViewModels;
using System;

namespace sutty.UI.Controls;

public sealed partial class HostCard : UserControl
{
    public static readonly DependencyProperty HostProperty =
        DependencyProperty.Register(
            nameof(Host),
            typeof(HostInfoModel),
            typeof(HostCard),
            new PropertyMetadata(null, OnHostChanged));

    public HostInfoModel Host
    {
        get => (HostInfoModel)GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    public HostCard()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => UpdateVisuals();
    }

    public event EventHandler<HostInfoModel>? Clicked;
    public event EventHandler<HostInfoModel>? PrimaryActionRequested;
    public event EventHandler<HostInfoModel>? DeleteRequested;

    public void RefreshLanguage() => UpdateVisuals();

    private static void OnHostChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is HostCard card)
            card.UpdateVisuals();
    }

    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Host is not null)
            Clicked?.Invoke(this, Host);
    }

    private void PrimaryActionButton_Tapped(object sender, TappedRoutedEventArgs e)
        => e.Handled = true;

    private void PrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (Host is not null)
            PrimaryActionRequested?.Invoke(this, Host);
    }

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (Host is not null)
            DeleteRequested?.Invoke(this, Host);
    }

    private void UpdateVisuals()
    {
        if (PrimaryActionIcon is null || PrimaryActionButton is null || Host is null) return;

        var hasSavedProfile = !string.IsNullOrWhiteSpace(Host.ProfileId);
        PrimaryActionIcon.Glyph = hasSavedProfile
            ? Host.IsPinned ? "\uE735" : "\uE734"
            : "\uE74E";
        PrimaryActionIcon.Foreground = Helpers.ThemeResources.Brush(
            this,
            Host.IsPinned ? "AccentTeal" : "TextFaint");

        var actionLabel = hasSavedProfile
            ? Host.IsPinned
                ? Helpers.Loc.T("즐겨찾기에서 제거", "Remove from favorites")
                : Helpers.Loc.T("즐겨찾기에 추가", "Add to favorites")
            : Helpers.Loc.T("호스트 저장", "Save host");
        var targetLabel = $"{actionLabel}: {Host.Alias} ({Host.Hostname})";
        ToolTipService.SetToolTip(PrimaryActionButton, targetLabel);
        AutomationProperties.SetName(PrimaryActionButton, targetLabel);

        DeleteMenuItem.Visibility = Host.IsSavedProfile ? Visibility.Visible : Visibility.Collapsed;
        DeleteMenuItem.Text = Helpers.Loc.T("저장 호스트 삭제", "Delete saved host");

        OutcomeDot.Visibility = Host.HasOutcome ? Visibility.Visible : Visibility.Collapsed;
        OutcomeDot.Fill = Helpers.ThemeResources.Brush(this, Host.Outcome switch
        {
            "Success" => "StatusGreen",
            "Failed" => "StatusRed",
            "Cancelled" => "StatusAmber",
            _ => "StatusIdle",
        });
        var outcomeLabel = Host.Outcome switch
        {
            "Success" => Helpers.Loc.T("연결 성공", "Connection succeeded"),
            "Failed" => Helpers.Loc.T("연결 실패", "Connection failed"),
            "Cancelled" => Helpers.Loc.T("연결 취소", "Connection cancelled"),
            _ => "",
        };
        ToolTipService.SetToolTip(OutcomeDot, outcomeLabel);
        AutomationProperties.SetName(OutcomeDot, outcomeLabel);
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
        => CardRoot.Background = Helpers.ThemeResources.Brush(this, "CardBgHover");

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
        => CardRoot.Background = Helpers.ThemeResources.Brush(this, "CardBg");
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Foundation;

namespace sutty.UI.Controls;

/// <summary>Wraps toolbars and actions within the width offered by their parent.</summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(WrapPanel),
        new PropertyMetadata(6d, OnSpacingChanged));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private static void OnSpacingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((WrapPanel)sender).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = double.IsInfinity(availableSize.Width) ? double.MaxValue : Math.Max(0, availableSize.Width);
        double x = 0, y = 0, lineHeight = 0, width = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(limit, double.PositiveInfinity));
            var childWidth = Math.Min(limit, child.DesiredSize.Width);
            if (x > 0 && x + childWidth > limit)
            {
                y += lineHeight + Spacing;
                x = 0;
                lineHeight = 0;
            }
            width = Math.Max(width, x + childWidth);
            x += childWidth + Spacing;
            lineHeight = Math.Max(lineHeight, child.DesiredSize.Height);
        }
        return new Size(width, y + lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var width = Math.Min(finalSize.Width, child.DesiredSize.Width);
            if (x > 0 && x + width > finalSize.Width)
            {
                y += lineHeight + Spacing;
                x = 0;
                lineHeight = 0;
            }
            child.Arrange(new Rect(x, y, width, child.DesiredSize.Height));
            x += width + Spacing;
            lineHeight = Math.Max(lineHeight, child.DesiredSize.Height);
        }
        return finalSize;
    }
}

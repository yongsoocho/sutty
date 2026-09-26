using Microsoft.UI.Xaml.Controls;
using sutty.UI.Services;
using Windows.Foundation;

namespace sutty.UI.Controls;

/// <summary>The nine Multi cards always occupy three explicit rows and columns.</summary>
public sealed class MultiSessionFixedGridLayout : NonVirtualizingLayout
{
    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var geometry = MultiSessionGridGeometry.FromViewport(availableSize.Width, availableSize.Height);
        var cell = new Size(geometry.CellWidth, geometry.CellHeight);
        foreach (var child in context.Children) child.Measure(cell);
        return new Size(geometry.Width, geometry.Height);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        var geometry = MultiSessionGridGeometry.FromViewport(finalSize.Width, finalSize.Height);
        for (var index = 0; index < context.Children.Count; index++)
        {
            var (x, y) = geometry.Position(index);
            context.Children[index].Arrange(new Rect(x, y, geometry.CellWidth, geometry.CellHeight));
        }
        return new Size(geometry.Width, geometry.Height);
    }
}

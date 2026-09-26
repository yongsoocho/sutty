using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.Graphics;

namespace sutty.UI.Views;

public sealed partial class MainWindow
{
    private InputNonClientPointerSource? _titleBarInputSource;
    private XamlRoot? _titleBarXamlRoot;
    private RectInt32[] _titleBarPassthroughRects = [];
    private bool _titleBarInteractionStopped;

    private void InitializeTitleBarInteraction()
    {
        _titleBarInputSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        Root.Loaded += TitleBar_RootLoaded;
        TitleTabs.LayoutUpdated += TitleBar_LayoutUpdated;
        TitleBarDragRegion.SizeChanged += TitleBar_SizeChanged;
        Closed += TitleBar_WindowClosed;
    }

    private void TitleBar_RootLoaded(object sender, RoutedEventArgs e)
    {
        if (_titleBarXamlRoot is not null)
            _titleBarXamlRoot.Changed -= TitleBar_XamlRootChanged;
        _titleBarXamlRoot = Root.XamlRoot;
        if (_titleBarXamlRoot is not null)
            _titleBarXamlRoot.Changed += TitleBar_XamlRootChanged;
        UpdateTitleBarInteractiveRegions();
    }

    private void TitleBar_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateTitleBarInteractiveRegions();

    private void TitleBar_LayoutUpdated(object? sender, object e) =>
        UpdateTitleBarInteractiveRegions();

    private void TitleBar_XamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
        UpdateTitleBarInteractiveRegions();

    private void UpdateTitleBarInteractiveRegions()
    {
        if (_titleBarInteractionStopped || _windowClosing || !ExtendsContentIntoTitleBar ||
            _titleBarInputSource is null || Root.XamlRoot is not { } xamlRoot ||
            !TitleTabs.IsLoaded || TitleTabs.ActualWidth <= 0 || TitleTabs.ActualHeight <= 0)
            return;

        var scale = xamlRoot.RasterizationScale;
        if (!double.IsFinite(scale) || scale <= 0) return;

        // The caption insets use physical pixels. Keep a small empty drag area
        // beside the tabs, including after DPI changes and maximize/restore.
        var rightMargin = Math.Max(132, AppWindow.TitleBar.RightInset / scale + 32);
        if (Math.Abs(TitleTabs.Margin.Right - rightMargin) > 0.01)
        {
            var margin = TitleTabs.Margin;
            TitleTabs.Margin = new Thickness(margin.Left, margin.Top, rightMargin, margin.Bottom);
            return; // LayoutUpdated will use the newly arranged tab bounds.
        }

        var tabBounds = TitleTabs.TransformToVisual(Root).TransformBounds(
            new Rect(0, 0, TitleTabs.ActualWidth, TitleTabs.ActualHeight));
        var rects = new List<RectInt32>();
        var foundParts = CollectTitleBarInteractiveParts(TitleTabs, tabBounds, scale, rects);
        var requiredParts = TitleTabs.IsAddTabButtonVisible ? 3 : 1;
        if ((foundParts & requiredParts) != requiredParts)
        {
            // Fail safely if a future WinUI template renames either part: the
            // entire tab strip remains clickable instead of becoming a caption.
            rects.Clear();
            AddTitleBarPassthroughRect(TitleTabs, tabBounds, scale, rects);
        }

        var next = rects.ToArray();
        if (_titleBarPassthroughRects.SequenceEqual(next)) return;
        // A XAML control drawn over SetTitleBar's rectangle is not automatically
        // a client input region. Register it at the native hit-test layer so an
        // overflow button's second click cannot become a caption double-click.
        _titleBarInputSource.SetRegionRects(NonClientRegionKind.Passthrough, next);
        _titleBarPassthroughRects = next;
    }

    private int CollectTitleBarInteractiveParts(
        DependencyObject parent, Rect tabBounds, double scale, List<RectInt32> rects)
    {
        var foundParts = 0;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement element)
            {
                if (element.Visibility != Visibility.Visible) continue;
                // In the pinned WinUI TabView template, TabListView contains the
                // tab viewport AND both overflow RepeatButtons. Include disabled
                // arrows too; reaching an edge must not turn them into a caption.
                if (element.Name is "TabListView" or "AddButton")
                {
                    var previousCount = rects.Count;
                    AddTitleBarPassthroughRect(element, tabBounds, scale, rects);
                    if (rects.Count > previousCount)
                        foundParts |= element.Name == "TabListView" ? 1 : 2;
                    continue;
                }
            }
            foundParts |= CollectTitleBarInteractiveParts(child, tabBounds, scale, rects);
        }
        return foundParts;
    }

    private void AddTitleBarPassthroughRect(
        FrameworkElement element, Rect tabBounds, double scale, List<RectInt32> rects)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
        var bounds = element.TransformToVisual(Root).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        // Clamp to the visible strip, then round outward so fractional DPI never
        // leaves a one-pixel caption seam on an interactive control's edge.
        var left = (int)Math.Floor(Math.Max(bounds.Left, tabBounds.Left) * scale);
        var top = (int)Math.Floor(Math.Max(bounds.Top, tabBounds.Top) * scale);
        var right = (int)Math.Ceiling(Math.Min(bounds.Right, tabBounds.Right) * scale);
        var bottom = (int)Math.Ceiling(Math.Min(bounds.Bottom, tabBounds.Bottom) * scale);
        if (right > left && bottom > top)
            rects.Add(new RectInt32(left, top, right - left, bottom - top));
    }

    private void TitleBar_WindowClosed(object sender, WindowEventArgs args)
    {
        _titleBarInteractionStopped = true;
        Root.Loaded -= TitleBar_RootLoaded;
        TitleTabs.LayoutUpdated -= TitleBar_LayoutUpdated;
        TitleBarDragRegion.SizeChanged -= TitleBar_SizeChanged;
        Closed -= TitleBar_WindowClosed;
        if (_titleBarXamlRoot is not null)
            _titleBarXamlRoot.Changed -= TitleBar_XamlRootChanged;
        _titleBarXamlRoot = null;
        _titleBarInputSource = null;
        _titleBarPassthroughRects = [];
    }
}

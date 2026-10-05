using System;

namespace sutty.UI.Services;

/// <summary>Keeps the user's preferred width independent of temporary layout constraints.</summary>
public sealed class DetailsPaneWidthState
{
    public double PreferredWidth { get; private set; } = 460;
    public double AppliedWidth { get; private set; }

    public void Restore(double width)
    {
        if (double.IsFinite(width) && width > 0)
            PreferredWidth = Math.Clamp(width, 300, 800);
    }

    public double ApplyLayout(double minimum, double maximum)
    {
        AppliedWidth = Math.Clamp(PreferredWidth, minimum, maximum);
        return AppliedWidth;
    }

    public bool CaptureResize(double columnWidth)
    {
        if (!double.IsFinite(columnWidth) || columnWidth <= 0 ||
            Math.Abs(columnWidth - AppliedWidth) < 0.5)
            return false;

        AppliedWidth = columnWidth;
        PreferredWidth = Math.Clamp(columnWidth, 300, 800);
        return true;
    }
}

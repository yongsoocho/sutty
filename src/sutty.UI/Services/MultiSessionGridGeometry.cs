using System;

namespace sutty.UI.Services;

/// <summary>Explicit 3×3 cell coordinates, independent of adaptive wrap thresholds.</summary>
internal readonly record struct MultiSessionGridGeometry(double CellWidth, double CellHeight)
{
    public const int Columns = 3;
    public const int Slots = 9;
    public const double Spacing = 8;
    public const double MinimumCellWidth = 190;
    public const double MinimumCellHeight = 210;
    public double Width => Columns * CellWidth + (Columns - 1) * Spacing;
    public double Height => Columns * CellHeight + (Columns - 1) * Spacing;

    public static MultiSessionGridGeometry FromViewport(double width, double height) => new(
        CellSize(width, MinimumCellWidth), CellSize(height, MinimumCellHeight));

    private static double CellSize(double viewport, double minimum) =>
        double.IsFinite(viewport) && viewport > 0
            ? Math.Max(minimum, Math.Floor((viewport - (Columns - 1) * Spacing) / Columns))
            : minimum;

    public (double X, double Y) Position(int index)
    {
        if (index is < 0 or >= Slots) throw new ArgumentOutOfRangeException(nameof(index));
        return ((index % Columns) * (CellWidth + Spacing), (index / Columns) * (CellHeight + Spacing));
    }
}

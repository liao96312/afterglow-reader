namespace AfterglowReader.SystemIntegration;

internal static class WindowHitTest
{
    internal const int Client = 1;
    internal const int Caption = 2;
    internal const int Left = 10;
    internal const int Right = 11;
    internal const int Top = 12;
    internal const int TopLeft = 13;
    internal const int TopRight = 14;
    internal const int Bottom = 15;
    internal const int BottomLeft = 16;
    internal const int BottomRight = 17;

    internal static int ScaleDip(int value, uint dpi)
        => Math.Max(1, (int)Math.Round(value * Math.Max(96u, dpi) / 96d));

    internal static bool TryGetResizeRegion(string? edge, out int region)
    {
        region = edge switch
        {
            "left" => Left,
            "right" => Right,
            "top" => Top,
            "topLeft" => TopLeft,
            "topRight" => TopRight,
            "bottom" => Bottom,
            "bottomLeft" => BottomLeft,
            "bottomRight" => BottomRight,
            _ => Client
        };

        return region != Client;
    }

    internal static int Resolve(
        double x,
        double y,
        int windowLeft,
        int windowTop,
        int windowRight,
        int windowBottom,
        int resizeBorder,
        int dragWidth,
        int dragHeight)
    {
        var onLeft = x < windowLeft + resizeBorder;
        var onRight = x >= windowRight - resizeBorder;
        var onTop = y < windowTop + resizeBorder;
        var onBottom = y >= windowBottom - resizeBorder;

        if (onTop && onLeft) return TopLeft;
        if (onTop && onRight) return TopRight;
        if (onBottom && onLeft) return BottomLeft;
        if (onBottom && onRight) return BottomRight;
        if (onLeft) return Left;
        if (onRight) return Right;
        if (onTop) return Top;
        if (onBottom) return Bottom;

        var usableDragWidth = Math.Min(dragWidth, Math.Max(0, windowRight - windowLeft - (resizeBorder * 2)));
        if (x < windowLeft + resizeBorder + usableDragWidth
            && y < windowTop + resizeBorder + dragHeight)
        {
            return Caption;
        }

        return Client;
    }
}

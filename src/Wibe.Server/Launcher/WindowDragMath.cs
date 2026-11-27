using System.Drawing;

namespace Wibe.Server.Launcher;

/// <summary>
/// Перемещение безрамочного окна: разница курсора за кадр прибавляется к стартовой
/// позиции окна. WebView2 перехватывает мышь, поэтому тащит окно таймер, а не Windows.
/// </summary>
public static class WindowDragMath
{
    public static Point Next(Point formStart, Point cursorStart, Point cursorNow) =>
        new(formStart.X + (cursorNow.X - cursorStart.X), formStart.Y + (cursorNow.Y - cursorStart.Y));

    /// <summary>Какую часть окна держит курсор (0..1) — чтобы разворот из maximized не прыгал.</summary>
    public static double GripRatio(Rectangle form, Point cursor) =>
        form.Width <= 0 ? 0.5 : Math.Clamp((double)(cursor.X - form.Left) / form.Width, 0.05, 0.95);
}

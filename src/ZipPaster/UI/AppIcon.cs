using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ZipPaster.UI;

/// <summary>
/// The application icon, drawn at runtime rather than shipped as a .ico.
/// </summary>
/// <remarks>
/// Keeps the repository free of binary assets and guarantees a crisp icon at any
/// DPI. Cached because the tray, every form, and the taskbar all ask for it.
/// </remarks>
internal static class AppIcon
{
    private static Icon? _cached;

    public static Icon Get() => _cached ??= Create();

    private static Icon Create()
    {
        const int Size = 64;

        using var bitmap = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var bounds = new Rectangle(2, 2, Size - 5, Size - 5);

            using (var path = RoundedRect(bounds, 12))
            using (var fill = new LinearGradientBrush(bounds, Color.FromArgb(37, 99, 235), Color.FromArgb(29, 78, 216), 60f))
            {
                g.FillPath(fill, path);
            }

            // A bold "Z" reads clearly even at 16x16 in the notification area.
            using var font = new Font("Segoe UI", 34f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            g.DrawString("Z", font, text, new RectangleF(0, -1, Size, Size), format);
        }

        nint handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon survives DestroyIcon on the temporary handle.
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethodsGdi.DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }
}

internal static partial class NativeMethodsGdi
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);
}

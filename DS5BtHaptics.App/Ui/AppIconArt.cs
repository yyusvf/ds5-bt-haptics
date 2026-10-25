using System.Drawing;
using System.Drawing.Drawing2D;

namespace DS5BtHaptics.App.Ui;

/// <summary>What the small dot on the tray icon says.</summary>
internal enum TrayBadge
{
    None,
    /// <summary>Bridge running, virtual controller attached.</summary>
    Ready,
    /// <summary>Waiting for the controller or for Windows to attach the virtual one.</summary>
    Waiting,
    /// <summary>Setup incomplete or the bridge failed.</summary>
    Problem,
}

/// <summary>
/// The app icon in one place: a controller silhouette with two stick holes, and three haptic
/// waves above it. Tray, taskbar and flyout all use this; DS5BtHaptics.ico is its image (see
/// <see cref="AppIconFile"/>).
/// </summary>
internal static class AppIconArt
{
    public static readonly Color Colour = Color.FromArgb(0x63, 0x5D, 0xF1);

    private const float Reference = 32f;

    /// <summary>Drawn at 4x and scaled down - at 16 px the details would otherwise blur.</summary>
    public static Bitmap Render(int size, TrayBadge badge = TrayBadge.None)
    {
        const int oversample = 4;
        using Bitmap large = Draw(size * oversample, badge);

        var result = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(result);
        g.Clear(Color.Transparent);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(large, new Rectangle(0, 0, size, size));
        return result;
    }

    private static Bitmap Draw(int size, TrayBadge badge)
    {
        var bitmap = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / Reference, size / Reference);

        using var brush = new SolidBrush(Colour);

        // Body: a wide rounded bar with two grips hanging off its ends.
        using (var body = new GraphicsPath())
        {
            body.AddPath(Rounded(2.5f, 13f, 27f, 10f, 5f), false);
            body.AddEllipse(1.5f, 15f, 10.5f, 13.5f);
            body.AddEllipse(20f, 15f, 10.5f, 13.5f);
            using var region = new Region(body);
            g.FillRegion(brush, region);
        }

        // Stick holes, cut out so the taskbar shows through.
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.Transparent))
        {
            g.FillEllipse(clear, 8.2f, 16.2f, 4.6f, 4.6f);
            g.FillEllipse(clear, 19.2f, 16.2f, 4.6f, 4.6f);
        }
        g.CompositingMode = CompositingMode.SourceOver;

        // Haptic waves above the controller.
        using (var pen = new Pen(Colour, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(pen, 11f, 6.5f, 10f, 10f, 225, 90);
            g.DrawArc(pen, 7f, 2.5f, 18f, 18f, 232, 76);
        }

        if (badge != TrayBadge.None)
        {
            Color dot = badge switch
            {
                TrayBadge.Ready => Color.FromArgb(0x3F, 0xB9, 0x50),
                TrayBadge.Waiting => Color.FromArgb(0xD2, 0x99, 0x22),
                _ => Color.FromArgb(0xE5, 0x48, 0x4D),
            };
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent))
            {
                g.FillEllipse(clear, 18f, 18f, 15f, 15f);
            }
            g.CompositingMode = CompositingMode.SourceOver;
            using var dotBrush = new SolidBrush(dot);
            g.FillEllipse(dotBrush, 20f, 20f, 11f, 11f);
        }

        return bitmap;
    }

    private static GraphicsPath Rounded(float x, float y, float width, float height, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

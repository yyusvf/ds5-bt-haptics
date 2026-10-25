#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DS5BtHaptics.App.Ui.Shell;

/// <summary>
/// Renders a window to a PNG without showing it - for UI work. The window is created far off
/// screen, measured, captured and closed. Anything the window manager adds (shadow, rounded
/// corners) is not in the capture; layout, colours, type and spacing are.
/// </summary>
internal static class DesignRender
{
    public static void Capture(Window window, string path, int width, int height, double scale = 1.5)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.Width = width;
        window.ShowInTaskbar = false;

        // Height 0: the window sizes itself (SizeToContent) and that is what gets captured.
        if (height > 0)
        {
            window.Height = height;
        }

        // The window background isn't part of the captured content, so put it on the root.
        if (window.Content is System.Windows.Controls.Panel root
            && window.TryFindResource("WindowBrush") is Brush fallback)
        {
            root.Background = fallback;
        }

        window.Show();
        window.UpdateLayout();
        Dispatch();

        var visual = (Visual)window.Content;
        if (height <= 0 && visual is FrameworkElement measured)
        {
            height = (int)Math.Ceiling(measured.ActualHeight);
        }

        var target = new RenderTargetBitmap(
            (int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (FileStream file = File.Create(path))
        {
            encoder.Save(file);
        }

        window.Close();
    }

    /// <summary>Lets the dispatcher run idle once so layout has settled.</summary>
    private static void Dispatch()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
#endif

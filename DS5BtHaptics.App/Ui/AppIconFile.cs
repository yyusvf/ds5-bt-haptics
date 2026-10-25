#if DEBUG
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace DS5BtHaptics.App.Ui;

/// <summary>
/// Writes DS5BtHaptics.ico from <see cref="AppIconArt"/>. Debug only, run by hand with
/// <c>--makeicon &lt;path&gt;</c>, so the file always mirrors the drawing.
/// </summary>
internal static class AppIconFile
{
    /// <summary>Each size is drawn separately - Windows picks a different one per place.</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    public static void Write(string path)
    {
        List<byte[]> frames = [];
        foreach (int size in Sizes)
        {
            using Bitmap bitmap = AppIconArt.Render(size);
            using var buffer = new MemoryStream();
            bitmap.Save(buffer, ImageFormat.Png);
            frames.Add(buffer.ToArray());
        }

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(file);

        // Header: reserved, type (1 = icon), image count.
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Count);

        // 16-byte directory entry per image; image data follows.
        int offset = 6 + frames.Count * 16;
        for (int i = 0; i < frames.Count; i++)
        {
            // 256 doesn't fit in a byte and is written as 0.
            writer.Write((byte)(Sizes[i] == 256 ? 0 : Sizes[i]));
            writer.Write((byte)(Sizes[i] == 256 ? 0 : Sizes[i]));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }

        foreach (byte[] frame in frames)
        {
            writer.Write(frame);
        }
    }
}
#endif

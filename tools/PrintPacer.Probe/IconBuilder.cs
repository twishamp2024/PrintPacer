using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PrintPacer.Probe;

/// <summary>
/// Draws the application icon and packs it into a multi-resolution .ico.
///
/// Generated rather than checked in as a binary so the artwork is reviewable, and so the
/// small sizes can be drawn with less detail instead of being blindly downscaled.
/// </summary>
internal static class IconBuilder
{
    private static readonly Color Background = Color.FromArgb(0x1E, 0x40, 0x6B);
    private static readonly Color Accent = Color.FromArgb(0xF5, 0x9E, 0x0B);
    private static readonly Color Paper = Color.FromArgb(0xFA, 0xFA, 0xFA);
    private static readonly Color Ink = Color.FromArgb(0x93, 0xA6, 0xBC);

    public static void Write(string path)
    {
        int[] sizes = { 256, 128, 64, 48, 32, 16 };
        var images = sizes.Select(Render).ToArray();

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(output);

        var payloads = images.Select(image =>
        {
            using var buffer = new MemoryStream();
            image.Save(buffer, ImageFormat.Png);
            return buffer.ToArray();
        }).ToArray();

        // ICONDIR
        writer.Write((ushort)0);          // reserved
        writer.Write((ushort)1);          // type: icon
        writer.Write((ushort)sizes.Length);

        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            // ICONDIRENTRY. 256 is encoded as 0.
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)0);        // palette colours
            writer.Write((byte)0);        // reserved
            writer.Write((ushort)1);      // colour planes
            writer.Write((ushort)32);     // bits per pixel
            writer.Write(payloads[i].Length);
            writer.Write(offset);
            offset += payloads[i].Length;
        }

        foreach (var payload in payloads) writer.Write(payload);
        foreach (var image in images) image.Dispose();
    }

    private static Bitmap Render(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float S(double fraction) => (float)(fraction * size);

        // Rounded square backdrop.
        using (var brush = new SolidBrush(Background))
        using (var shape = RoundedRect(S(0.02), S(0.02), S(0.96), S(0.96), S(0.22)))
        {
            g.FillPath(brush, shape);
        }

        // A stack of sheets, which is what the application actually manages: batches of
        // pages rather than one continuous document.
        var sheetWidth = S(0.46);
        var sheetHeight = S(0.56);
        var stackOffset = S(0.055);
        var baseX = S(0.19);
        var baseY = S(0.16);

        for (var layer = 2; layer >= 0; layer--)
        {
            var x = baseX + stackOffset * layer;
            var y = baseY + stackOffset * layer;
            var shade = layer == 0 ? Paper : Color.FromArgb(200 - layer * 45, Paper);

            using var brush = new SolidBrush(shade);
            using var path = RoundedRect(x, y, sheetWidth, sheetHeight, S(0.035));
            g.FillPath(brush, path);
        }

        // Text lines on the front sheet, omitted at the smallest sizes where they turn to mud.
        if (size >= 32)
        {
            using var pen = new Pen(Ink, Math.Max(1f, S(0.022)));
            var lineX = baseX + S(0.055);
            var lineWidth = sheetWidth - S(0.11);
            for (var i = 0; i < 4; i++)
            {
                var y = baseY + S(0.10) + i * S(0.075);
                var width = i == 3 ? lineWidth * 0.55f : lineWidth;
                g.DrawLine(pen, lineX, y, lineX + width, y);
            }
        }

        // Clock badge: the scheduling half of the idea.
        var badge = S(0.42);
        var badgeX = size - badge - S(0.06);
        var badgeY = size - badge - S(0.06);

        using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillEllipse(shadow, badgeX - S(0.012), badgeY - S(0.012), badge + S(0.024), badge + S(0.024));

        using (var brush = new SolidBrush(Accent))
            g.FillEllipse(brush, badgeX, badgeY, badge, badge);

        using (var pen = new Pen(Background, Math.Max(1.5f, S(0.035))) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            var cx = badgeX + badge / 2f;
            var cy = badgeY + badge / 2f;
            g.DrawLine(pen, cx, cy, cx, cy - badge * 0.27f);          // minute hand
            g.DrawLine(pen, cx, cy, cx + badge * 0.20f, cy + badge * 0.10f); // hour hand
        }

        return bitmap;
    }

    private static GraphicsPath RoundedRect(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

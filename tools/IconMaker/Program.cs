using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;

var output = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Output .ico path required.");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using var bitmap = new Bitmap(256, 256);
using (var graphics = Graphics.FromImage(bitmap))
{
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    graphics.Clear(Color.Transparent);
    using var background = new LinearGradientBrush(new Rectangle(0, 0, 256, 256), Color.FromArgb(16, 34, 48), Color.FromArgb(9, 20, 31), 45);
    graphics.FillRoundedRectangle(background, new Rectangle(10, 10, 236, 236), 58);
    using var cyan = new Pen(Color.FromArgb(73, 190, 255), 24) { StartCap = LineCap.Round, EndCap = LineCap.Round };
    using var amber = new Pen(Color.FromArgb(255, 184, 74), 24) { StartCap = LineCap.Round, EndCap = LineCap.Round };
    graphics.DrawArc(cyan, 52, 66, 112, 124, 95, 185);
    graphics.DrawArc(amber, 92, 66, 112, 124, 275, 185);
    using var white = new SolidBrush(Color.White);
    graphics.FillEllipse(white, 116, 116, 24, 24);
}

var handle = bitmap.GetHicon();
try
{
    using var icon = Icon.FromHandle(handle);
    using var stream = File.Create(output);
    icon.Save(stream);
}
finally { DestroyIcon(handle); }

[DllImport("user32.dll")]
static extern bool DestroyIcon(IntPtr handle);

static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle rectangle, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}

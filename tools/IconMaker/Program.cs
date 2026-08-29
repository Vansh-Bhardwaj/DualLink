using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

var output = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Output .ico path required.");
var preview = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
Directory.CreateDirectory(Path.GetDirectoryName(output)!);

var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
var frames = sizes.Select(size => (Size: size, Data: RenderPng(size))).ToArray();
WriteIco(output, frames);

if (preview is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
    File.WriteAllBytes(preview, RenderPng(512));
}

static byte[] RenderPng(int size)
{
    const int supersampling = 4;
    var canvasSize = size * supersampling;
    using var canvas = new Bitmap(canvasSize, canvasSize, PixelFormat.Format32bppArgb);
    canvas.SetResolution(96 * supersampling, 96 * supersampling);
    using (var graphics = Graphics.FromImage(canvas))
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);
        graphics.ScaleTransform(canvasSize / 48f, canvasSize / 48f);

        using var silhouette = RoundedRectangle(new RectangleF(2, 2, 44, 44), 10);
        using var background = new LinearGradientBrush(
            new PointF(7, 4), new PointF(41, 44),
            Color.FromArgb(255, 22, 29, 48), Color.FromArgb(255, 13, 17, 30));
        graphics.FillPath(background, silhouette);

        using var edge = new Pen(Color.FromArgb(88, 151, 177, 222), 0.75f);
        graphics.DrawPath(edge, silhouette);

        using var top = FlowPath(10, 14, 27, 24);
        using var bottom = FlowPath(10, 34, 27, 24);
        using var shadow = new Pen(Color.FromArgb(88, 0, 0, 0), 7.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawPath(shadow, top);
        graphics.DrawPath(shadow, bottom);

        using var cyan = new Pen(Color.FromArgb(255, 72, 202, 255), 5.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var amber = new Pen(Color.FromArgb(255, 255, 177, 69), 5.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawPath(cyan, top);
        graphics.DrawPath(amber, bottom);

        using var outputShadow = new Pen(Color.FromArgb(92, 0, 0, 0), 7.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(outputShadow, 27, 25.2f, 38, 25.2f);
        using var outputStroke = new LinearGradientBrush(
            new PointF(25, 24), new PointF(39, 24),
            Color.FromArgb(255, 230, 246, 255), Color.FromArgb(255, 133, 222, 255));
        using var merged = new Pen(outputStroke, 5.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(merged, 27, 24, 38, 24);

        using var junction = new SolidBrush(Color.FromArgb(255, 241, 248, 255));
        graphics.FillEllipse(junction, 24.75f, 21.75f, 4.5f, 4.5f);
    }

    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    bitmap.SetResolution(96, 96);
    using (var graphics = Graphics.FromImage(bitmap))
    {
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(canvas, new Rectangle(0, 0, size, size));
    }

    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Png);
    return stream.ToArray();
}

static GraphicsPath FlowPath(float startX, float startY, float endX, float endY)
{
    var path = new GraphicsPath();
    path.AddBezier(startX, startY, 19, startY, 19, endY, endX, endY);
    return path;
}

static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
{
    var path = new GraphicsPath();
    var diameter = radius * 2;
    path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
    path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
    path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
    path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
    path.CloseFigure();
    return path;
}

static void WriteIco(string path, IReadOnlyList<(int Size, byte[] Data)> frames)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)frames.Count);
    var offset = 6 + frames.Count * 16;
    foreach (var frame in frames)
    {
        writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
        writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(frame.Data.Length);
        writer.Write(offset);
        offset += frame.Data.Length;
    }
    foreach (var frame in frames) writer.Write(frame.Data);
}

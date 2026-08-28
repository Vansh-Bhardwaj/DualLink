using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace DualLink;

public sealed class TrayManager : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private Icon? _currentIcon;

    public TrayManager(Action show, Action toggle, Action exit)
    {
        _toggleItem = new Forms.ToolStripMenuItem("Arm auto-boost");
        _toggleItem.Click += (_, _) => toggle();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open DualLink", null, (_, _) => show());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit and restore routing", null, (_, _) => exit());

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = "DualLink — normal routing",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => show();
        Update(false, false);
    }

    public void Update(bool armed, bool boosting)
    {
        var color = boosting ? Color.FromArgb(85, 230, 165) : armed ? Color.FromArgb(255, 184, 77) : Color.FromArgb(113, 130, 152);
        var nextIcon = CreateIcon(color);
        _notifyIcon.Icon = nextIcon;
        _currentIcon?.Dispose();
        _currentIcon = nextIcon;
        _notifyIcon.Text = boosting ? "DualLink — boosting both links" : armed ? "DualLink — armed" : "DualLink — normal routing";
        _toggleItem.Text = armed ? "Disarm and restore" : "Arm auto-boost";
    }

    private static Icon CreateIcon(Color accent)
    {
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(12, 19, 27));
        using var ring = new Pen(accent, 5);
        graphics.FillEllipse(background, 3, 3, 58, 58);
        graphics.DrawEllipse(ring, 5, 5, 54, 54);
        using var font = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        var size = graphics.MeasureString("DL", font);
        graphics.DrawString("DL", font, textBrush, (64 - size.Width) / 2, (64 - size.Height) / 2 - 1);
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

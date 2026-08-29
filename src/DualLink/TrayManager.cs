using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace DualLink;

public readonly record struct TraySnapshot(
    bool Armed,
    bool Boosting,
    double DownloadMbps,
    double UploadMbps,
    int ActiveConnections,
    string RoutingMode,
    string RouteQuality);

public sealed class TrayManager : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripLabel _statusItem;
    private readonly Forms.ToolStripLabel _speedItem;
    private readonly Forms.ToolStripLabel _sessionsItem;
    private readonly Forms.ToolStripLabel _qualityItem;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private Icon? _currentIcon;
    private bool? _lastArmed;
    private bool? _lastBoosting;

    public TrayManager(Action show, Action toggle, Action exit)
    {
        _statusItem = new Forms.ToolStripLabel("Normal routing")
        {
            Font = new Font("Segoe UI Variable Text", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(243, 246, 252),
            Padding = new Forms.Padding(10, 8, 10, 1)
        };
        _speedItem = new Forms.ToolStripLabel("↓ 0.0 Mbps    ↑ 0.0 Mbps")
        {
            Font = new Font("Segoe UI Variable Text", 9),
            ForeColor = Color.FromArgb(186, 196, 212),
            Padding = new Forms.Padding(10, 1, 10, 1)
        };
        _sessionsItem = new Forms.ToolStripLabel("0 active sessions")
        {
            Font = new Font("Segoe UI Variable Text", 8.5f),
            ForeColor = Color.FromArgb(131, 146, 166),
            Padding = new Forms.Padding(10, 1, 10, 8)
        };
        _qualityItem = new Forms.ToolStripLabel("Connections ready")
        {
            Font = new Font("Segoe UI Variable Text", 8.5f),
            ForeColor = Color.FromArgb(131, 146, 166),
            Padding = new Forms.Padding(10, 1, 10, 8)
        };

        var openItem = new Forms.ToolStripMenuItem("Open DualLink")
        {
            Font = new Font("Segoe UI Variable Text", 9, FontStyle.Bold),
            Padding = new Forms.Padding(8, 5, 8, 5)
        };
        openItem.Click += (_, _) => show();

        _toggleItem = new Forms.ToolStripMenuItem("Arm automatic boost")
        {
            Padding = new Forms.Padding(8, 5, 8, 5)
        };
        _toggleItem.Click += (_, _) => toggle();

        var exitItem = new Forms.ToolStripMenuItem("Exit and restore routing")
        {
            Padding = new Forms.Padding(8, 5, 8, 5)
        };
        exitItem.Click += (_, _) => exit();

        _menu = new Forms.ContextMenuStrip
        {
            BackColor = Color.FromArgb(25, 29, 38),
            ForeColor = Color.FromArgb(231, 236, 244),
            Font = new Font("Segoe UI Variable Text", 9),
            ShowImageMargin = false,
            Padding = new Forms.Padding(5),
            MinimumSize = new Size(270, 0),
            Renderer = new Forms.ToolStripProfessionalRenderer(new DualLinkColorTable()) { RoundedEdges = true }
        };
        _menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            _statusItem,
            _speedItem,
            _sessionsItem,
            _qualityItem,
            new Forms.ToolStripSeparator(),
            openItem,
            _toggleItem,
            new Forms.ToolStripSeparator(),
            exitItem
        });

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Text = "DualLink — normal routing",
            Visible = true
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) show();
        };
        Update(new TraySnapshot(false, false, 0, 0, 0, "Smart", "Connections ready"));
    }

    public void Update(TraySnapshot snapshot)
    {
        var state = snapshot.Boosting ? $"Boosting · {snapshot.RoutingMode}" : snapshot.Armed ? "Armed · waiting for an app" : "Normal routing";
        _statusItem.Text = state;
        _speedItem.Text = $"↓ {snapshot.DownloadMbps:0.0} Mbps    ↑ {snapshot.UploadMbps:0.0} Mbps";
        _sessionsItem.Text = snapshot.ActiveConnections == 1 ? "1 active session" : $"{snapshot.ActiveConnections} active sessions";
        _qualityItem.Text = snapshot.RouteQuality;
        _toggleItem.Text = snapshot.Armed ? "Disarm and restore" : "Arm automatic boost";

        var tooltip = $"DualLink — {state}\n↓ {snapshot.DownloadMbps:0.0} Mbps · ↑ {snapshot.UploadMbps:0.0} Mbps\n{snapshot.RouteQuality}";
        _notifyIcon.Text = tooltip.Length <= 127 ? tooltip : tooltip[..127];

        if (_lastArmed == snapshot.Armed && _lastBoosting == snapshot.Boosting) return;
        _lastArmed = snapshot.Armed;
        _lastBoosting = snapshot.Boosting;
        var accent = snapshot.Boosting
            ? Color.FromArgb(85, 230, 165)
            : snapshot.Armed ? Color.FromArgb(255, 184, 77) : Color.FromArgb(126, 143, 168);
        var nextIcon = CreateStateIcon(accent);
        _notifyIcon.Icon = nextIcon;
        _currentIcon?.Dispose();
        _currentIcon = nextIcon;
    }

    public void Notify(string title, string message, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(4000);
    }

    public void ShowMenuForPreview() => _menu.Show(Forms.Cursor.Position);

    private static Icon CreateStateIcon(Color accent)
    {
        var pixelSize = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/DualLink.ico"));
        using var stream = resource?.Stream ?? throw new InvalidOperationException("DualLink icon resource is missing.");
        using var sourceIcon = new Icon(stream, pixelSize, pixelSize);
        using var bitmap = sourceIcon.ToBitmap();
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var dotSize = Math.Max(5, pixelSize / 3);
        var left = pixelSize - dotSize - 1;
        var top = pixelSize - dotSize - 1;
        using var border = new SolidBrush(Color.FromArgb(16, 20, 30));
        using var fill = new SolidBrush(accent);
        graphics.FillEllipse(border, left - 1, top - 1, dotSize + 2, dotSize + 2);
        graphics.FillEllipse(fill, left, top, dotSize, dotSize);
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _currentIcon?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private sealed class DualLinkColorTable : Forms.ProfessionalColorTable
    {
        private static readonly Color Surface = Color.FromArgb(25, 29, 38);
        private static readonly Color Hover = Color.FromArgb(43, 50, 64);
        private static readonly Color Border = Color.FromArgb(67, 77, 96);
        public override Color ToolStripDropDownBackground => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Color.FromArgb(82, 96, 121);
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color SeparatorDark => Color.FromArgb(59, 68, 84);
        public override Color SeparatorLight => Color.FromArgb(59, 68, 84);
    }
}

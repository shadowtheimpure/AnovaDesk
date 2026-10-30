using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace AnovaDesk;

/// <summary>
/// Windows notifications hosted on a tray icon. Balloon notifications are used
/// deliberately: they work for an unpackaged, self-contained exe without needing
/// a Start-menu shortcut or an AppUserModelID registration.
/// </summary>
internal sealed class Notifier : IDisposable
{
    private const string AppTitle = "AnovaDesk";

    private readonly NotifyIcon _icon;

    public Notifier(Action onOpen, Action onTest, Action onExit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open AnovaDesk", null, (_, _) => onOpen());
        menu.Items.Add("Test notification", null, (_, _) => onTest());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => onExit());

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = AppTitle,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => onOpen();
    }

    /// <summary>Result of the last Show call, for the in-app traffic log.</summary>
    public string LastStatus { get; private set; } = "not used";

    /// <summary>Shows a notification. Never throws: a failed toast must not stop cook monitoring.</summary>
    public bool Show(string title, string message, bool isError)
    {
        var safeTitle = Truncate(title, 63);
        var safeMessage = Truncate(message, 255);

        // Preferred path: a real toast. Falls back to a shell balloon only if toasts are
        // unavailable, so the tray menu still gives feedback on locked-down machines.
        if (ToastChannel.Show(safeTitle, safeMessage))
        {
            LastStatus = ToastChannel.Status;
            return true;
        }

        try
        {
            _icon.ShowBalloonTip(
                timeout: 8000,
                tipTitle: safeTitle,
                tipText: safeMessage,
                tipIcon: isError ? ToolTipIcon.Warning : ToolTipIcon.Info);

            LastStatus = "toast unavailable (" + ToastChannel.Status + "), balloon sent";
            return true;
        }
        catch (Exception ex)
        {
            LastStatus = "toast and balloon both failed: " + ex.Message;
            return false;
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max];

    private static Icon CreateIcon()
    {
        // Prefer the icon embedded in the exe so the tray matches Explorer and the taskbar.
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var embedded = Icon.ExtractAssociatedIcon(exe!);
                if (embedded is not null) return embedded;
            }
        }
        catch
        {
            // fall through to the drawn placeholder
        }

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var dot = new SolidBrush(Color.FromArgb(0xFF, 0x7A, 0x45));
            g.FillEllipse(dot, 3, 3, 26, 26);

            using var mark = new Pen(Color.FromArgb(0x1A, 0x0E, 0x06), 3f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawArc(mark, 9, 10, 14, 14, 200, 280);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch
        {
            // Shutting down.
        }
    }
}

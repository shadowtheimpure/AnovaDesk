using CommunityToolkit.WinUI.Notifications;
using Windows.UI.Notifications;

namespace AnovaDesk;

/// <summary>
/// Real Windows toast notifications.
///
/// An unpackaged app cannot post toasts from a registry-registered AppUserModelID alone: the
/// shell returns 0x80070490 (ERROR_NOT_FOUND) from CreateToastNotifier. It needs a Start-menu
/// shortcut carrying that AUMID, which the Community Toolkit's compat layer creates on demand.
/// </summary>
public static class ToastChannel
{
    public static string Status { get; private set; } = "not initialised";

    public static bool IsReady { get; private set; }

    public static string Setting { get; private set; } = "unknown";

    public static void Initialize()
    {
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            Setting = notifier.Setting.ToString();
            IsReady = notifier.Setting == NotificationSetting.Enabled;
            Status = "ready (" + Setting + ")";
        }
        catch (Exception ex)
        {
            IsReady = false;
            Setting = "unknown";
            Status = Describe("init", ex);
        }
    }

    public static bool Show(string title, string message)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();

            Status = "toast posted";
            return true;
        }
        catch (Exception ex)
        {
            Status = Describe("show", ex);
            return false;
        }
    }

    /// <summary>Toasts this app currently has in Action Center, or -1 when unavailable.</summary>
    public static int HistoryCount()
    {
        try
        {
            return ToastNotificationManagerCompat.History.GetHistory().Count;
        }
        catch
        {
            return -1;
        }
    }

    public static void ClearHistory()
    {
        try { ToastNotificationManagerCompat.History.Clear(); } catch { }
    }

    private static string Describe(string step, Exception ex) =>
        $"{step} failed: {ex.GetType().Name} hr=0x{ex.HResult:X8} msg='{ex.Message}'";
}

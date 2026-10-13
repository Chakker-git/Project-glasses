using System.Text;
using static GlassesDesk.Native;

namespace GlassesDesk;

/// <summary>
/// The glasses' own display is part of the Windows desktop, so windows can open or be dragged onto
/// it, where they sit hidden behind GlassesDesk's full-screen view. While GlassesDesk runs, this
/// moves any such window onto the centre panel's monitor. Call <see cref="Sweep"/> periodically on the
/// UI thread.
/// </summary>
internal static class WindowShepherd
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "#32768", // taskbars, desktop, menus
        "GlassesDeskWindow",
    };

    // Windows we couldn't move, or that keep coming back (e.g. elevated apps, apps that pin their position).
    private static readonly Dictionary<IntPtr, int> Attempts = new();
    private const int MaxAttempts = 3;

    /// <summary>Glasses monitor and where to put stray windows; set by the render thread after each rebuild.</summary>
    public static volatile Target? Current;

    public sealed record Target(IntPtr GlassesMonitor, RECT GlassesBounds, RECT Destination);

    public static void Sweep()
    {
        if (Current is not { } target) return;
        EnumWindows((hwnd, _) =>
        {
            try { MaybeMove(hwnd, target); } catch { /* window went away mid-move */ }
            return true;
        }, IntPtr.Zero);
    }

    private static void MaybeMove(IntPtr hwnd, Target target)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return;
        if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) != target.GlassesMonitor) return;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return;

        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        if ((style & WS_CAPTION) != WS_CAPTION && (style & WS_THICKFRAME) == 0) return; // popups, tooltips, splash screens
        var exStyle = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 || (exStyle & WS_EX_NOACTIVATE) != 0) return;

        if (Attempts.GetValueOrDefault(hwnd) >= MaxAttempts) return;

        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        if (IgnoredClasses.Contains(cls.ToString())) return;

        Attempts[hwnd] = Attempts.GetValueOrDefault(hwnd) + 1;
        var placement = new WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hwnd, ref placement);
        var dest = target.Destination;

        if (placement.showCmd == SW_SHOWMAXIMIZED)
        {
            // A maximized window maximizes on the monitor its restored position is on.
            placement.rcNormalPosition = Fit(placement.rcNormalPosition, target.GlassesBounds, dest);
            SetWindowPlacement(hwnd, ref placement);
        }
        else
        {
            GetWindowRect(hwnd, out var rect);
            var r = Fit(rect, target.GlassesBounds, dest);
            SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        var title = new StringBuilder(128);
        GetWindowText(hwnd, title, title.Capacity);
        Console.WriteLine(Attempts[hwnd] < MaxAttempts
            ? $"  moved \"{title}\" off the glasses display"
            : $"  can't keep \"{title}\" off the glasses display; leaving it (move it by hand: Win+Shift+Arrow)");
    }

    /// <summary>Same relative position on the destination, shrunk to fit if needed.</summary>
    private static RECT Fit(RECT r, RECT from, RECT to)
    {
        var w = Math.Min(r.Width, to.Width);
        var h = Math.Min(r.Height, to.Height);
        var x = Math.Clamp(to.Left + (r.Left - from.Left), to.Left, to.Right - w);
        var y = Math.Clamp(to.Top + (r.Top - from.Top), to.Top, to.Bottom - h);
        return new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }
}

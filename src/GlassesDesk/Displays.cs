using System.Runtime.InteropServices;
using static GlassesDesk.Native;

namespace GlassesDesk;

/// <summary>One attached monitor as Windows sees it.</summary>
internal sealed record Display(int Index, IntPtr Handle, string DeviceName, string FriendlyName, RECT Bounds, bool IsPrimary)
{
    public bool LooksLikeGlasses =>
        FriendlyName.Contains("SmartGlasses", StringComparison.OrdinalIgnoreCase) ||
        FriendlyName.Contains("RayNeo", StringComparison.OrdinalIgnoreCase);

    /// <summary>A monitor created by the Virtual Display Driver (VirtualDrivers/Virtual-Display-Driver, EDID name "VDD by MTT").</summary>
    public bool IsVirtual => FriendlyName.StartsWith("VDD by MTT", StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        $"[{Index}] {FriendlyName,-28} {DeviceName,-14} {Bounds.Width}x{Bounds.Height} at ({Bounds.Left},{Bounds.Top})" +
        (IsPrimary ? "  primary" : "") + (LooksLikeGlasses ? "  <- glasses" : "") + (IsVirtual ? "  (virtual)" : "");
}

internal static class Displays
{
    public static List<Display> Enumerate()
    {
        var names = FriendlyNamesByGdiName();
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            handles.Add(h);
            return true;
        }, IntPtr.Zero);

        var result = new List<Display>();
        foreach (var h in handles)
        {
            var info = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(h, ref info)) continue;
            result.Add(new Display(0, h, info.szDevice, names.GetValueOrDefault(info.szDevice, "(unknown)"),
                info.rcMonitor, (info.dwFlags & 1) != 0));
        }
        return result
            .OrderBy(d => d.Bounds.Left).ThenBy(d => d.Bounds.Top)
            .Select((d, i) => d with { Index = i })
            .ToList();
    }

    /// <summary>Maps GDI device names (\\.\DISPLAY3) to monitor names from EDID ("SmartGlasses").</summary>
    private static Dictionary<string, string> FriendlyNamesByGdiName()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return map;
        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return map;

        for (var i = 0; i < pathCount; i++)
        {
            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = paths[i].sourceInfo.adapterId,
                    id = paths[i].sourceInfo.id,
                },
            };
            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = paths[i].targetInfo.adapterId,
                    id = paths[i].targetInfo.id,
                },
            };
            if (DisplayConfigGetDeviceInfo(ref source) == 0 && DisplayConfigGetDeviceInfo(ref target) == 0)
            {
                var name = string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName)
                    ? "(internal panel)"
                    : target.monitorFriendlyDeviceName;
                map[source.viewGdiDeviceName] = name;
            }
        }
        return map;
    }
}

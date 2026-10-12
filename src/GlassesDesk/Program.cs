using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using GlassesCore;
using static GlassesDesk.Native;

namespace GlassesDesk;

// World-locked virtual monitors: shows every other monitor as a panel fixed in space around you,
// rendered full-screen on the glasses and counter-rotated against head motion.
internal static class Program
{
    private enum Command { Recenter, Quit, PanelBigger, PanelSmaller, FovUp, FovDown, PredictUp, PredictDown }

    private static readonly (int Id, uint Vk, Command Command, string Label)[] Hotkeys =
    {
        (1, 0x52, Command.Recenter, "R          recenter (look where you want the middle panel, then press)"),
        (2, 0x51, Command.Quit, "Q          quit"),
        (3, 0x26, Command.PanelBigger, "Up/Down    panel size"),
        (4, 0x28, Command.PanelSmaller, ""),
        (5, 0x21, Command.FovUp, "PgUp/PgDn  field of view (tune until panels stay put as you turn)"),
        (6, 0x22, Command.FovDown, ""),
        (7, 0x27, Command.PredictUp, "Left/Right motion prediction"),
        (8, 0x25, Command.PredictDown, ""),
    };

    private static readonly ConcurrentQueue<Command> Commands = new();
    private static WndProc? _wndProc; // keep the delegate alive for the window's lifetime
    private static volatile bool _quit;

    [STAThread]
    private static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        timeBeginPeriod(1);

        var displays = Displays.Enumerate();
        Console.WriteLine("Displays:");
        foreach (var d in displays) Console.WriteLine($"  {d}");
        if (args.Contains("--list")) return 0;

        var glassesDisplay = ArgInt(args, "--glasses") is int gi && gi < displays.Count
            ? displays[gi]
            : displays.FirstOrDefault(d => d.LooksLikeGlasses);
        if (glassesDisplay == null)
        {
            Console.WriteLine("Couldn't find the glasses display. Run with --glasses N using an index above.");
            return 1;
        }
        var sources = displays.Where(d => d != glassesDisplay).ToList();
        if (sources.Count == 0)
        {
            Console.WriteLine("No other monitors to show.");
            return 1;
        }

        var settings = Settings.Load();
        if (ArgFloat(args, "--fov") is float fov) settings.FovDegrees = fov;
        if (ArgFloat(args, "--width") is float width) settings.PanelWidthDegrees = width;
        if (ArgFloat(args, "--gap") is float gap) settings.GapDegrees = gap;
        if (ArgFloat(args, "--predict-ms") is float predict) settings.PredictMilliseconds = predict;
        var centerIndex = ArgInt(args, "--center");

        using var glasses = RayNeoGlasses.TryOpen(out var error);
        if (glasses == null)
        {
            Console.WriteLine(error);
            return 1;
        }
        var filter = new OrientationFilter();
        if (settings.GyroBiasDps is { Length: 3 } b)
            filter.SetInitialBias(new Vector3(b[0], b[1], b[2]) * (MathF.PI / 180f));
        long imuSamples = 0;
        glasses.ImuSample += s => { filter.Update(s); Interlocked.Increment(ref imuSamples); };
        glasses.Disconnected += ex => Console.WriteLine($"Glasses disconnected: {ex.Message}");
        glasses.Start();

        Console.WriteLine();
        Console.WriteLine($"Rendering on {glassesDisplay.FriendlyName} ({glassesDisplay.DeviceName}); showing {sources.Count} monitor(s).");
        Console.WriteLine($"Settings: {settings}");
        Console.WriteLine("Hotkeys (Ctrl+Alt + ...):");
        foreach (var h in Hotkeys.Where(h => h.Label != "")) Console.WriteLine($"  {h.Label}");
        Console.WriteLine("Keep your head still for a couple of seconds while tracking calibrates.");
        Console.WriteLine();

        var hwnd = CreateGlassesWindow(glassesDisplay.Bounds);
        foreach (var h in Hotkeys)
            if (!RegisterHotKey(hwnd, h.Id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, h.Vk))
                Console.WriteLine($"  warning: Ctrl+Alt hotkey {h.Vk:X2} is taken by another app");

        var renderThread = new Thread(() => RenderLoop(hwnd, glassesDisplay.Bounds, sources, settings, centerIndex, filter, () => imuSamples))
        {
            IsBackground = true,
            Name = "Render",
            Priority = ThreadPriority.Highest,
        };
        renderThread.SetApartmentState(ApartmentState.MTA);
        renderThread.Start();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        _quit = true;
        renderThread.Join(2000);
        // Real bias on these glasses is ~0.1 °/s; anything larger is probably head sway that slipped
        // through, and seeding the next session with it would make drift sticky.
        var bias = filter.GyroBias * (180f / MathF.PI);
        settings.GyroBiasDps = bias.Length() < 0.5f ? new[] { bias.X, bias.Y, bias.Z } : null;
        settings.Save();
        Console.WriteLine($"Saved settings: {settings}");
        return 0;
    }

    private static void RenderLoop(IntPtr hwnd, RECT bounds, List<Display> sources, Settings settings,
        int? centerIndex, OrientationFilter filter, Func<long> imuSamples)
    {
        try
        {
            MonitorCapture.InitializeThread();
            using var renderer = new Renderer(hwnd, bounds.Width, bounds.Height);
            var winrtDevice = MonitorCapture.CreateWinRTDevice(renderer.Device);
            var captures = new List<MonitorCapture>();
            foreach (var display in sources)
            {
                try { captures.Add(new MonitorCapture(renderer.Device, winrtDevice, display)); }
                catch (Exception ex) { Console.WriteLine($"Can't capture {display.FriendlyName}: {ex.Message}"); }
            }

            IReadOnlyList<(PanelSource Source, PanelPlacement Placement)> layout = Array.Empty<(PanelSource, PanelPlacement)>();
            var byBounds = captures.ToDictionary(c => new PanelSource(c.Display.Bounds.Left, c.Display.Bounds.Top, c.Display.Bounds.Width, c.Display.Bounds.Height));
            void Relayout() => layout = PanelLayout.Arrange(byBounds.Keys, settings.PanelWidthDegrees, settings.GapDegrees, centerIndex);
            Relayout();

            var aspect = bounds.Width / (float)bounds.Height;
            var anchorYaw = 0f;
            var anchorPitch = 0f;
            var centered = false;
            var frames = 0;
            var clock = Stopwatch.StartNew();
            var lastSamples = imuSamples();

            while (!_quit)
            {
                renderer.WaitForFrame();

                while (Commands.TryDequeue(out var command))
                {
                    switch (command)
                    {
                        case Command.Recenter: centered = false; break;
                        case Command.PanelBigger: settings.PanelWidthDegrees += 2; Relayout(); break;
                        case Command.PanelSmaller: settings.PanelWidthDegrees = MathF.Max(10, settings.PanelWidthDegrees - 2); Relayout(); break;
                        case Command.FovUp: settings.FovDegrees += 0.5f; break;
                        case Command.FovDown: settings.FovDegrees -= 0.5f; break;
                        case Command.PredictUp: settings.PredictMilliseconds += 4; break;
                        case Command.PredictDown: settings.PredictMilliseconds = MathF.Max(0, settings.PredictMilliseconds - 4); break;
                    }
                    if (command != Command.Recenter) Console.WriteLine($"  {settings}");
                }

                foreach (var capture in captures) capture.Update(renderer.Context);

                var head = filter.PredictOrientation(settings.PredictMilliseconds / 1000f);
                if (!centered && filter.IsReady)
                {
                    var forward = Vector3.Transform(-Vector3.UnitZ, head);
                    anchorYaw = MathF.Atan2(-forward.X, -forward.Z) * 180f / MathF.PI;
                    anchorPitch = MathF.Asin(Math.Clamp(forward.Y, -1, 1)) * 180f / MathF.PI;
                    centered = true;
                    Console.WriteLine($"  recentered (yaw {anchorYaw:F1}°, pitch {anchorPitch:F1}°)");
                }

                // Diagonal FOV -> vertical FOV for this aspect ratio.
                var tanHalfDiag = MathF.Tan(settings.FovDegrees * MathF.PI / 360f);
                var verticalFov = 2 * MathF.Atan(tanHalfDiag / MathF.Sqrt(1 + aspect * aspect));
                var view = Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(head));
                var projection = Matrix4x4.CreatePerspectiveFieldOfView(verticalFov, aspect, 0.05f, 100f);

                const float distance = 2f;
                var panels = layout
                    .Select(l => (Capture: byBounds[l.Source], l.Placement))
                    .Where(p => p.Capture.View != null)
                    .Select(p => (p.Capture.View!, PanelLayout.WorldMatrix(p.Placement, distance, anchorPitch, anchorYaw)));
                renderer.Render(view * projection, centered ? panels : Enumerable.Empty<(Vortice.Direct3D11.ID3D11ShaderResourceView, Matrix4x4)>());

                frames++;
                if (clock.ElapsedMilliseconds >= 5000)
                {
                    var samples = imuSamples();
                    Console.WriteLine($"  {frames / clock.Elapsed.TotalSeconds:F0} fps, IMU {(samples - lastSamples) / clock.Elapsed.TotalSeconds:F0} Hz");
                    lastSamples = samples;
                    frames = 0;
                    clock.Restart();
                }
            }

            foreach (var capture in captures) capture.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Renderer failed: {ex}");
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static IntPtr CreateGlassesWindow(RECT bounds)
    {
        _wndProc = WindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "GlassesDeskWindow",
        };
        RegisterClassEx(ref wc);
        // Topmost, borderless, never takes focus, not in the taskbar.
        return CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, wc.lpszClassName, "Glasses Desk",
            WS_POPUP | WS_VISIBLE, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY:
                var hotkey = Hotkeys.FirstOrDefault(h => h.Id == (int)wParam);
                if (hotkey.Command == Command.Quit) DestroyWindow(hwnd);
                else if (hotkey.Id != 0) Commands.Enqueue(hotkey.Command);
                return IntPtr.Zero;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                foreach (var h in Hotkeys) UnregisterHotKey(hwnd, h.Id);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static int? ArgInt(string[] args, string name) =>
        Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : null;

    private static float? ArgFloat(string[] args, string name) =>
        Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length &&
        float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
}

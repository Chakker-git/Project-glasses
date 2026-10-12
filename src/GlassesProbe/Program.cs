using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using HidSharp;
using HidSharp.Reports;

namespace GlassesProbe;

// Milestone 1: find out whether the glasses expose a motion sensor (IMU) to Windows,
// and capture enough raw data to reverse-engineer its report format.
//
//   GlassesProbe list                 enumerate HID + USB devices, flag likely glasses
//   GlassesProbe dump [targets] [--seconds N]
//                                     read raw input reports while you follow
//                                     on-screen head-movement prompts
//   GlassesProbe imu [--seconds N]    start the RayNeo IMU stream and show live orientation
//
// list and dump are read-only. imu sends the IMU on/off and device-info commands.
internal static class Program
{
    // 0x1BBB is TCL's USB vendor ID (RayNeo is a TCL brand). Unconfirmed for the Air 4 Pro,
    // so we also match on name strings.
    private static readonly HashSet<int> SuspectVendorIds = new() { 0x1BBB };
    private static readonly string[] SuspectNames = { "rayneo", "tcl", "falcon", "nxtwear", "air" };

    private static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var rest = args.Skip(1).ToArray();

        var logPath = $"probe-{command}-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        using var log = new StreamWriter(logPath) { AutoFlush = true };
        var tee = new TeeWriter(Console.Out, log);
        Console.SetOut(tee);

        try
        {
            switch (command)
            {
                case "list":
                    ListDevices();
                    break;
                case "dump":
                    Dump(rest);
                    break;
                case "imu":
                    ImuCommand.Run(rest);
                    break;
                default:
                    PrintHelp();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"Output saved to {Path.GetFullPath(logPath)}");
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  GlassesProbe list");
        Console.WriteLine("  GlassesProbe dump                 (all flagged devices)");
        Console.WriteLine("  GlassesProbe dump 3 7             (by index from 'list')");
        Console.WriteLine("  GlassesProbe dump 1bbb            (by vendor ID)");
        Console.WriteLine("  GlassesProbe dump 1bbb:af50       (by vendor:product ID)");
        Console.WriteLine("  Options: --seconds N  (default 30)");
        Console.WriteLine("  GlassesProbe imu [--seconds N]     turn on the glasses' IMU and show live head orientation");
    }

    // ---------------------------------------------------------------- list

    private static List<HidDevice> GetHidDevices() =>
        DeviceList.Local.GetHidDevices()
            .OrderBy(d => d.VendorID).ThenBy(d => d.ProductID).ThenBy(d => d.DevicePath)
            .ToList();

    private static bool IsSuspect(HidDevice d)
    {
        if (SuspectVendorIds.Contains(d.VendorID)) return true;
        var text = $"{Safe(d.GetManufacturer)} {Safe(d.GetProductName)}".ToLowerInvariant();
        return SuspectNames.Any(text.Contains);
    }

    private static void ListDevices()
    {
        var devices = GetHidDevices();
        Console.WriteLine($"=== HID devices ({devices.Count}) ===   * = possibly the glasses");
        Console.WriteLine();

        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var suspect = IsSuspect(d);
            Console.WriteLine($"{(suspect ? "*" : " ")}[{i}] {d.VendorID:X4}:{d.ProductID:X4}  " +
                              $"{Safe(d.GetManufacturer)} / {Safe(d.GetProductName)}");
            Console.WriteLine($"      report bytes: in={SafeInt(d.GetMaxInputReportLength)} " +
                              $"out={SafeInt(d.GetMaxOutputReportLength)} " +
                              $"feature={SafeInt(d.GetMaxFeatureReportLength)}   " +
                              $"usages: {DescribeUsages(d)}");
            Console.WriteLine($"      path: {d.DevicePath}");

            if (suspect)
            {
                try
                {
                    var raw = d.GetRawReportDescriptor();
                    Console.WriteLine($"      report descriptor ({raw.Length} bytes):");
                    foreach (var line in Hex(raw).Chunk(32 * 3))
                        Console.WriteLine($"        {new string(line)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"      report descriptor: unavailable ({ex.GetType().Name})");
                }
            }
            Console.WriteLine();
        }

        if (OperatingSystem.IsWindows())
        {
            ListWmi("USB devices (all interfaces, including non-HID)",
                "SELECT Name, DeviceID, PNPClass, Service FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'");
            ListWmi("Monitors",
                "SELECT Name, DeviceID, PNPClass, Service FROM Win32_PnPEntity WHERE PNPClass = 'Monitor'");
        }

        var flagged = devices.Count(IsSuspect);
        Console.WriteLine(flagged > 0
            ? $"{flagged} HID interface(s) flagged. Next: run 'GlassesProbe imu'."
            : "Nothing flagged automatically. Look for the glasses in the USB list above and " +
              "run 'GlassesProbe dump <vid>' with their vendor ID.");
    }

    [SupportedOSPlatform("windows")]
    private static void ListWmi(string title, string query)
    {
        Console.WriteLine($"=== {title} ===");
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            var rows = searcher.Get().Cast<ManagementObject>()
                .Select(o => (
                    Id: o["DeviceID"]?.ToString() ?? "",
                    Name: o["Name"]?.ToString() ?? "",
                    Class: o["PNPClass"]?.ToString() ?? "",
                    Service: o["Service"]?.ToString() ?? ""))
                .OrderBy(r => r.Id)
                .ToList();
            foreach (var r in rows)
                Console.WriteLine($"  {r.Id}\n      {r.Name}  [class={r.Class}, driver={r.Service}]");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  WMI query failed: {ex.Message}");
        }
        Console.WriteLine();
    }

    private static string DescribeUsages(HidDevice d)
    {
        try
        {
            var usages = d.GetReportDescriptor().DeviceItems
                .SelectMany(item => item.Usages.GetAllValues())
                .Distinct()
                .Select(u => $"{u >> 16:X4}:{u & 0xFFFF:X4}{UsageName(u)}");
            return string.Join(", ", usages);
        }
        catch
        {
            return "(unreadable)";
        }
    }

    private static string UsageName(uint usage) => (usage >> 16, usage & 0xFFFF) switch
    {
        (0x01, 0x02) => " mouse",
        (0x01, 0x06) => " keyboard",
        (0x01, 0x80) => " system-control",
        (0x0C, _) => " consumer",
        (0x20, _) => " SENSOR",
        ( >= 0xFF00, _) => " vendor",
        _ => "",
    };

    // ---------------------------------------------------------------- dump

    // Movement script shown during capture. Phases are recorded in the CSV so the summary can
    // show which bytes move with which motion.
    private static readonly (double Until, string Name, string Prompt)[] Phases =
    {
        (5, "still", "Hold your head completely STILL"),
        (12, "yaw", "Turn your head slowly LEFT and RIGHT (like saying no)"),
        (19, "pitch", "Nod slowly UP and DOWN (like saying yes)"),
        (26, "roll", "Tilt your head ear-to-shoulder, LEFT and RIGHT"),
        (double.MaxValue, "free", "Move however you like (or wait)"),
    };

    private static string PhaseAt(double t, out string prompt)
    {
        var p = Phases.First(p => t < p.Until);
        prompt = p.Prompt;
        return p.Name;
    }

    private static void Dump(string[] args)
    {
        var seconds = 30;
        var targets = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--seconds" && i + 1 < args.Length) seconds = int.Parse(args[++i]);
            else targets.Add(args[i].ToLowerInvariant());
        }

        var all = GetHidDevices();
        var selected = SelectDevices(all, targets);
        if (selected.Count == 0)
        {
            Console.WriteLine("No matching HID devices. Run 'GlassesProbe list' first.");
            return;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var recorders = new List<Recorder>();
        foreach (var (index, device) in selected)
        {
            var label = $"[{index}] {device.VendorID:X4}:{device.ProductID:X4} {Safe(device.GetProductName)}";
            if (!device.TryOpen(out var stream))
            {
                Console.WriteLine($"{label}: could not open (Windows locks keyboards/mice; others may be in use).");
                continue;
            }
            var csv = $"probe-dump-{stamp}-{index}-{device.VendorID:X4}{device.ProductID:X4}.csv";
            recorders.Add(new Recorder(label, stream, csv));
            Console.WriteLine($"{label}: opened, logging to {csv}");
        }
        if (recorders.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine($"Capturing for {seconds}s. Put the glasses ON and follow the prompts. Ctrl+C stops early.");
        Console.WriteLine("Starting in 3...");
        Thread.Sleep(3000);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var clock = Stopwatch.StartNew();
        var threads = recorders.Select(r =>
        {
            var t = new Thread(() => r.Run(clock, cts.Token)) { IsBackground = true };
            t.Start();
            return t;
        }).ToList();

        var lastPhase = "";
        while (!cts.IsCancellationRequested && clock.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(1000);
            var t = clock.Elapsed.TotalSeconds;
            var phase = PhaseAt(t, out var prompt);
            if (phase != lastPhase)
            {
                Console.WriteLine();
                Console.WriteLine($">>> {prompt}");
                lastPhase = phase;
            }
            foreach (var r in recorders)
                Console.WriteLine($"  {t,5:F1}s {r.Label}: {r.TakeRecentCount(),4} reports/s  last: {r.LastHex}");
        }

        cts.Cancel();
        foreach (var t in threads) t.Join(2000);

        Console.WriteLine();
        Console.WriteLine("=================== SUMMARY ===================");
        foreach (var r in recorders)
        {
            r.Dispose();
            r.PrintSummary(clock.Elapsed.TotalSeconds);
        }
        Console.WriteLine("Paste this SUMMARY (or the whole saved .txt) back to Claude.");
    }

    private static List<(int Index, HidDevice Device)> SelectDevices(List<HidDevice> all, List<string> targets)
    {
        var indexed = all.Select((d, i) => (Index: i, Device: d)).ToList();
        if (targets.Count == 0) return indexed.Where(x => IsSuspect(x.Device)).ToList();

        var result = new List<(int, HidDevice)>();
        foreach (var t in targets)
        {
            if (!t.Contains(':') && t.Length < 4 && int.TryParse(t, out var idx) && idx >= 0 && idx < all.Count)
            {
                result.Add(indexed[idx]);
                continue;
            }
            var parts = t.Split(':');
            var vid = Convert.ToInt32(parts[0], 16);
            int? pid = parts.Length > 1 ? Convert.ToInt32(parts[1], 16) : null;
            result.AddRange(indexed.Where(x => x.Device.VendorID == vid && (pid == null || x.Device.ProductID == pid)));
        }
        return result.Distinct().ToList();
    }

    // ---------------------------------------------------------------- helpers

    internal static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes) sb.Append(b.ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    private static string Safe(Func<string> f)
    {
        try { return f() ?? "?"; } catch { return "?"; }
    }

    private static string SafeInt(Func<int> f)
    {
        try { return f().ToString(); } catch { return "?"; }
    }

    internal static string PhaseNameAt(double t) => PhaseAt(t, out _);
    internal static IEnumerable<string> PhaseNames => Phases.Select(p => p.Name);
}

/// <summary>Reads one HID interface on a background thread and accumulates per-byte statistics.</summary>
internal sealed class Recorder : IDisposable
{
    private readonly HidStream _stream;
    private readonly StreamWriter _csv;
    private readonly object _lock = new();
    private int _recent;
    private long _total;
    private string _lastHex = "(none yet)";
    private readonly Dictionary<int, int> _lengths = new();
    private readonly Dictionary<byte, long> _reportIds = new();
    private ByteStats[] _stats = Array.Empty<ByteStats>();
    private byte[]? _previous;
    private string? _error;

    public string Label { get; }

    public Recorder(string label, HidStream stream, string csvPath)
    {
        Label = label;
        _stream = stream;
        _stream.ReadTimeout = 500;
        _csv = new StreamWriter(csvPath);
        _csv.WriteLine("t_ms,phase,length,hex");
    }

    public string LastHex { get { lock (_lock) return _lastHex; } }

    public int TakeRecentCount() => Interlocked.Exchange(ref _recent, 0);

    public void Run(Stopwatch clock, CancellationToken token)
    {
        var buffer = new byte[Math.Max(_stream.Device.GetMaxInputReportLength(), 1)];
        while (!token.IsCancellationRequested)
        {
            int n;
            try
            {
                n = _stream.Read(buffer, 0, buffer.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _error = ex.Message;
                return;
            }
            if (n <= 0) continue;

            var t = clock.Elapsed.TotalSeconds;
            var phase = Program.PhaseNameAt(t);
            var report = buffer.AsSpan(0, n);
            var hex = Program.Hex(report);

            lock (_lock)
            {
                _total++;
                _lastHex = hex;
                _lengths[n] = _lengths.GetValueOrDefault(n) + 1;
                _reportIds[report[0]] = _reportIds.GetValueOrDefault(report[0]) + 1;
                if (_stats.Length < n)
                {
                    var grown = new ByteStats[n];
                    Array.Copy(_stats, grown, _stats.Length);
                    for (var i = _stats.Length; i < n; i++) grown[i] = new ByteStats();
                    _stats = grown;
                }
                for (var i = 0; i < n; i++)
                    _stats[i].Add(report[i], _previous != null && i < _previous.Length ? _previous[i] : (int?)null, phase);
                _previous = report.ToArray();
                _csv.WriteLine($"{t * 1000:F2},{phase},{n},{hex}");
            }
            Interlocked.Increment(ref _recent);
        }
    }

    public void PrintSummary(double seconds)
    {
        lock (_lock)
        {
            Console.WriteLine();
            Console.WriteLine(Label);
            if (_error != null) Console.WriteLine($"  stopped with error: {_error}");
            if (_total == 0)
            {
                Console.WriteLine("  SILENT: no input reports. The sensor may need an enable command, or isn't on this interface.");
                return;
            }
            Console.WriteLine($"  reports: {_total}  (~{_total / seconds:F0}/s)");
            Console.WriteLine($"  lengths: {string.Join(", ", _lengths.Select(kv => $"{kv.Key}B x{kv.Value}"))}");
            Console.WriteLine($"  first byte (report id?): {string.Join(", ", _reportIds.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key:X2} x{kv.Value}"))}");

            var phases = Program.PhaseNames.ToArray();
            Console.WriteLine("  bytes that change (offset: min-max, distinct, mean |delta| per phase " +
                              string.Join("/", phases) + "):");
            var anyChanging = false;
            for (var i = 0; i < _stats.Length; i++)
            {
                var s = _stats[i];
                if (s.Distinct <= 1) continue;
                anyChanging = true;
                var deltas = string.Join("/", phases.Select(p => s.MeanDelta(p).ToString("F1")));
                Console.WriteLine($"    {i,3}: {s.Min:X2}-{s.Max:X2}  distinct={s.Distinct,3}  delta {deltas}");
            }
            if (!anyChanging) Console.WriteLine("    (none - every report was identical)");
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        lock (_lock) _csv.Dispose();
    }

    private sealed class ByteStats
    {
        private readonly bool[] _seen = new bool[256];
        private readonly Dictionary<string, (double Sum, long Count)> _deltas = new();
        public int Distinct { get; private set; }
        public byte Min { get; private set; } = 0xFF;
        public byte Max { get; private set; }

        public void Add(byte value, int? previous, string phase)
        {
            if (!_seen[value]) { _seen[value] = true; Distinct++; }
            if (value < Min) Min = value;
            if (value > Max) Max = value;
            if (previous is int p)
            {
                var (sum, count) = _deltas.GetValueOrDefault(phase);
                _deltas[phase] = (sum + Math.Abs(value - p), count + 1);
            }
        }

        public double MeanDelta(string phase) =>
            _deltas.TryGetValue(phase, out var d) && d.Count > 0 ? d.Sum / d.Count : 0;
    }
}

/// <summary>Writes to the console and a log file at once.</summary>
internal sealed class TeeWriter : TextWriter
{
    private readonly TextWriter _a, _b;
    public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
    public override Encoding Encoding => _a.Encoding;
    public override void Write(char value) { _a.Write(value); _b.Write(value); }
    public override void Write(string? value) { _a.Write(value); _b.Write(value); }
    public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
    public override void Flush() { _a.Flush(); _b.Flush(); }
}

using System.Diagnostics;
using GlassesCore;

namespace GlassesProbe;

// Stage 2 check: turn on the IMU, decode it, and show live head orientation.
internal static class ImuCommand
{
    public static void Run(string[] args)
    {
        var seconds = 120;
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--seconds" && i + 1 < args.Length) seconds = int.Parse(args[++i]);

        using var glasses = RayNeoGlasses.TryOpen(out var error);
        if (glasses == null)
        {
            Console.WriteLine(error);
            return;
        }
        Console.WriteLine($"Opened {glasses.Device.DevicePath}");

        var filter = new OrientationFilter();
        var csvPath = $"probe-imu-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        using var csv = new StreamWriter(csvPath);
        csv.WriteLine("tick,ax_g,ay_g,az_g,gx_rads,gy_rads,gz_rads,mx,my,mz,temp_c");

        var sync = new object();
        ImuSample latest = default;
        long samples = 0, samplesThisSecond = 0;
        var otherFrames = 0;
        string? disconnected = null;

        glasses.ImuSample += s =>
        {
            filter.Update(s);
            lock (sync)
            {
                latest = s;
                samples++;
                samplesThisSecond++;
                var m = s.Magnetometer;
                csv.WriteLine($"{s.Tick},{s.Accel.X:F4},{s.Accel.Y:F4},{s.Accel.Z:F4}," +
                              $"{s.Gyro.X:F4},{s.Gyro.Y:F4},{s.Gyro.Z:F4}," +
                              $"{m?.X:F1},{m?.Y:F1},{m?.Z:F1},{s.Temperature:F1}");
            }
        };
        glasses.OtherFrame += frame =>
        {
            if (RayNeoProtocol.TryDecodeDeviceInfo(frame, out var info))
                Console.WriteLine($"  device info: board 0x{info.BoardId:X2}, sensor on={info.SensorOn}, firmware '{info.FirmwareDate}'");
            else if (Interlocked.Increment(ref otherFrames) <= 10)
                Console.WriteLine($"  other frame: {Program.Hex(frame)}");
        };
        glasses.Disconnected += ex => disconnected = ex.Message;

        Console.WriteLine("Starting IMU. Keep your head STILL for the first 3 seconds (gyro calibration).");
        Console.WriteLine("Keys: R = recenter, Q = quit.");
        Console.WriteLine();
        glasses.Start();

        var clock = Stopwatch.StartNew();
        var nextRate = 1000L;
        var nextPrint = 0L;
        var rate = 0L;
        var interactive = !Console.IsInputRedirected;
        while (clock.Elapsed.TotalSeconds < seconds && disconnected == null)
        {
            Thread.Sleep(100);
            if (interactive && Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                if (key == ConsoleKey.Q) break;
                if (key == ConsoleKey.R) { filter.Recenter(); Console.WriteLine("  -- recentered --"); }
            }

            ImuSample s;
            lock (sync)
            {
                s = latest;
                if (clock.ElapsedMilliseconds >= nextRate)
                {
                    rate = samplesThisSecond;
                    samplesThisSecond = 0;
                    nextRate += 1000;
                }
            }
            if (samples == 0)
            {
                if (clock.ElapsedMilliseconds > 3000 && clock.ElapsedMilliseconds >= nextPrint)
                {
                    Console.WriteLine("  no IMU frames yet...");
                    nextPrint = clock.ElapsedMilliseconds + 1000;
                }
                continue;
            }

            // Print 4x per second so it stays readable in a scrolling terminal.
            if (clock.ElapsedMilliseconds < nextPrint) continue;
            nextPrint = clock.ElapsedMilliseconds + 250;
            var (yaw, pitch, roll) = filter.GetEulerDegrees();
            var mag = s.Magnetometer is { } m ? $"{m.X,7:F0}{m.Y,7:F0}{m.Z,7:F0}" : "   (none)          ";
            Console.WriteLine(
                $"{clock.Elapsed.TotalSeconds,6:F1}s {rate,4}Hz | yaw {yaw,7:F1}  pitch {pitch,6:F1}  roll {roll,6:F1} | " +
                $"acc {s.Accel.X,6:F2}{s.Accel.Y,6:F2}{s.Accel.Z,6:F2} g | " +
                $"gyro {Deg(s.Gyro.X),7:F1}{Deg(s.Gyro.Y),7:F1}{Deg(s.Gyro.Z),7:F1} dps | " +
                $"mag {mag} | {s.Temperature:F1}C{(filter.IsReady ? "" : "  calibrating")}");
        }

        glasses.Dispose(); // stops the IMU and the reader thread before the CSV closes

        Console.WriteLine();
        if (disconnected != null) Console.WriteLine($"Glasses disconnected: {disconnected}");
        var b = filter.GyroBias;
        Console.WriteLine($"Samples: {samples} in {clock.Elapsed.TotalSeconds:F1}s (~{samples / clock.Elapsed.TotalSeconds:F0} Hz)");
        Console.WriteLine($"Learned gyro bias: {Deg(b.X):F2} {Deg(b.Y):F2} {Deg(b.Z):F2} dps");
        Console.WriteLine($"Raw samples saved to {Path.GetFullPath(csvPath)}");
        if (samples == 0)
            Console.WriteLine("No IMU data arrived. Paste this output to Claude; next step is trying alternate commands.");
    }

    private static float Deg(float rad) => rad * 180f / MathF.PI;
}

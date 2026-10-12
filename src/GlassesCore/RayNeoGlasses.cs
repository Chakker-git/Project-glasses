using HidSharp;

namespace GlassesCore;

/// <summary>
/// Connection to the glasses' vendor HID interface. Starts the IMU stream on <see cref="Start"/>
/// and stops it on <see cref="Dispose"/>.
/// </summary>
public sealed class RayNeoGlasses : IDisposable
{
    private readonly HidStream _stream;
    private readonly object _writeLock = new();
    private Thread? _reader;
    private volatile bool _running;

    public HidDevice Device { get; }

    /// <summary>Raised on the reader thread for every decoded IMU frame.</summary>
    public event Action<ImuSample>? ImuSample;

    /// <summary>Raised on the reader thread for every non-IMU frame (acks, notifications).</summary>
    public event Action<byte[]>? OtherFrame;

    /// <summary>Raised once if the reader stops because the device went away.</summary>
    public event Action<Exception>? Disconnected;

    private RayNeoGlasses(HidDevice device, HidStream stream)
    {
        Device = device;
        _stream = stream;
        _stream.ReadTimeout = 500;
        _stream.WriteTimeout = 500;
    }

    /// <summary>Finds and opens the glasses, or returns null with a reason.</summary>
    public static RayNeoGlasses? TryOpen(out string error)
    {
        var candidates = DeviceList.Local.GetHidDevices(RayNeoProtocol.VendorId, RayNeoProtocol.ProductId)
            .Where(d => SafeLength(d) >= RayNeoProtocol.FrameLength)
            .ToList();
        if (candidates.Count == 0)
        {
            error = "Glasses not found (no HID device 1BBB:AF50 with 64-byte reports). Are they plugged in?";
            return null;
        }
        foreach (var device in candidates)
        {
            if (device.TryOpen(out var stream))
            {
                error = "";
                return new RayNeoGlasses(device, stream);
            }
        }
        error = "Found the glasses but couldn't open them. Close any RayNeo app that might be holding them.";
        return null;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "RayNeo IMU", Priority = ThreadPriority.AboveNormal };
        _reader.Start();
        Send(RayNeoProtocol.CmdDeviceInfo);
        Send(RayNeoProtocol.CmdImuOn);
    }

    public void Send(byte command, byte value = 0)
    {
        var report = RayNeoProtocol.BuildCommand(command, value);
        lock (_writeLock) _stream.Write(report, 0, report.Length);
    }

    private void ReadLoop()
    {
        var buffer = new byte[Math.Max(SafeLength(Device), RayNeoProtocol.FrameLength + 1)];
        while (_running)
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
                if (_running) Disconnected?.Invoke(ex);
                return;
            }

            var frame = RayNeoProtocol.GetFrame(buffer.AsSpan(0, n));
            if (frame.IsEmpty) continue;
            if (RayNeoProtocol.TryDecodeImu(frame, out var sample)) ImuSample?.Invoke(sample);
            else OtherFrame?.Invoke(frame.ToArray());
        }
    }

    public void Dispose()
    {
        if (_running)
        {
            try { Send(RayNeoProtocol.CmdImuOff); } catch { /* device may already be gone */ }
            _running = false;
            _reader?.Join(1000);
        }
        _stream.Dispose();
    }

    private static int SafeLength(HidDevice d)
    {
        try { return d.GetMaxInputReportLength(); } catch { return 0; }
    }
}

using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace GlassesCore;

/// <summary>One decoded IMU frame from the glasses.</summary>
/// <param name="Accel">Acceleration in g, body axes (X right, Y up, Z back). Reads about +1g on Y at rest.</param>
/// <param name="Gyro">Angular velocity in rad/s, body axes.</param>
/// <param name="Magnetometer">Raw magnetometer, remapped to body axes; null when the frame carries a placeholder.</param>
/// <param name="Temperature">Sensor temperature, °C.</param>
/// <param name="Tick">Device clock, 10 kHz ticks.</param>
public readonly record struct ImuSample(
    Vector3 Accel, Vector3 Gyro, Vector3? Magnetometer, float Temperature, uint Tick);

/// <summary>Basic fields from the device-info reply (frame type 0xC8).</summary>
public readonly record struct DeviceInfo(byte BoardId, bool SensorOn, string FirmwareDate);

/// <summary>
/// RayNeo Air-series USB HID protocol (VID 1BBB, PID AF50; Air 2/3s/4 Pro share it).
/// Layout cross-checked against two MIT-licensed projects:
/// verncat/RayNeo-Air-3S-Pro-OpenVR (src/RayneoApi.cpp) and MegaBadCoder/hovera (RayNeoProtocol.swift).
/// </summary>
/// <remarks>
/// Host to glasses: 64-byte frame <c>66 cmd value payload...</c>.
/// Glasses to host: 64-byte frame <c>99 type ...</c>. IMU frames (type 0x65) carry float32 LE
/// accel m/s² at 4/8/12, gyro °/s at 16/20/24, temperature at 28, magnetometer at 32/36/52,
/// and a u32 tick at 40.
/// </remarks>
public static class RayNeoProtocol
{
    public const int VendorId = 0x1BBB;
    public const int ProductId = 0xAF50;
    public const int FrameLength = 64;

    public const byte CmdDeviceInfo = 0x00;
    public const byte CmdImuOn = 0x01;
    public const byte CmdImuOff = 0x02;

    public const byte FrameImu = 0x65;
    public const byte FrameAck = 0xC8;

    private const byte OutMagic = 0x66;
    private const byte InMagic = 0x99;
    private const float StandardGravity = 9.80665f;
    private const float DegToRad = MathF.PI / 180f;
    private const float MagnetometerPlaceholder = 3200f;

    /// <summary>Builds an output report: report ID 0 followed by the 64-byte command frame.</summary>
    public static byte[] BuildCommand(byte command, byte value = 0)
    {
        var report = new byte[FrameLength + 1];
        report[1] = OutMagic;
        report[2] = command;
        report[3] = value;
        return report;
    }

    /// <summary>
    /// Strips the Windows report-ID prefix if present and returns the 64-byte frame,
    /// or an empty span if this isn't a RayNeo frame.
    /// </summary>
    public static ReadOnlySpan<byte> GetFrame(ReadOnlySpan<byte> report)
    {
        if (report.Length > FrameLength && report[0] == 0 && report[1] == InMagic) report = report[1..];
        return report.Length >= FrameLength && report[0] == InMagic ? report[..FrameLength] : default;
    }

    public static bool TryDecodeImu(ReadOnlySpan<byte> frame, out ImuSample sample)
    {
        sample = default;
        if (frame.Length < FrameLength || frame[0] != InMagic || frame[1] != FrameImu) return false;

        var accel = new Vector3(F(frame, 4), F(frame, 8), F(frame, 12)) / StandardGravity;
        var gyro = new Vector3(F(frame, 16), F(frame, 20), F(frame, 24)) * DegToRad;
        Vector3? mag = null;
        float mx = F(frame, 32), my = F(frame, 36), mz = F(frame, 52);
        if (MathF.Abs(mx) != MagnetometerPlaceholder && MathF.Abs(my) != MagnetometerPlaceholder)
            mag = new Vector3(my, -mx, mz); // magnetometer axes are rotated 90° relative to accel/gyro
        var tick = BinaryPrimitives.ReadUInt32LittleEndian(frame[40..]);

        sample = new ImuSample(accel, gyro, mag, F(frame, 28), tick);
        return true;
    }

    private static float F(ReadOnlySpan<byte> frame, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(frame[offset..]);

    public static bool TryDecodeDeviceInfo(ReadOnlySpan<byte> frame, out DeviceInfo info)
    {
        info = default;
        if (frame.Length < FrameLength || frame[0] != InMagic || frame[1] != FrameAck) return false;
        // Payload starts at 4: cpuid[12], board_id, sensor_on, support_fov, date[12], ...
        var date = Encoding.ASCII.GetString(frame.Slice(4 + 15, 12)).TrimEnd('\0', ' ');
        info = new DeviceInfo(frame[4 + 12], frame[4 + 13] != 0, date);
        return true;
    }
}

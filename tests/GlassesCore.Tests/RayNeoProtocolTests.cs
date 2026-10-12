using System.Buffers.Binary;
using GlassesCore;
using Xunit;

namespace GlassesCore.Tests;

public class RayNeoProtocolTests
{
    internal static byte[] ImuFrame(float[] accelMs2, float[] gyroDps, uint tick, float mx = 10, float my = 20, float mz = 30)
    {
        var f = new byte[64];
        f[0] = 0x99;
        f[1] = 0x65;
        void W(int off, float v) => BinaryPrimitives.WriteSingleLittleEndian(f.AsSpan(off), v);
        for (var i = 0; i < 3; i++) W(4 + i * 4, accelMs2[i]);
        for (var i = 0; i < 3; i++) W(16 + i * 4, gyroDps[i]);
        W(28, 36.5f);
        W(32, mx);
        W(36, my);
        W(52, mz);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(40), tick);
        return f;
    }

    [Fact]
    public void BuildCommand_PrefixesReportIdAndMagic()
    {
        var report = RayNeoProtocol.BuildCommand(RayNeoProtocol.CmdImuOn);
        Assert.Equal(65, report.Length);
        Assert.Equal(new byte[] { 0x00, 0x66, 0x01, 0x00 }, report[..4]);
        Assert.All(report[4..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void GetFrame_StripsWindowsReportId()
    {
        var frame = ImuFrame(new[] { 0f, 9.80665f, 0f }, new[] { 0f, 0f, 0f }, 1);
        var withId = new byte[65];
        frame.CopyTo(withId, 1);
        Assert.Equal(frame, RayNeoProtocol.GetFrame(withId).ToArray());
        Assert.Equal(frame, RayNeoProtocol.GetFrame(frame).ToArray());
        Assert.True(RayNeoProtocol.GetFrame(new byte[65]).IsEmpty);
    }

    [Fact]
    public void TryDecodeImu_ConvertsUnits()
    {
        var frame = ImuFrame(new[] { 0f, 9.80665f, 0f }, new[] { 90f, 0f, -180f }, 12345);
        Assert.True(RayNeoProtocol.TryDecodeImu(frame, out var s));
        Assert.Equal(1f, s.Accel.Y, 4);
        Assert.Equal(MathF.PI / 2, s.Gyro.X, 4);
        Assert.Equal(-MathF.PI, s.Gyro.Z, 4);
        Assert.Equal(36.5f, s.Temperature, 3);
        Assert.Equal(12345u, s.Tick);
        Assert.Equal(new System.Numerics.Vector3(20, -10, 30), s.Magnetometer);
    }

    [Fact]
    public void TryDecodeImu_PlaceholderMagnetometerIsNull()
    {
        var frame = ImuFrame(new[] { 0f, 9.8f, 0f }, new[] { 0f, 0f, 0f }, 1, mx: -3200, my: 3200);
        Assert.True(RayNeoProtocol.TryDecodeImu(frame, out var s));
        Assert.Null(s.Magnetometer);
    }

    [Fact]
    public void TryDecodeDeviceInfo_ReadsBoardAndDate()
    {
        var f = new byte[64];
        f[0] = 0x99;
        f[1] = 0xC8;
        f[4 + 12] = 0x3A;
        f[4 + 13] = 1;
        System.Text.Encoding.ASCII.GetBytes("Jan 10 2026").CopyTo(f, 4 + 15);
        Assert.True(RayNeoProtocol.TryDecodeDeviceInfo(f, out var info));
        Assert.Equal(0x3A, info.BoardId);
        Assert.True(info.SensorOn);
        Assert.Equal("Jan 10 2026", info.FirmwareDate);
    }
}

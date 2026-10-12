using System.Numerics;
using GlassesCore;
using Xunit;

namespace GlassesCore.Tests;

public class OrientationFilterTests
{
    private const uint TicksPerSample = 20; // 500 Hz at 10 kHz ticks

    private static uint Feed(OrientationFilter f, uint tick, int count, Vector3 gyroRad, Vector3? accel = null)
    {
        for (var i = 0; i < count; i++)
        {
            tick += TicksPerSample;
            f.Update(new ImuSample(accel ?? Vector3.UnitY, gyroRad, null, 30, tick));
        }
        return tick;
    }

    [Theory]
    [InlineData(0, 1, 0, 45, 0, 0)]   // spin about +Y (up) = turn left
    [InlineData(1, 0, 0, 0, 45, 0)]   // spin about +X (right) = look up
    public void IntegratesGyro(float x, float y, float z, float yaw, float pitch, float roll)
    {
        var f = new OrientationFilter();
        var tick = Feed(f, 0, 1000, Vector3.Zero); // 2 s still
        // 45° over 0.5 s, with gravity left untouched so tilt correction stays out of the way for yaw
        Feed(f, tick, 250, new Vector3(x, y, z) * (MathF.PI / 4 / 0.5f), accel: Vector3.Zero);
        var e = f.GetEulerDegrees();
        Assert.Equal(yaw, e.Yaw, 0.5f);
        Assert.Equal(pitch, e.Pitch, 0.5f);
        Assert.Equal(roll, e.Roll, 0.5f);
    }

    [Fact]
    public void LearnsGyroBiasWhileStill()
    {
        var f = new OrientationFilter();
        var bias = new Vector3(0.01f, -0.02f, 0.005f);
        Feed(f, 0, 2500, bias); // 5 s still with a biased gyro
        Assert.True((f.GyroBias - bias).Length() < 0.001f);
        Assert.InRange(MathF.Abs(f.GetEulerDegrees().Yaw), 0, 1.5f);
    }

    [Fact]
    public void TiltSettlesToGravity()
    {
        var f = new OrientationFilter();
        var tick = Feed(f, 0, 10, Vector3.Zero);
        // Head pitched up 30°: world "up" appears tilted toward body -Z (forward).
        var a = MathF.PI / 6;
        Feed(f, tick, 5000, Vector3.Zero, accel: new Vector3(0, MathF.Cos(a), -MathF.Sin(a)));
        Assert.Equal(30, f.GetEulerDegrees().Pitch, 1f);
    }

    [Fact]
    public void RecenterZeroesYaw()
    {
        var f = new OrientationFilter();
        var tick = Feed(f, 0, 1000, Vector3.Zero);
        Feed(f, tick, 250, new Vector3(0, MathF.PI / 2, 0), accel: Vector3.Zero);
        Assert.Equal(45, f.GetEulerDegrees().Yaw, 0.5f);
        f.Recenter();
        Assert.Equal(0, f.GetEulerDegrees().Yaw, 0.01f);
    }
}

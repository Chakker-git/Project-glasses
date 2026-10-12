using System.Numerics;
using GlassesCore;
using Xunit;

namespace GlassesCore.Tests;

public class MountCalibrationTests
{
    [Fact]
    public void LevelHeadWithTiltedSensorGivesLevelCamera()
    {
        // Real reading from an Air 4 Pro worn level: gravity tilted toward body +Z.
        var accel = new Vector3(0.03f, 0.96f, 0.29f);
        var mount = MountCalibration.FromLevelHead(accel);
        Assert.Equal(16.8f, mount.PitchDegrees, 0.5f);
        Assert.Equal(-1.7f, mount.RollDegrees, 0.5f);

        // Filter settles with world up = sensor's measured up; camera = q * mount must then be level.
        var f = new OrientationFilter();
        uint tick = 0;
        for (var i = 0; i < 3000; i++) { tick += 20; f.Update(new ImuSample(accel / accel.Length(), Vector3.Zero, null, 30, tick)); }
        var camera = f.Orientation * mount.Rotation;
        var cameraUp = Vector3.Transform(Vector3.UnitY, camera);
        var cameraForward = Vector3.Transform(-Vector3.UnitZ, camera);
        Assert.Equal(1, cameraUp.Y, 3);         // no roll, no pitch
        Assert.Equal(0, cameraForward.Y, 3);    // looking at the horizon
    }

    [Fact]
    public void ZeroCalibrationIsIdentity()
    {
        Assert.Equal(Quaternion.Identity, new MountCalibration(0, 0).Rotation);
    }
}

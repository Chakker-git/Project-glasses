using System.Numerics;
using GlassesCore;
using Xunit;

namespace GlassesCore.Tests;

public class PoseStabilizerTests
{
    private static float YawDeg(Quaternion q)
    {
        var f = Vector3.Transform(-Vector3.UnitZ, q);
        return MathF.Atan2(-f.X, -f.Z) * 180f / MathF.PI;
    }

    [Fact]
    public void DampsJitterWhenStill()
    {
        var s = new PoseStabilizer();
        var rng = new Random(2);
        var inputs = new List<float>();
        var outputs = new List<float>();
        for (var i = 0; i < 600; i++)
        {
            var jitter = ((float)rng.NextDouble() - 0.5f) * 0.04f; // ±0.02°, about one glasses pixel
            var o = s.Update(Quaternion.CreateFromAxisAngle(Vector3.UnitY, jitter * MathF.PI / 180f), 1 / 60f);
            if (i > 60) { inputs.Add(jitter); outputs.Add(YawDeg(o)); }
        }
        static double Rms(List<float> v) => Math.Sqrt(v.Average(x => (double)x * x));
        Assert.True(Rms(outputs) < Rms(inputs) * 0.4, $"out {Rms(outputs)} vs in {Rms(inputs)}");
    }

    [Fact]
    public void TracksRealTurnsWithoutLag()
    {
        var s = new PoseStabilizer();
        Quaternion o = Quaternion.Identity;
        for (var i = 0; i <= 60; i++) // 60 °/s turn for one second
            o = s.Update(Quaternion.CreateFromAxisAngle(Vector3.UnitY, i * MathF.PI / 180f), 1 / 60f);
        Assert.Equal(60, YawDeg(o), 0.1f);
    }
}

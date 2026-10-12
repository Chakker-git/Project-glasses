using System.Numerics;
using GlassesCore;
using Xunit;

namespace GlassesCore.Tests;

public class PanelLayoutTests
{
    [Fact]
    public void OrdersByDesktopPositionAndCentresTheRow()
    {
        var sources = new[]
        {
            new PanelSource(1920, 0, 1920, 1080),   // middle
            new PanelSource(-1920, 0, 1920, 1080),  // left
            new PanelSource(3840, 0, 1920, 1080),   // right
        };
        var layout = PanelLayout.Arrange(sources, panelWidthDegrees: 40, gapDegrees: 4);

        Assert.Equal(new[] { -1920, 1920, 3840 }, layout.Select(l => l.Source.Left));
        Assert.Equal(44, layout[0].Placement.YawDegrees, 3);   // left panel is at + yaw
        Assert.Equal(0, layout[1].Placement.YawDegrees, 3);
        Assert.Equal(-44, layout[2].Placement.YawDegrees, 3);
    }

    [Fact]
    public void CenterIndexPutsThatPanelStraightAhead()
    {
        var sources = new[] { new PanelSource(0, 0, 1920, 1080), new PanelSource(1920, 0, 1920, 1080) };
        var layout = PanelLayout.Arrange(sources, 40, 4, centerIndex: 0);
        Assert.Equal(0, layout[0].Placement.YawDegrees, 3);
        Assert.Equal(-44, layout[1].Placement.YawDegrees, 3);
    }

    [Fact]
    public void TallerMonitorKeepsHeightAndNarrows()
    {
        var layout = PanelLayout.Arrange(new[] { new PanelSource(0, 0, 1920, 1200) }, 40, 4);
        Assert.Equal(22.5f * 1920 / 1200, layout[0].Placement.WidthDegrees, 3);
    }

    [Fact]
    public void WorldMatrixPlacesPanelAtItsYaw()
    {
        var m = PanelLayout.WorldMatrix(new PanelPlacement(90, 40, 16 / 9f), 2);
        var centre = Vector3.Transform(Vector3.Zero, m);
        Assert.Equal(-2, centre.X, 3); // +90° yaw = to the viewer's left (-X)
        Assert.Equal(0, centre.Z, 3);
        var rightEdge = Vector3.Transform(Vector3.UnitX, m);
        var angle = MathF.Atan2(rightEdge.Z - centre.Z, 2) * 180 / MathF.PI; // edge is toward the front
        Assert.Equal(-20, angle, 2);
    }

    [Fact]
    public void PredictionExtrapolatesTurn()
    {
        var f = new OrientationFilter();
        uint tick = 0;
        for (var i = 0; i < 600; i++) { tick += 20; f.Update(new ImuSample(Vector3.UnitY, new Vector3(0, 1, 0), null, 30, tick)); }
        var now = Vector3.Transform(-Vector3.UnitZ, f.Orientation);
        var ahead = Vector3.Transform(-Vector3.UnitZ, f.PredictOrientation(0.1f));
        var deltaDeg = (MathF.Atan2(-ahead.X, -ahead.Z) - MathF.Atan2(-now.X, -now.Z)) * 180 / MathF.PI;
        Assert.Equal(5.73f, deltaDeg, 1); // 1 rad/s * 0.1 s
    }
}

using System.Numerics;

namespace GlassesCore;

/// <summary>A monitor to show as a virtual panel: its pixel size and its desktop position.</summary>
public readonly record struct PanelSource(int Left, int Top, int Width, int Height);

/// <summary>Where a panel sits around the viewer: yaw of its centre (+ = left) and its angular width.</summary>
public readonly record struct PanelPlacement(float YawDegrees, float WidthDegrees, float Aspect);

/// <summary>
/// Lays monitors out left-to-right on a ring around the viewer in the same order as the Windows
/// desktop, so dragging a window off the right edge of one panel lands it on the panel to the right.
/// </summary>
public static class PanelLayout
{
    /// <param name="sources">Monitors to show.</param>
    /// <param name="panelWidthDegrees">Angular width of a 16:9-or-wider panel; narrower ones keep their height.</param>
    /// <param name="gapDegrees">Space between neighbouring panels.</param>
    /// <param name="centerIndex">Which panel (in left-to-right order) sits straight ahead; null centres the row.</param>
    public static IReadOnlyList<(PanelSource Source, PanelPlacement Placement)> Arrange(
        IEnumerable<PanelSource> sources, float panelWidthDegrees, float gapDegrees, int? centerIndex = null)
    {
        var ordered = sources.OrderBy(s => s.Left).ThenBy(s => s.Top).ToList();
        if (ordered.Count == 0) return Array.Empty<(PanelSource, PanelPlacement)>();

        // Same angular height for every panel; width follows each monitor's aspect ratio.
        var height = panelWidthDegrees * 9f / 16f;
        var widths = ordered.Select(s => height * s.Width / (float)s.Height).ToList();

        // Running position of each panel's centre, measured rightward from the first panel's left edge.
        var centers = new float[ordered.Count];
        var x = 0f;
        for (var i = 0; i < ordered.Count; i++)
        {
            centers[i] = x + widths[i] / 2;
            x += widths[i] + gapDegrees;
        }
        var total = x - gapDegrees;
        var origin = centerIndex is int c && c >= 0 && c < ordered.Count ? centers[c] : total / 2;

        // Rightward distance becomes negative yaw (yaw + = left).
        return ordered
            .Select((s, i) => (s, new PanelPlacement(origin - centers[i], widths[i], s.Width / (float)s.Height)))
            .ToList();
    }

    /// <summary>
    /// World matrix (row-vector convention) for a unit quad spanning [-1,1]² placed at
    /// <paramref name="distance"/> in the panel's direction, facing the viewer. The whole ring can be
    /// turned by <paramref name="yawOffsetDegrees"/> and raised or lowered by <paramref name="pitchDegrees"/>
    /// (each panel keeps facing the viewer and stays level).
    /// </summary>
    public static Matrix4x4 WorldMatrix(PanelPlacement p, float distance, float pitchDegrees = 0, float yawOffsetDegrees = 0)
    {
        const float toRad = MathF.PI / 180f;
        var halfWidth = distance * MathF.Tan(p.WidthDegrees * MathF.PI / 360f);
        var halfHeight = halfWidth / p.Aspect;
        return Matrix4x4.CreateScale(halfWidth, halfHeight, 1)
               * Matrix4x4.CreateTranslation(0, 0, -distance)
               * Matrix4x4.CreateRotationX(pitchDegrees * toRad)
               * Matrix4x4.CreateRotationY((p.YawDegrees + yawOffsetDegrees) * toRad);
    }
}

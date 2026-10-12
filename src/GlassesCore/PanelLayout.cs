using System.Numerics;

namespace GlassesCore;

/// <summary>A monitor to show as a virtual panel: its pixel size and its desktop position.</summary>
public readonly record struct PanelSource(int Left, int Top, int Width, int Height);

/// <summary>
/// Where a panel sits around the viewer: yaw (+ = left) and pitch (+ = up) of its centre, its angular
/// width, and its aspect ratio.
/// </summary>
public readonly record struct PanelPlacement(float YawDegrees, float WidthDegrees, float Aspect, float PitchDegrees = 0);

/// <summary>
/// Places monitors around the viewer the way they're arranged in Windows Display Settings: a monitor
/// to the right of another appears to the right, one above appears above. Dragging a window off one
/// panel's edge therefore lands it on the neighbouring panel. Sizes are proportional to pixels, so
/// every panel has the same text size and a portrait monitor is tall.
/// </summary>
public static class PanelLayout
{
    /// <param name="sources">Monitors to show.</param>
    /// <param name="reference">The monitor that sits straight ahead.</param>
    /// <param name="referenceWidthDegrees">Angular width of the reference monitor; sets degrees per pixel.</param>
    /// <param name="gapDegrees">Space between monitors that touch on the desktop.</param>
    public static IReadOnlyList<(PanelSource Source, PanelPlacement Placement)> Arrange(
        IEnumerable<PanelSource> sources, PanelSource reference, float referenceWidthDegrees, float gapDegrees)
    {
        var list = sources.OrderBy(s => s.Left).ThenBy(s => s.Top).ToList();
        if (list.Count == 0 || reference.Width <= 0) return Array.Empty<(PanelSource, PanelPlacement)>();

        var degreesPerPixel = referenceWidthDegrees / reference.Width;
        // Spread positions so neighbours that touch on the desktop get a gap between them.
        var spacing = (referenceWidthDegrees + gapDegrees) / referenceWidthDegrees;
        var refCx = reference.Left + reference.Width / 2f;
        var refCy = reference.Top + reference.Height / 2f;

        return list.Select(s =>
        {
            var dx = s.Left + s.Width / 2f - refCx;
            var dy = s.Top + s.Height / 2f - refCy;
            return (s, new PanelPlacement(
                YawDegrees: -dx * degreesPerPixel * spacing,   // right on the desktop = negative yaw
                WidthDegrees: s.Width * degreesPerPixel,
                Aspect: s.Width / (float)s.Height,
                PitchDegrees: -dy * degreesPerPixel * spacing)); // desktop Y grows downward
        }).ToList();
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
               * Matrix4x4.CreateRotationX((pitchDegrees + p.PitchDegrees) * toRad)
               * Matrix4x4.CreateRotationY((p.YawDegrees + yawOffsetDegrees) * toRad);
    }
}

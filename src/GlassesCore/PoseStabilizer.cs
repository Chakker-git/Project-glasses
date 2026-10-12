using System.Numerics;

namespace GlassesCore;

/// <summary>
/// Removes sub-pixel jitter while the head is nearly still. One glasses pixel is ~0.02°, so sensor
/// noise far below what you can feel still makes text shimmer. Below <see cref="StillDegreesPerSecond"/>
/// the rendered pose is low-passed; as the head speeds up the smoothing fades out so real turns
/// get no added lag.
/// </summary>
public sealed class PoseStabilizer
{
    public float StillDegreesPerSecond { get; set; } = 3f;
    public float StillTimeConstant { get; set; } = 0.12f;

    private Quaternion? _output;
    private Quaternion? _lastInput;
    private Vector3 _velocity; // smoothed angular velocity, °/s; random jitter averages out, real turns don't

    public Quaternion Update(Quaternion target, float dt)
    {
        if (_output is not Quaternion output || _lastInput is not Quaternion last || dt <= 0)
        {
            _output = _lastInput = target;
            return target;
        }
        _lastInput = target;

        var step = Quaternion.Normalize(target * Quaternion.Conjugate(last));
        if (step.W < 0) step = -step;
        var angle = 2 * MathF.Acos(Math.Clamp(step.W, -1, 1)) * 180f / MathF.PI;
        var axis = new Vector3(step.X, step.Y, step.Z);
        var instant = axis.LengthSquared() > 1e-12f ? Vector3.Normalize(axis) * (angle / dt) : Vector3.Zero;
        _velocity = Vector3.Lerp(_velocity, instant, 1 - MathF.Exp(-dt / 0.05f));
        var speed = _velocity.Length();
        // Full smoothing below the threshold, none at twice the threshold and above.
        var motion = Math.Clamp((speed - StillDegreesPerSecond) / StillDegreesPerSecond, 0, 1);
        var tau = StillTimeConstant * (1 - motion);
        var alpha = tau <= 1e-4f ? 1 : 1 - MathF.Exp(-dt / tau);
        output = Quaternion.Normalize(Quaternion.Slerp(output, target, alpha));
        _output = output;
        return output;
    }

    public void Reset()
    {
        _output = _lastInput = null;
        _velocity = Vector3.Zero;
    }
}

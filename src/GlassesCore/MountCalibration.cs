using System.Numerics;

namespace GlassesCore;

/// <summary>
/// The IMU sits in the glasses' frame at an angle to the display (on the Air 4 Pro, ~17° nose-down).
/// This is the fixed rotation from the display's frame to the IMU's body frame, so the virtual
/// camera can follow the eyes rather than the sensor: <c>camera = imuOrientation * Rotation</c>.
/// </summary>
/// <param name="PitchDegrees">Sensor tilt about the display's X (right) axis.</param>
/// <param name="RollDegrees">Sensor tilt about the display's Z (back) axis.</param>
public readonly record struct MountCalibration(float PitchDegrees, float RollDegrees)
{
    private const float ToRad = MathF.PI / 180f;

    /// <summary>Rotation taking display-frame vectors to IMU body-frame vectors (roll applied first, then pitch).</summary>
    public Quaternion Rotation =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitX, PitchDegrees * ToRad) *
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, RollDegrees * ToRad);

    /// <summary>
    /// Calibrates from the accelerometer while the wearer holds their head level and looks straight
    /// ahead: whatever direction gravity's "up" has in the body frame is the display's up.
    /// </summary>
    public static MountCalibration FromLevelHead(Vector3 accelBody)
    {
        var up = Vector3.Normalize(accelBody);
        // Rotation applied to display +Y = (-sin r, cos r cos p, cos r sin p).
        var roll = -MathF.Asin(Math.Clamp(up.X, -1, 1));
        var pitch = MathF.Atan2(up.Z, up.Y);
        return new MountCalibration(pitch / ToRad, roll / ToRad);
    }
}

using System.Numerics;

namespace GlassesCore;

/// <summary>
/// Complementary filter for head orientation: integrates the gyro, pulls tilt toward the
/// accelerometer's "up", and learns gyro bias from stretches where the head is still.
/// Yaw (left/right) has no absolute reference yet, so it drifts slowly; <see cref="Recenter"/> fixes that.
/// </summary>
/// <remarks>
/// Frames: X right, Y up, Z back (forward is -Z), for both the glasses body and the world.
/// <see cref="Orientation"/> rotates body vectors into world space.
/// </remarks>
public sealed class OrientationFilter
{
    // How hard the accelerometer pulls tilt back (rad/s per rad of error).
    private const float TiltGain = 0.5f;
    // Only trust the accelerometer when it reads close to 1g and the head isn't spinning fast.
    private const float MaxAccelDeviation = 0.1f;
    private const float MaxTurnRateForTilt = 1.5f;
    // Gyro bias is learned from windows where the head is genuinely still. Measured on an
    // Air 4 Pro: true bias ~0.05 °/s, noise ~0.5 °/s per axis. Slow head sway also has low
    // noise, so a window's mean must also sit close to the current bias, and learning is slow.
    private const double BiasWindowSeconds = 0.5;
    private const float StillGyroStd = 0.9f * DegToRad;      // per-axis std within a window
    private const float StillAccelStd = 0.01f;               // g
    private const float StillAccelDeviation = 0.05f;         // g, |norm - 1|
    private const float MaxBiasStep = 1.0f * DegToRad;       // ignore windows further than this from the bias
    private const float BiasTimeConstant = 20f;              // seconds
    private const double SensorWarmupSeconds = 1.0;          // gyro reads off for ~1 s after IMU on
    private const int BiasAveragingWindows = 10;             // plain average of the first N still windows
    private const float DegToRad = MathF.PI / 180f;

    private readonly object _lock = new();
    private Quaternion _q = Quaternion.Identity;
    private Vector3 _bias;
    private bool _initialized;
    private uint? _lastTick;
    private BiasWindow _window;
    private int _biasWindows;
    private double _runSeconds;
    private float _yawOffset;

    public long SampleCount { get; private set; }

    public bool IsReady { get { lock (_lock) return _initialized && _runSeconds > 1.0; } }

    public Vector3 GyroBias { get { lock (_lock) return _bias; } }

    /// <summary>Head orientation with the recenter offset applied.</summary>
    public Quaternion Orientation
    {
        get { lock (_lock) return Quaternion.CreateFromAxisAngle(Vector3.UnitY, -_yawOffset) * _q; }
    }

    public void Update(in ImuSample s)
    {
        lock (_lock)
        {
            SampleCount++;
            var last = _lastTick;
            _lastTick = s.Tick;
            if (last is null) return;
            var dt = unchecked(s.Tick - last.Value) / 10_000f;
            if (dt <= 0 || dt > 0.1f) return;
            _runSeconds += dt;

            var accelNorm = s.Accel.Length();
            if (!_initialized)
            {
                if (accelNorm < 0.1f) return;
                _q = FromTo(s.Accel / accelNorm, Vector3.UnitY);
                _initialized = true;
                return;
            }

            LearnBias(s, accelNorm, dt);
            var w = s.Gyro - _bias;

            if (MathF.Abs(accelNorm - 1) < MaxAccelDeviation && w.Length() < MaxTurnRateForTilt)
            {
                var measuredUp = s.Accel / accelNorm;
                var estimatedUp = Vector3.Transform(Vector3.UnitY, Quaternion.Conjugate(_q));
                w += TiltGain * Vector3.Cross(measuredUp, estimatedUp);
            }

            var rate = w.Length();
            if (rate > 1e-9f)
                _q = Quaternion.Normalize(_q * Quaternion.CreateFromAxisAngle(w / rate, rate * dt));
        }
    }

    private void LearnBias(in ImuSample s, float accelNorm, float dt)
    {
        if (_runSeconds < SensorWarmupSeconds) return;
        _window.Add(s.Gyro, accelNorm, dt);
        if (_window.Seconds < BiasWindowSeconds) return;

        var (mean, std) = _window.Gyro();
        var (accelMean, accelStd) = _window.Accel();
        _window = default;

        var still = MathF.Max(std.X, MathF.Max(std.Y, std.Z)) < StillGyroStd
                    && accelStd < StillAccelStd
                    && MathF.Abs(accelMean - 1) < StillAccelDeviation;
        if (!still) return;

        if (_biasWindows > 0 && (mean - _bias).Length() > MaxBiasStep) return; // slow, steady head turn

        if (_biasWindows < BiasAveragingWindows)
        {
            // Plain running average until we have a few seconds of stillness.
            _biasWindows++;
            _bias += (mean - _bias) / _biasWindows;
        }
        else
        {
            _bias += (mean - _bias) * (float)(BiasWindowSeconds / BiasTimeConstant);
        }
    }

    private struct BiasWindow
    {
        private Vector3 _sum, _sumSq;
        private double _accelSum, _accelSumSq;
        private int _count;
        public double Seconds { get; private set; }

        public void Add(Vector3 gyro, float accelNorm, float dt)
        {
            _sum += gyro;
            _sumSq += gyro * gyro;
            _accelSum += accelNorm;
            _accelSumSq += (double)accelNorm * accelNorm;
            _count++;
            Seconds += dt;
        }

        public (Vector3 Mean, Vector3 Std) Gyro()
        {
            var mean = _sum / _count;
            var variance = Vector3.Max(_sumSq / _count - mean * mean, Vector3.Zero);
            return (mean, Vector3.SquareRoot(variance));
        }

        public (float Mean, float Std) Accel()
        {
            var mean = _accelSum / _count;
            return ((float)mean, (float)Math.Sqrt(Math.Max(0, _accelSumSq / _count - mean * mean)));
        }
    }

    /// <summary>Makes the current facing direction "straight ahead".</summary>
    public void Recenter()
    {
        lock (_lock) _yawOffset = YawOf(_q);
    }

    /// <summary>Yaw (+ = turned left), pitch (+ = looking up), roll (+ = right ear down), in degrees.</summary>
    public (float Yaw, float Pitch, float Roll) GetEulerDegrees()
    {
        var q = Orientation;
        var forward = Vector3.Transform(-Vector3.UnitZ, q);
        var right = Vector3.Transform(Vector3.UnitX, q);
        const float toDeg = 180f / MathF.PI;
        return (YawOf(q) * toDeg,
                MathF.Asin(Math.Clamp(forward.Y, -1, 1)) * toDeg,
                -MathF.Asin(Math.Clamp(right.Y, -1, 1)) * toDeg);
    }

    private static float YawOf(Quaternion q)
    {
        var forward = Vector3.Transform(-Vector3.UnitZ, q);
        return MathF.Atan2(-forward.X, -forward.Z);
    }

    /// <summary>Shortest rotation taking unit vector <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        var d = Vector3.Dot(from, to);
        if (d < -0.9999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, from);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitZ, from);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(c, 1 + d));
    }
}

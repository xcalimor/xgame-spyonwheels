using System;

namespace SpyOnWheels;

/// <summary>
/// **The camera's zoom from the car's speed (WI-017, the vision: "zoom out a bit more on high speed than low speed … to
/// give the player a better view when in high speed").** The view widens in step with speed, twice as wide at top
/// speed (D1), and the zoom eases toward that, never jumps (D2), so a crash or a hard brake does not snap the picture.
/// Plain simulation: reads the car's speed, writes nothing back (the vision's Camera reads Vehicle Physics only).
/// </summary>
public sealed class SpeedCamera
{
    /// <summary>The zoom standing still: the skeleton's 0.5 (a 1280 x 720 window shows 40 x 22.5 m).</summary>
    public const float NearZoom = 0.5f;

    /// <summary>How much wider the view is at top speed than standing still (2: 80 x 45 m).</summary>
    public const float WidenAtTopSpeed = 2f;

    /// <summary>How quickly the zoom follows the speed, seconds (the time to close ~63% of the gap).</summary>
    public const double EaseSeconds = 0.2;   // WI-017 his drives: "a bit faster zoom" (0.6 → 0.35), "still a bit slow" (→ 0.2)

    private readonly double _topSpeedMs;

    public SpeedCamera(double topSpeedMs) => _topSpeedMs = topSpeedMs;

    /// <summary>The zoom now.</summary>
    public float Zoom { get; private set; } = NearZoom;

    /// <summary>The zoom the camera eases toward at <paramref name="speedMs"/> (either direction).</summary>
    public float TargetAt(double speedMs)
    {
        double share = Math.Clamp(Math.Abs(speedMs) / _topSpeedMs, 0, 1);
        return NearZoom / (float)(1 + (WidenAtTopSpeed - 1) * share);
    }

    /// <summary>One step of time: the zoom moves part of the way toward the speed's target.</summary>
    public void Update(double speedMs, double dt)
    {
        if (dt <= 0) return;
        float target = TargetAt(speedMs);
        Zoom += (target - Zoom) * (float)(1 - Math.Exp(-dt / EaseSeconds));
    }
}

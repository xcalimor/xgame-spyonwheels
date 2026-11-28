using System;

namespace SpyOnWheels;

/// <summary>
/// **One-finger steering (WI-030, his ask: "we only have four directions to steer with; on a phone a virtual direction
/// stick").** Left and right are on or off; the wheel follows them over a short moment instead of jumping, so a tap is a
/// small correction and a hold is a full turn. Letting go or steering the other way brings the wheel back faster than it
/// went out, so catching a slide is quick.
/// </summary>
public sealed class SteerInput
{
    public const double RiseSeconds = 0.15;     // from straight to full lock, held
    public const double ReturnSeconds = 0.06;   // from full lock back to straight, let go (or reversed)

    /// <summary>The wheel now, -1 (full left) to 1 (full right).</summary>
    public float Value { get; private set; }

    /// <summary>Follow <paramref name="wanted"/> (-1, 0 or 1 from the keys or the stick) for <paramref name="dt"/>.</summary>
    public float Update(float wanted, double dt)
    {
        wanted = Math.Clamp(wanted, -1f, 1f);
        float v = Value;
        // Back toward straight first when let go or reversed; then out toward the wanted side.
        if (v != 0 && (wanted == 0 || Math.Sign(wanted) != Math.Sign(v)))
        {
            float back = (float)(dt / ReturnSeconds);
            v = Math.Abs(v) <= back ? 0 : v - Math.Sign(v) * back;
        }
        else if (wanted != 0)
        {
            float rise = (float)(dt / RiseSeconds);
            v = Math.Abs(wanted - v) <= rise ? wanted : v + Math.Sign(wanted - v) * rise;
        }
        Value = v;
        return v;
    }

    public void Reset() => Value = 0;
}

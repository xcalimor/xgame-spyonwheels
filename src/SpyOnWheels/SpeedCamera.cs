using System;
using Microsoft.Xna.Framework;

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

    /// <summary>
    /// **The lead (WI-017 D4, his ruling 2026-11-20: "the camera should focus in high speed more on what's in front"):**
    /// at top speed the camera sits this share of the half-view ahead of the car, the way it travels, so the car is
    /// halfway between the centre and the trailing edge. A share of the view on each axis, so it stays on screen at any
    /// zoom and shows more ahead on the wide axis.
    /// </summary>
    public const float LeadAtTopSpeed = 0.5f;

    /// <summary>How quickly the lead follows (D5): a little gentler than the zoom, a moving centre is felt more.</summary>
    public const double LeadEaseSeconds = 0.3;

    /// <summary>Where the camera centres relative to the car now, world px.</summary>
    public Vector2 Lead { get; private set; }

    /// <summary>The lead the camera eases toward: <paramref name="forward"/> is the car's facing (unit); backing up leads
    /// backward (D6). <paramref name="viewPx"/> is the window, screen px.</summary>
    public Vector2 LeadTargetAt(double speedMs, Vector2 forward, Vector2 viewPx, float zoom)
    {
        float share = (float)Math.Clamp(Math.Abs(speedMs) / _topSpeedMs, 0, 1);
        var travel = forward * MathF.Sign((float)speedMs);
        var halfView = viewPx / 2f / zoom;
        return travel * halfView * share * LeadAtTopSpeed;
    }

    /// <summary>One step of time: the zoom and the lead move part of the way toward the speed's targets.</summary>
    public void Update(double speedMs, double dt, Vector2 forward = default, Vector2 viewPx = default)
    {
        if (dt <= 0) return;
        float target = TargetAt(speedMs);
        Zoom += (target - Zoom) * (float)(1 - Math.Exp(-dt / EaseSeconds));
        if (viewPx == Vector2.Zero) viewPx = new Vector2(1280, 720);
        var lead = LeadTargetAt(speedMs, forward, viewPx, Zoom);
        Lead += (lead - Lead) * (float)(1 - Math.Exp(-dt / LeadEaseSeconds));
    }
}

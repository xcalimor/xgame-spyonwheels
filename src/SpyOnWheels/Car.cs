using System;
using Microsoft.Xna.Framework;

namespace SpyOnWheels;

/// <summary>What the driver asks of the car this step (the vision's four arrows).</summary>
public readonly record struct Drive(bool Throttle, bool Brake, float Steer);

/// <summary>
/// **The car as plain simulation (WI-015 D4)**, no window, no world: Up accelerates to a top speed, Down brakes (and,
/// once stopped, backs up slowly), Left/Right turn the car, quickly when slow and less as speed grows, as a real car
/// does; a car standing still does not turn. World pixels and seconds; the tuning is in metres and read at the world's
/// scale, so a car is the same car in any world.
/// </summary>
/// <summary>
/// **What a surface does to the tyres (WI-019, his ask: "turning gets harder because tyres get better grip on asphalt than
/// on grass").** <see cref="Grip"/> scales the grip (1 = asphalt); <see cref="DragPerS"/> holds the car back in proportion to
/// its speed, so flat out it settles where the engine and the drag balance. <see cref="MaxDriftDeg"/> is how far the slide
/// may swing from the nose: under power at speed a turn always reaches it, so it is what makes a power turn on asphalt
/// differ from one on grass (his drive: "too small a difference between grass and asphalt").
/// </summary>
public readonly record struct Traction(double Grip, double DragPerS, double MaxDriftDeg)
{
    /// <summary>The road and the bridge decks: the tuning as WI-018 left it.</summary>
    public static readonly Traction Asphalt = new(1.6, 0, 18);   // his drive: "make it feel like the tyres suck onto the road" (grip 1 → 1.6, drift 30° → 18°)

    /// <summary>Everything off the road (D1, D2): 60% of the grip, and flat out it holds ~130 km/h.</summary>
    public static readonly Traction OffRoad = new(0.6, Car.AccelerationMs2 / OffRoadTopSpeedMs, 35);   // loose: the slide swings out to 35°

    /// <summary>Flat out off the road, m/s (~130 km/h).</summary>
    public const double OffRoadTopSpeedMs = 36;
}

public sealed class Car
{
    // The tuning: a first guess for the skeleton's feel, to be felt and changed.
    public const double TopSpeedMs = 46.0;        // ~165 km/h (WI-017 his drive: "the speed could be a bit higher"; was 38)
    public const double ReverseSpeedMs = 6.0;
    public const double AccelerationMs2 = 16.0;    // WI-018 D3 "an aggressive sports car": 0-165 km/h in ~3 s (was 9)
    public const double BrakeMs2 = 50.0;           // WI-018 D3 "more aggressive braking" (22 → 30), his drive "much harder brakes" (→ 50)
    public const double RollDragMs2 = 2.5;         // coasting slows the car
    public const double TurnSlowRad = 2.6;         // turn rate at low speed, rad/s
    public const double TurnFastRad = 0.9;         // turn rate at top speed
    public const double PowerTurnBoost = 1.33;     // WI-018 his drive "drift circle a bit too wide": under power the rear steps out and the nose turns this much faster (in a drift the nose sets the circle)
    public const double TurnFullFromMs = 4.0;      // below this the turn rate fades to nothing at standstill

    // WI-018 grip (his ask: "if I turn while accelerating the car gets a bit harder to steer"): the car keeps its momentum
    // when the nose turns, and the tyres pull the motion back in line with the nose up to this much sideways, m/s2.
    public const double GripMs2 = 40.0;            // coasting or braking: holds nearly every turn the steering asks for
    public const double GripPowerMs2 = 20.0;       // under throttle while turning: half, so it runs wide and slides (D2)
    public const double GripKeepsSpeed = 0.85;     // the share of the sideways speed the tyres turn into forward speed; the rest is scrubbed

    /// <summary>What the ground under the car does to it (WI-019, set by <see cref="Driving"/> from the world each step).</summary>
    public Traction Ground { get; set; } = Traction.Asphalt;

    private readonly double _pxPerMetre;

    public Car(Vector2 position, float heading, double pxPerMetre)
    {
        Position = position;
        Heading = heading;
        _pxPerMetre = pxPerMetre;
    }

    /// <summary>World px.</summary>
    public Vector2 Position { get; private set; }

    /// <summary>Radians, 0 facing north (up the screen), growing clockwise.</summary>
    public float Heading { get; private set; }

    /// <summary>Metres per second along the heading; negative backing up.</summary>
    public double SpeedMs { get; private set; }

    /// <summary>Metres per second sideways, to the right of the heading (WI-018): the slide. Zero when the tyres hold.</summary>
    public double SlipMs { get; private set; }

    /// <summary>The unit vector the car faces, in world px axes (y down).</summary>
    public Vector2 Forward => new(MathF.Sin(Heading), -MathF.Cos(Heading));

    /// <summary>Drive freely, no world: the intent applied and the car moved by it.</summary>
    public void Step(Drive drive, double dt) => Position += Advance(drive, dt);

    /// <summary>The intent applied to speed and heading; returns where the car WANTS to move this step (world px). The
    /// world decides how much of it happens (<see cref="Driving"/>).</summary>
    public Vector2 Advance(Drive drive, double dt)
    {
        if (dt <= 0) return Vector2.Zero;
        double v = SpeedMs;
        if (drive.Throttle) v = v < 0 ? Math.Min(0, v + BrakeMs2 * dt) : Math.Min(TopSpeedMs, v + AccelerationMs2 * dt);
        else if (drive.Brake) v = v > 0 ? Math.Max(0, v - BrakeMs2 * dt) : Math.Max(-ReverseSpeedMs, v - AccelerationMs2 * 0.5 * dt);
        else v = v > 0 ? Math.Max(0, v - RollDragMs2 * dt) : Math.Min(0, v + RollDragMs2 * dt);
        v -= v * Ground.DragPerS * dt;   // off the road the ground holds the car back (WI-019 D2)
        SpeedMs = v;

        double speed = Math.Abs(v);
        double share = Math.Min(1.0, speed / TopSpeedMs);
        double rate = TurnSlowRad + (TurnFastRad - TurnSlowRad) * share;
        rate *= Math.Min(1.0, speed / TurnFullFromMs);
        bool steering = Math.Abs(drive.Steer) > 0.01f && speed > 0;
        if (drive.Throttle && steering) rate *= PowerTurnBoost;
        TurnTo(WrapAngle(Heading + (float)(Math.Clamp(drive.Steer, -1f, 1f) * rate * dt * Math.Sign(v))));

        // The tyres: pull the slide back toward the nose, up to the grip; most of what they take becomes forward speed.
        double grip = (drive.Throttle && steering ? GripPowerMs2 : GripMs2) * Ground.Grip * dt;
        double take = Math.Min(Math.Abs(SlipMs), grip);
        SlipMs -= Math.Sign(SlipMs) * take;
        // A drift, not a spin: past the largest angle the tyres take the rest too (D2).
        double maxSlip = Math.Tan(MathHelper.ToRadians((float)Ground.MaxDriftDeg)) * Math.Abs(SpeedMs);
        if (Math.Abs(SlipMs) > maxSlip) { take += Math.Abs(SlipMs) - maxSlip; SlipMs = Math.Sign(SlipMs) * maxSlip; }
        if (SpeedMs != 0) SpeedMs = Math.Clamp(SpeedMs + Math.Sign(SpeedMs) * take * GripKeepsSpeed, -ReverseSpeedMs, TopSpeedMs);
        // Nose and slide together never pass the top speed.
        double total = Math.Sqrt(SpeedMs * SpeedMs + SlipMs * SlipMs);
        if (total > TopSpeedMs) { SpeedMs *= TopSpeedMs / total; SlipMs *= TopSpeedMs / total; }
        return Velocity * (float)(_pxPerMetre * dt);
    }

    /// <summary>The height of the surface the car stands on, world px (WI-016).</summary>
    public float HeightPx { get; set; }

    public void MoveTo(Vector2 at) => Position = at;
    /// <summary>The car's motion in the world, m/s (y down): along the nose plus the slide.</summary>
    public Vector2 Velocity => Forward * (float)SpeedMs + Right * (float)SlipMs;

    /// <summary>The unit vector to the car's right.</summary>
    public Vector2 Right => new(MathF.Cos(Heading), MathF.Sin(Heading));

    /// <summary>Turn the nose; the motion keeps its direction in the world (WI-018 D1), so it is split anew along the new nose.</summary>
    public void TurnTo(float heading)
    {
        var velocity = Velocity;
        Heading = heading;
        SpeedMs = Vector2.Dot(velocity, Forward);
        SlipMs = Vector2.Dot(velocity, Right);
    }

    /// <summary>Lose speed: <paramref name="keep"/> is the share kept (0 stops the car).</summary>
    public void Slow(double keep) { SpeedMs *= Math.Clamp(keep, 0, 1); SlipMs *= Math.Clamp(keep, 0, 1); }

    /// <summary>Back to a place, stopped (the water's answer, Q3).</summary>
    public void Reset(Vector2 at, float heading, float heightPx)
    {
        Position = at;
        Heading = heading;
        HeightPx = heightPx;
        SpeedMs = 0;
        SlipMs = 0;
    }

    private static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }
}

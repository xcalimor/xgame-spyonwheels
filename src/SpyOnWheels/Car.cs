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
public sealed class Car
{
    // The tuning: a first guess for the skeleton's feel, to be felt and changed.
    public const double TopSpeedMs = 46.0;        // ~165 km/h (WI-017 his drive: "the speed could be a bit higher"; was 38)
    public const double ReverseSpeedMs = 6.0;
    public const double AccelerationMs2 = 9.0;
    public const double BrakeMs2 = 22.0;
    public const double RollDragMs2 = 2.5;         // coasting slows the car
    public const double TurnSlowRad = 2.6;         // turn rate at low speed, rad/s
    public const double TurnFastRad = 0.9;         // turn rate at top speed
    public const double TurnFullFromMs = 4.0;      // below this the turn rate fades to nothing at standstill

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
        SpeedMs = v;

        double speed = Math.Abs(v);
        double share = Math.Min(1.0, speed / TopSpeedMs);
        double rate = TurnSlowRad + (TurnFastRad - TurnSlowRad) * share;
        rate *= Math.Min(1.0, speed / TurnFullFromMs);
        Heading = WrapAngle(Heading + (float)(Math.Clamp(drive.Steer, -1f, 1f) * rate * dt * Math.Sign(v)));
        return Forward * (float)(v * _pxPerMetre * dt);
    }

    /// <summary>The height of the surface the car stands on, world px (WI-016).</summary>
    public float HeightPx { get; set; }

    public void MoveTo(Vector2 at) => Position = at;
    public void TurnTo(float heading) => Heading = heading;

    /// <summary>Lose speed: <paramref name="keep"/> is the share kept (0 stops the car).</summary>
    public void Slow(double keep) => SpeedMs *= Math.Clamp(keep, 0, 1);

    /// <summary>Back to a place, stopped (the water's answer, Q3).</summary>
    public void Reset(Vector2 at, float heading, float heightPx)
    {
        Position = at;
        Heading = heading;
        HeightPx = heightPx;
        SpeedMs = 0;
    }

    private static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }
}

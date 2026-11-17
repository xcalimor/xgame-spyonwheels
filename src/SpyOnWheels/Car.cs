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
    public const double TopSpeedMs = 38.0;        // ~137 km/h
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

    public void Step(Drive drive, double dt)
    {
        if (dt <= 0) return;
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
        Position += Forward * (float)(v * _pxPerMetre * dt);
    }

    private static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }
}

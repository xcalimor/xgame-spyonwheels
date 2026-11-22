using System;
using Microsoft.Xna.Framework;

namespace SpyOnWheels;

/// <summary>
/// **The target's driver (WI-020 step 1, "trivial behaviour" as the vision's slice asks).** It drives the same car through the
/// same world as the player, with the same four arrows: it aims at a point a little ahead on the route (further at speed),
/// steers toward it, and holds a cruising speed that it lowers before a bend. Pushed off its line, it steers back. While
/// spun out it has no wheel at all (the car ignores it).
/// </summary>
public sealed class Runner
{
    public const double CruiseMs = 32;            // ~115 km/h on the straight: the player's car is faster (46)
    public const double BendSlowdown = 0.45;      // a right-angle bend ahead takes this share off the cruise
    public const double LookMetres = 4, LookSeconds = 0.25;   // a far aim cuts the bends (first run: 23 m ahead cut 24 m bends by ~3 m)
    public const double BendLookMetres = 45;
    public const double FullSteerRad = 0.35;      // this far off its aim it steers full lock
    public const double OffsetEaseMs = 4;         // WI-026: how fast it moves across to pass, sideways m/s

    private readonly Route _route;
    private readonly double _ppm;

    public Runner(Route route, double pxPerMetre, float along, double cruiseMs = CruiseMs)
    {
        _route = route;
        _ppm = pxPerMetre;
        Along = along;
        Cruise = cruiseMs;
    }

    /// <summary>How far along the route the car is, px (tracked near where it was).</summary>
    public float Along { get; private set; }

    /// <summary>Its speed on the straight, m/s (WI-026: traffic cruises slower than the fleeing pickup).</summary>
    public double Cruise { get; }

    /// <summary>How far to the right of its route it drives now, px (WI-026: out of its lane to pass a slower car).</summary>
    public float Offset { get; private set; }

    /// <summary>The route's point it last aimed at (for a check's picture).</summary>
    public Vector2 Aim { get; private set; }

    /// <param name="capMs">WI-026: the most it may go now (a car ahead it must not hit); null: no car ahead.</param>
    /// <param name="offsetPx">WI-026: how far right of its route it wants to drive (negative: left), eased toward.</param>
    public Drive DriveFor(Car car, double? capMs = null, float offsetPx = 0, double dt = 1 / 60.0)
    {
        float m = (float)_ppm;
        Along = _route.Project(car.Position, Along - 20 * m, Along + 60 * m);
        float look = (float)((LookMetres + LookSeconds * Math.Max(0, car.SpeedMs)) * _ppm);
        float step = (float)(OffsetEaseMs * _ppm * dt);
        Offset += Math.Clamp(offsetPx - Offset, -step, step);
        float h = _route.HeadingAt(Along + look);
        Aim = _route.PointAt(Along + look) + new Vector2(MathF.Cos(h), MathF.Sin(h)) * Offset;
        var to = Aim - car.Position;
        float want = MathF.Atan2(to.X, -to.Y);
        float off = MathHelper.WrapAngle(want - car.Heading);
        float steer = Math.Clamp(off / (float)FullSteerRad, -1f, 1f);

        // How much the road turns over the stretch ahead: a bend lowers the speed it wants.
        float here = _route.HeadingAt(Along), turn = 0;
        for (float d = 5; d <= BendLookMetres; d += 5)
            turn = Math.Max(turn, Math.Abs(MathHelper.WrapAngle(_route.HeadingAt(Along + d * m) - here)));
        double target = Cruise * (1 - BendSlowdown * Math.Min(1, turn / MathHelper.PiOver2));
        // WI-022: a car that turns slower than the sports car (the pickup) takes a bend slower in the same share, so it turns
        // as tightly; the straight keeps the cruise.
        if (turn > 0.05f) target *= Math.Min(1, car.Handling.TurnFastRad / Car.TurnFastRad);
        // Far off its aim (pushed round, or after a spin) it slows to get its nose back first.
        if (Math.Abs(off) > 1.0) target = Math.Min(target, 12);
        if (capMs is { } cap) target = Math.Min(target, cap);
        bool throttle = car.SpeedMs < target - 0.5;
        bool brake = car.SpeedMs > target + (capMs is not null && target <= capMs ? 0.5 : 3);   // behind a car: brake at once
        return new Drive(throttle, brake, steer);
    }
}

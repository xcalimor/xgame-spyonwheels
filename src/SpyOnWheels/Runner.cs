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

    private readonly Route _route;
    private readonly double _ppm;

    public Runner(Route route, double pxPerMetre, float along)
    {
        _route = route;
        _ppm = pxPerMetre;
        Along = along;
    }

    /// <summary>How far along the route the car is, px (tracked near where it was).</summary>
    public float Along { get; private set; }

    /// <summary>The route's point it last aimed at (for a check's picture).</summary>
    public Vector2 Aim { get; private set; }

    public Drive DriveFor(Car car)
    {
        float m = (float)_ppm;
        Along = _route.Project(car.Position, Along - 20 * m, Along + 60 * m);
        float look = (float)((LookMetres + LookSeconds * Math.Max(0, car.SpeedMs)) * _ppm);
        Aim = _route.PointAt(Along + look);
        var to = Aim - car.Position;
        float want = MathF.Atan2(to.X, -to.Y);
        float off = MathHelper.WrapAngle(want - car.Heading);
        float steer = Math.Clamp(off / (float)FullSteerRad, -1f, 1f);

        // How much the road turns over the stretch ahead: a bend lowers the speed it wants.
        float here = _route.HeadingAt(Along), turn = 0;
        for (float d = 5; d <= BendLookMetres; d += 5)
            turn = Math.Max(turn, Math.Abs(MathHelper.WrapAngle(_route.HeadingAt(Along + d * m) - here)));
        double target = CruiseMs * (1 - BendSlowdown * Math.Min(1, turn / MathHelper.PiOver2));
        // WI-022: a car that turns slower than the sports car (the pickup) takes a bend slower in the same share, so it turns
        // as tightly; the straight keeps the cruise.
        if (turn > 0.05f) target *= Math.Min(1, car.Handling.TurnFastRad / Car.TurnFastRad);
        // Far off its aim (pushed round, or after a spin) it slows to get its nose back first.
        if (Math.Abs(off) > 1.0) target = Math.Min(target, 12);
        bool throttle = car.SpeedMs < target - 0.5;
        bool brake = car.SpeedMs > target + 3;
        return new Drive(throttle, brake, steer);
    }
}

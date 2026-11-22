using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace SpyOnWheels;

/// <summary>One traffic car's trip as a source gives it: the lane line it drives, where it starts, its box and handling,
/// which picture it wears (<see cref="Look"/>: 0 the sports car's, 1 the pickup's), its tint, its cruise, and whether it ends
/// where a road leaves the map (<see cref="ToExit"/>: there it drives off and is gone).</summary>
public sealed record TrafficTrip(Route Route, (Vector2 At, float Heading, float HeightPx) Start, CarBody Body, Handling Handling,
    int Look, Color Tint, double CruiseMs, bool ToExit = false);

/// <summary>Where traffic comes from (the game: random lane trips over the world's roads; a test: a fixed list).</summary>
public interface ITrafficSource
{
    /// <summary>A new trip, or null when none can be found. <paramref name="initial"/>: the stage is starting, so it may
    /// start anywhere away from the player; otherwise out of the player's sight, near enough to matter.</summary>
    TrafficTrip? Next(Random rng, Vector2 player, IReadOnlyList<Vector2> taken, bool initial);

    /// <summary>A car at its trip's end (not an exit) drives on: a new way from where it stands. Null: none (a dead end).</summary>
    (Route Route, bool ToExit)? Onward(Random rng, Car car);
}

/// <summary>A traffic car on the road: its trip, its car, its driver.</summary>
public sealed class Traveller
{
    public Traveller(TrafficTrip trip, double pxPerMetre)
    {
        Trip = trip;
        _ppm = pxPerMetre;
        Car = new Car(trip.Start.At, trip.Start.Heading, pxPerMetre, trip.Handling) { HeightPx = trip.Start.HeightPx };
        Runner = new Runner(trip.Route, pxPerMetre, trip.Route.Project(trip.Start.At), trip.CruiseMs);
    }

    public TrafficTrip Trip { get; private set; }
    public Car Car { get; }
    public Runner Runner { get; private set; }
    public CarBody Body => Trip.Body;
    /// <summary>Seconds it has stood still (wrecked or blocked): a car stuck long enough is taken away out of sight.</summary>
    public double StillFor { get; set; }
    /// <summary>On along a new way from where it stands: the same car, paint and pace.</summary>
    public void DriveOn(Route route, bool toExit, double pxPerMetre)
    {
        Trip = Trip with { Route = route, ToExit = toExit };
        Runner = new Runner(route, pxPerMetre, route.Project(Car.Position), Trip.CruiseMs);
    }

    /// <summary>At its trip's end.</summary>
    public bool Arrived => Trip.Route.Length - Runner.Along < 3 * _ppm;

    private readonly double _ppm;

    /// <summary>The most it may go so it comes to a stop at its trip's end, m/s.</summary>
    public double EndCapMs => Math.Sqrt(2 * Traffic.ComfortBrakeMs2 * Math.Max(0, (Trip.Route.Length - Runner.Along) / _ppm - 1));
}

/// <summary>
/// **Innocent traffic (WI-026, his ask: "add some random traffic to current stage, both in same direction and opposite
/// traffic").** Every car on the road reads the cars ahead of it in its own lane: traffic slows behind them and keeps its
/// distance; the fleeing target pulls out and passes. Every number is a first guess, to be found in play (vision §11).
/// </summary>
public static class Traffic
{
    public const int Cars = 14;                    // on the stage at once
    public const double CruiseFromMs = 17, CruiseToMs = 24;   // ~60 to ~85 km/h
    public const double LookAheadMetres = 40;      // how far ahead a driver watches its lane
    public const double LaneHalfMetres = 2.0;      // a car this far to either side of its line is in its lane
    public const double CarLengthMetres = 5;       // centre to centre, a gap of zero
    public const double KeepGapMetres = 6;         // traffic stops this far behind the car ahead
    public const double ComfortBrakeMs2 = 6;       // and slows for it this hard
    public const double PassAsideMetres = 3.6;     // the target pulls out this far to pass (one lane)
    public const double PassSlowerByMs = 3;        // it passes a car going this much slower than it would
    public const double OutOfSightMetres = 70;     // traffic comes and goes only this far from the player
    public const double StuckSeconds = 8;          // standing this long, it is taken away (once out of sight)

    /// <summary>The nearest car ahead in <paramref name="car"/>'s lane (its own line, <paramref name="asideM"/> to the right),
    /// and the most <paramref name="car"/> may go now so it stops <see cref="KeepGapMetres"/> behind it. Null: lane clear.
    /// <paramref name="behindM"/>: also cars this far back alongside (a car being passed).</summary>
    public static (double CapMs, Car Blocker, double GapM, double LateralM)? Ahead(Car car, IEnumerable<Car> others, double ppm,
        double asideM = 0, double behindM = 0)
    {
        (double, Car, double, double)? best = null;
        foreach (var o in others)
        {
            if (ReferenceEquals(o, car)) continue;
            var d = (o.Position - car.Position) / (float)ppm;
            double fwd = Vector2.Dot(d, car.Forward), lat = Vector2.Dot(d, car.Right);
            if (fwd <= -behindM || fwd > LookAheadMetres || Math.Abs(lat - asideM) > LaneHalfMetres) continue;
            double gap = fwd - CarLengthMetres;
            if (best is { } b && gap >= b.Item3) continue;
            double along = Math.Max(0, Vector2.Dot(o.Velocity, car.Forward));   // its speed my way (oncoming: none)
            double room = gap - KeepGapMetres;
            double cap = room <= 0 ? 0 : along + Math.Sqrt(2 * ComfortBrakeMs2 * room);
            best = (cap, o, gap, lat);
        }
        return best;
    }

    /// <summary>Where the fleeing target wants to drive across its line, metres right of it: out to pass a slower car in its
    /// lane ahead (on the side away from it, the left when it is dead ahead), staying out until it is a car's length past,
    /// then back in.</summary>
    public static double PassAside(Car car, Runner runner, IEnumerable<Car> others, double ppm)
    {
        double laneM = -runner.Offset / ppm;   // its own lane, seen from where the car is now
        if (Ahead(car, others, ppm, laneM, behindM: CarLengthMetres * 1.5) is not { } a || a.GapM > LookAheadMetres * 0.75) return 0;
        double along = Vector2.Dot(a.Blocker.Velocity, car.Forward);
        if (along > runner.Cruise - PassSlowerByMs) return 0;
        if (Math.Abs(runner.Offset) > 1) return Math.Sign(runner.Offset) * PassAsideMetres;   // already out: stay that side
        return a.LateralM - laneM >= 0 ? -PassAsideMetres : PassAsideMetres;
    }
}

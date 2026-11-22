using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using XGame.WorldData;
using XGame.WorldScene;

namespace SpyOnWheels;

/// <summary>
/// **WI-026: traffic trips over the world's roads.** A trip starts at a random place on a random road, on a random side of
/// it, so traffic drives both ways wherever the road has both. It goes the way the lane graph goes to another random
/// place: the same lanes and joints a route drawn in Xorpheus would take (<see cref="RouteWorld"/>). Railway tracks carry
/// no cars. A trip is at least <see cref="MinTripMetres"/> long and starts where a car fits.
/// </summary>
public sealed class LaneTrips : ITrafficSource
{
    public const double MinTripMetres = 80;
    public const double ApartMetres = 15;            // from every other car at the start
    public const double FirstFromPlayerMetres = 40;  // at the stage's start
    public const double ComesInWithinMetres = 250;   // later: out of sight, but no further than this
    public const double PickupShare = 0;          // the pickup picture is red, and a tint only darkens it: traffic pickups would read as the target
    public const double ToExitShare = 0.5;        // half the trips end where a road leaves the map; the rest somewhere on the roads, and drive on from there
    private const int Tries = 80;
    public const int EdgeCells = 3;
    public const double StartInMetres = 8;

    private static readonly Color[] Paints =
    {
        new(200, 60, 50), new(60, 100, 190), new(240, 240, 235), new(40, 40, 45), new(70, 150, 80),
        new(230, 190, 60), new(150, 150, 160), new(120, 70, 140),
    };

    private readonly RouteWorld _routes;
    private readonly List<(LaneRoad Road, double Length)> _roads;
    private readonly double _total;
    private readonly double _ppm;
    private readonly IDriveWorld _world;
    private readonly Func<Vector2, float> _heightAt;
    private readonly CarBody[] _bodies;
    private readonly List<(LaneRoad Road, double At)> _exits = new();

    /// <summary>The road ends traffic leaves the map by.</summary>
    public int Exits => _exits.Count;

    /// <summary>A point (cells) within 1.5 cells of a road's control line.</summary>
    private static bool Near(IReadOnlyList<int[]> points, double x, double y)
    {
        for (int i = 0; i + 1 < points.Count; i++)
        {
            double ax = points[i][0], ay = points[i][1], dx = points[i + 1][0] - ax, dy = points[i + 1][1] - ay;
            double len2 = dx * dx + dy * dy;
            double t = len2 == 0 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0, 1);
            double ex = ax + t * dx - x, ey = ay + t * dy - y;
            if (ex * ex + ey * ey <= 1.5 * 1.5) return true;
        }
        return false;
    }

    /// <param name="bodies">The box of each look: [0] the sports car's, [1] the pickup's.</param>
    public LaneTrips(WorldFile world, RoadSceneBuilder roads, IDriveWorld driveWorld, Func<Vector2, float> heightAt, CarBody[] bodies)
    {
        _routes = new RouteWorld(world, roads.TrafficSideOf, r => roads.StandardOf(r).LanePx);
        _roads = (world.LaneRoads ?? new List<LaneRoad>())
            .Where(r => r.Points.Count >= 2 && !roads.IsTrack(r))
            .Select(r => (r, LaneStretches.ControlLength(r.Points)))
            .Where(r => r.Item2 > 0).ToList();
        _total = _roads.Sum(r => r.Length);
        _ppm = world.Project.EffectivePxPerMetre;
        _world = driveWorld;
        _heightAt = heightAt;
        _bodies = bodies;
        // Where a road leaves the map: an end of it no other road touches, at the map's edge (a car vanishing mid-map
        // would be seen).
        foreach (var (road, length) in _roads)
            foreach (bool atEnd in new[] { false, true })
            {
                var p = atEnd ? road.Points[^1] : road.Points[0];
                bool joined = _roads.Any(o => !ReferenceEquals(o.Road, road) && Near(o.Road.Points, p[0], p[1]));
                var g = world.Ground;
                bool atEdge = p[0] - g.Origin[0] <= EdgeCells || p[1] - g.Origin[1] <= EdgeCells
                    || g.Origin[0] + g.Width - p[0] <= EdgeCells || g.Origin[1] + g.Height - p[1] <= EdgeCells;
                if (!joined && atEdge) _exits.Add((road, atEnd ? length : 0));
            }
    }

    /// <summary>No road to drive: no traffic.</summary>
    public bool Empty => _roads.Count == 0;

    private LogicNode RandomNode(Random rng)
    {
        double pick = rng.NextDouble() * _total;
        foreach (var (road, length) in _roads)
        {
            if (pick <= length) return new LogicNode { Road = road.Id, At = pick, Left = rng.Next(2) == 0 };
            pick -= length;
        }
        var (last, l) = _roads[^1];
        return new LogicNode { Road = last.Id, At = l, Left = rng.Next(2) == 0 };
    }

    /// <summary>A way from <paramref name="from"/> to an exit (mostly) or to a random place on the roads, at least
    /// <see cref="MinTripMetres"/> long, by the lane graph. Null when this try found none.</summary>
    private (Route Route, bool ToExit)? Way(Random rng, LogicNode from)
    {
        // Several destinations for the same start: a start whose lanes lead away from every exit still gets its trip, so
        // traffic drives each side of the road about equally (the first cut retried the start and drove 88 % one way).
        for (int i = 0; i < 10; i++)
            if (WayOnce(rng, from) is { } way) return way;
        return null;
    }

    private (Route Route, bool ToExit)? WayOnce(Random rng, LogicNode from)
    {
        bool toExit = _exits.Count > 0 && rng.NextDouble() < ToExitShare;
        var exit = toExit ? _exits[rng.Next(_exits.Count)] : default;
        var to = toExit ? new LogicNode { Road = exit.Road.Id, At = exit.At, Left = rng.Next(2) == 0 } : RandomNode(rng);
        var legs = _routes.Legs(new[] { from, to }, loop: false);
        if (legs.Count == 0 || legs.Any(l => l.Kind != RouteLegKind.Road)) return null;
        if (StageLogic.Path(legs) is not { } route || route.Length < MinTripMetres * _ppm) return null;
        return (route, toExit);
    }

    public TrafficTrip? Next(Random rng, Vector2 player, IReadOnlyList<Vector2> taken, bool initial)
    {
        if (Empty) return null;
        // The side of the road first, then a start on it: starts on one side fail more often (fewer ways on), and retrying
        // the start freely drove 4 cars in 5 the same way on his map.
        bool left = rng.Next(2) == 0;
        for (int i = 0; i < Tries; i++)
        {
            var from = RandomNode(rng);
            from.Left = left;
            var p = _routes.Position(from);
            var at = new Vector2((float)p.X, (float)p.Y);
            double toPlayer = Vector2.Distance(at, player) / _ppm;
            if (initial ? toPlayer < FirstFromPlayerMetres : toPlayer < Traffic.OutOfSightMetres || toPlayer > ComesInWithinMetres) continue;
            if (taken.Any(t => Vector2.Distance(t, at) < ApartMetres * _ppm)) continue;
            if (Way(rng, from) is not { } way) continue;
            var route = way.Route;

            int look = rng.NextDouble() < PickupShare ? 1 : 0;
            var body = _bodies[look];
            // 8 m in: a trip's first metres jog from the middle of the road's side into its lane (cars started sideways).
            float startAlong = (float)Math.Min(StartInMetres * _ppm, route.Length / 4);
            var start = route.PointAt(startAlong);
            float heading = route.HeadingAt(startAlong);
            if (!Driving.Fits(start, heading, _heightAt(start), _world, body, out float h, out _)) continue;
            double cruise = Traffic.CruiseFromMs + rng.NextDouble() * (Traffic.CruiseToMs - Traffic.CruiseFromMs);
            return new TrafficTrip(route, (start, heading, h), body, Handling.For(look == 1 ? "pickup" : "agent-car"),
                look, Paints[rng.Next(Paints.Length)], cruise, way.ToExit);
        }
        return null;
    }

    /// <summary>A car at the end of its trip drives on: a new way from the lane it stands in, or at a dead end from the other
    /// side of the road (it turns round). Null when neither has a way.</summary>
    public (Route Route, bool ToExit)? Onward(Random rng, Car car)
    {
        if (Empty || _routes.Snap(new RoadPoint(car.Position.X, car.Position.Y)) is not { } here) return null;
        for (int i = 0; i < 2; i++)
            if (Way(rng, here) is { } way) return way;
        var back = here.Copy();
        back.Left = !here.Left;
        back.Against = false;
        if (Way(rng, back) is { } round) return round;
        return null;
    }
}

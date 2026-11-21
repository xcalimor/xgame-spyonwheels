using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using XGame.WorldData;
using XGame.WorldScene;

namespace SpyOnWheels;

/// <summary>
/// **The way the target flees (WI-020 D2):** one line through the stage, in the target's own driving lane, walked by
/// distance. Built from the world's oldest paved road and on along whatever it runs into (over a bridge that leaves its end
/// onto the road where the deck lands, the same walk Xorpheus' train preview uses), then moved from the band's middle into
/// the outermost lane on the traffic's side. Kept as a dense polyline (about one point a metre): the cars only need where
/// it is, which way it runs and how far along a point lies.
/// </summary>
public sealed class Route
{
    private readonly Vector2[] _points;
    private readonly float[] _along;

    public Route(IReadOnlyList<Vector2> points)
    {
        if (points.Count < 2) throw new ArgumentException("a route has at least two points", nameof(points));
        _points = points.ToArray();
        _along = new float[_points.Length];
        for (int i = 1; i < _points.Length; i++) _along[i] = _along[i - 1] + Vector2.Distance(_points[i - 1], _points[i]);
        Length = _along[^1];
    }

    /// <summary>World px from start to end.</summary>
    public float Length { get; }

    public IReadOnlyList<Vector2> Points => _points;

    /// <summary>The point <paramref name="along"/> px from the start (clamped to the ends).</summary>
    public Vector2 PointAt(float along)
    {
        int i = SegmentAt(along);
        float span = _along[i + 1] - _along[i];
        float t = span <= 0 ? 0 : Math.Clamp((along - _along[i]) / span, 0, 1);
        return Vector2.Lerp(_points[i], _points[i + 1], t);
    }

    /// <summary>The way the route runs at <paramref name="along"/>, as a car heading (0 north, clockwise).</summary>
    public float HeadingAt(float along)
    {
        int i = SegmentAt(along);
        var d = _points[i + 1] - _points[i];
        return MathF.Atan2(d.X, -d.Y);
    }

    /// <summary>How far along the route the point nearest <paramref name="at"/> lies, searched between
    /// <paramref name="from"/> and <paramref name="to"/> px (a car is found near where it was, never on a far bend that
    /// happens to pass close).</summary>
    public float Project(Vector2 at, float from = 0, float to = float.MaxValue)
    {
        int i0 = SegmentAt(Math.Max(0, from)), i1 = SegmentAt(Math.Min(Length, to));
        float best = float.MaxValue, bestAlong = Math.Clamp(from, 0, Length);
        for (int i = i0; i <= i1; i++)
        {
            var a = _points[i]; var b = _points[i + 1];
            var ab = b - a;
            float len2 = ab.LengthSquared();
            float t = len2 <= 0 ? 0 : Math.Clamp(Vector2.Dot(at - a, ab) / len2, 0, 1);
            float d = Vector2.DistanceSquared(at, a + ab * t);
            if (d < best) { best = d; bestAlong = _along[i] + t * (_along[i + 1] - _along[i]); }
        }
        return bestAlong;
    }

    private int SegmentAt(float along)
    {
        int i = Array.BinarySearch(_along, along);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, _points.Length - 2);
    }

    /// <summary>
    /// The stage's route: the oldest paved road's line (D2), from its first point, every <paramref name="stepPx"/>, moved into
    /// the outermost lane on its traffic's side. Null when the world has no paved road.
    /// </summary>
    public static Route? Of(WorldFile world, RoadSceneBuilder roads, float stepPx = 64)
    {
        var road = (world.LaneRoads ?? new List<LaneRoad>()).Where(r => r.Points.Count >= 2 && !roads.IsTrack(r))
            .OrderBy(r => r.Born ?? int.MaxValue).FirstOrDefault();
        if (road is null) return null;
        var std = roads.StandardOf(road);
        if (TrackWalk.Of(world, road, std.LanePx, isTrack: r => !roads.IsTrack(r)) is not { } walk) return null;
        double side = roads.TrafficSideOf(road) == TrafficSide.Left ? -1 : 1;
        float lane = (float)(side * (Math.Max(1, road.Lanes) * std.LanePx / 2 - std.LanePx / 2));
        var points = new List<Vector2>();
        int n = Math.Max(1, (int)Math.Ceiling(walk.Length / stepPx));
        for (int k = 0; k <= n; k++)
        {
            var (at, heading) = walk.PoseAt(walk.Length * k / n);
            var right = new Vector2((float)-heading.Y, (float)heading.X);
            points.Add(new Vector2((float)at.X, (float)at.Y) + right * lane);
        }
        return new Route(points);
    }
}

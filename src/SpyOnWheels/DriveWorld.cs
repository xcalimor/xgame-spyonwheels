using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using XGame.WorldData;
using XGame.WorldScene;

namespace SpyOnWheels;

public enum SurfaceKind { Ground, Deck, Water }

/// <summary>A surface the car can stand on: its height in world px and what it is.</summary>
public readonly record struct Surface(float HeightPx, SurfaceKind Kind);

/// <summary>The world as the car feels it (WI-016): what it can stand on near a height, and what is solid there.</summary>
public interface IDriveWorld
{
    /// <summary>The surface at <paramref name="at"/> within a small step of <paramref name="heightPx"/>, or null: a wall
    /// (a sheer face up or down, a deck's side, the void).</summary>
    Surface? SurfaceNear(Vector2 at, float heightPx);

    /// <summary>Whether the convex <paramref name="shape"/> at <paramref name="heightPx"/> overlaps a solid; if so,
    /// <paramref name="normal"/> points out of the solid, toward the shape.</summary>
    bool HitsSolid(Vector2[] shape, float heightPx, out Vector2 normal);
}

/// <summary>
/// **The stage as the car feels it (WI-016 D1, D2, D4, D5)**, built from what the world data already says: the ground's
/// height with its ramps (<see cref="GroundSurface"/>), every bridge deck over a point (placed, overpass and valley
/// bridges, holes left out), the water (by palette, unless a paved road crosses it on a low bridge), and the solids: the
/// placed things' solid parts as the editor's red rule uses them, and sprites with collision boxes.
/// </summary>
public sealed class DriveWorld : IDriveWorld
{
    /// <summary>How far a surface may lie from the car's height and still be driven onto, in levels. A ramp changes the
    /// height a little each step; a sheer step changes it by a whole level at once.</summary>
    public const float StepLevels = 0.45f;

    private readonly WorldFile _world;
    private readonly GroundSurface _ground;
    private readonly float _levelPx, _tolerancePx;
    private readonly int _cell;
    private readonly Func<int, bool> _isWater;
    private readonly List<(LaneBridge Bridge, LaneBridgeGeometry.Frame Frame, float HeightPx)> _decks = new();
    private readonly Dictionary<(int, int), List<PlacedThings.RoadSample>> _paved = new();
    private readonly List<(Vector2[] Shape, float BasePx, float TopPx)> _solids = new();

    public DriveWorld(WorldFile world, RoadSceneBuilder roads, PlacedSceneBuilder placed, Func<string, (int Width, int Height)?> pictureSize)
    {
        _world = world;
        _ground = new GroundSurface(world);
        _levelPx = world.Project.EffectiveCellHeight;
        _tolerancePx = StepLevels * _levelPx;
        _cell = world.Project.CellSize;
        _isWater = SlopeField.WaterByPalette(world);

        foreach (var b in (world.LaneBridges ?? new List<LaneBridge>()).Concat(world.DerivedBridges ?? new List<LaneBridge>()))
            if (LaneBridgeGeometry.FrameOf(world, b, roads.BridgeLanePxOf(b)) is { } f)
            {
                _decks.Add((b, f, b.Z * _levelPx));
                foreach (var wall in DeckWalls(b, f, (float)(WallMetres * world.Project.EffectivePxPerMetre)))
                    _solids.Add((wall, b.Z * _levelPx, float.PositiveInfinity));
            }

        foreach (var s in PlacedThings.RoadSamples(world, r => roads.StandardOf(r).LanePx, roads.IsTrack).Where(s => s.What == "a road"))
        {
            var key = ((int)Math.Floor(s.X / _cell), (int)Math.Floor(s.Y / _cell));
            if (!_paved.TryGetValue(key, out var list)) _paved[key] = list = new();
            list.Add(s);
        }

        foreach (var box in placed.Boxes.Values.Where(b => b.Footprint.Length >= 3))
            _solids.Add((box.Footprint, box.BasePx, box.TopPx));
        foreach (var p in world.Placements)
            if (SpriteSolid(world, p, pictureSize) is { } shape)
                _solids.Add((shape, p.ElevationAt(world.Project.EffectiveCellHeight) * _levelPx, float.PositiveInfinity));
    }

    /// <summary>How many solids the car can hit (the report says it).</summary>
    public int SolidCount => _solids.Count;

    /// <summary>How many bridge decks the car can drive on.</summary>
    public int DeckCount => _decks.Count;

    /// <summary>A bridge wall's width (the editor draws 0.8 m).</summary>
    public const double WallMetres = 0.8;

    /// <summary>
    /// **A deck's sides are walls** (his drive, 2026-11-17: the car slipped off a deck's side onto the ramp under it and
    /// drove under the bridge). One wall a side a section, outside the lanes, except across a break; only a deck's ends
    /// meet the ground. They stand at the deck's height, so a car on the ground far below still drives under.
    /// </summary>
    public static IEnumerable<Vector2[]> DeckWalls(LaneBridge bridge, LaneBridgeGeometry.Frame f, float wallPx)
    {
        Vector2 P(double along, double across) { var p = f.At(along, across); return new Vector2((float)p.X, (float)p.Y); }
        for (int s = 0; s < bridge.Sections; s++)
        {
            if (LaneBridgeGeometry.IsBreak(bridge, s, f.Lanes)) continue;
            double a0 = s * f.SectionPx, a1 = a0 + f.SectionPx;
            foreach (int side in new[] { -1, 1 })
            {
                double inner = side * f.HalfWidth, outer = side * (f.HalfWidth + wallPx);
                yield return new[] { P(a0, inner), P(a1, inner), P(a1, outer), P(a0, outer) };
            }
        }
    }

    /// <summary>Each deck's run for the report: where it starts, which way it runs, how long it is, its height (px).</summary>
    public IEnumerable<(string RoadId, Vector2 Start, Vector2 Unit, float LengthPx, float HeightPx)> Decks =>
        _decks.Select(d => (d.Bridge.RoadId, new Vector2((float)(d.Frame.Start.X + d.Frame.Unit.Right.X * d.Frame.ShiftPx), (float)(d.Frame.Start.Y + d.Frame.Unit.Right.Y * d.Frame.ShiftPx)),
            new Vector2((float)d.Frame.Unit.X, (float)d.Frame.Unit.Y), (float)(d.Bridge.Sections * d.Frame.SectionPx), d.HeightPx));

    /// <summary>Every surface at a point, for the report: the ground's height (levels, water marked) and each deck over it.</summary>
    public string Describe(Vector2 at)
    {
        var parts = new List<string>();
        if (_ground.HeightAt(at.X, at.Y) is double g) parts.Add(FormattableString.Invariant($"ground {g:0.00}{(WaterAt(at) ? (OnPavedRoad(at) ? " water+road" : " WATER") : "")}"));
        else parts.Add("void");
        foreach (var (bridge, f, h) in _decks)
            if (OnDeck(bridge, f, at)) parts.Add(FormattableString.Invariant($"deck {h / _levelPx:0} ({bridge.RoadId[..Math.Min(8, bridge.RoadId.Length)]})"));
        return string.Join(", ", parts);
    }

    public Surface? SurfaceNear(Vector2 at, float heightPx)
    {
        Surface? best = null;
        float bestGap = float.MaxValue;
        void Consider(float h, SurfaceKind kind)
        {
            float gap = Math.Abs(h - heightPx);
            // A deck lies on top of what is under it: on a tie (a deck at the banks' level over water) the deck wins.
            if (gap <= _tolerancePx && (gap < bestGap || (gap == bestGap && kind == SurfaceKind.Deck))) { best = new Surface(h, kind); bestGap = gap; }
        }
        if (_ground.HeightAt(at.X, at.Y) is double g)
            Consider((float)(g * _levelPx), WaterAt(at) && !OnPavedRoad(at) ? SurfaceKind.Water : SurfaceKind.Ground);
        foreach (var (bridge, f, h) in _decks)
            if (OnDeck(bridge, f, at)) Consider(h, SurfaceKind.Deck);
        return best;
    }

    public bool HitsSolid(Vector2[] shape, float heightPx, out Vector2 normal)
    {
        foreach (var (solid, basePx, topPx) in _solids)
        {
            if (heightPx < basePx - _tolerancePx || heightPx >= topPx) continue;
            if (Geometry.Overlap(shape, solid, out normal)) return true;
        }
        normal = Vector2.Zero;
        return false;
    }

    private static bool OnDeck(LaneBridge bridge, LaneBridgeGeometry.Frame f, Vector2 at)
    {
        double vx = at.X - f.Start.X, vy = at.Y - f.Start.Y;
        var right = f.Unit.Right;
        double along = vx * f.Unit.X + vy * f.Unit.Y;
        double across = vx * right.X + vy * right.Y - f.ShiftPx;
        if (along < 0 || along > bridge.Sections * f.SectionPx || Math.Abs(across) > f.HalfWidth) return false;
        int section = Math.Min(bridge.Sections - 1, (int)(along / f.SectionPx));
        int lane = Math.Clamp((int)((across + f.HalfWidth) / f.LanePx), 0, Math.Max(0, f.Lanes - 1));
        return !LaneBridgeGeometry.IsHole(bridge, section, lane);
    }

    private bool WaterAt(Vector2 at)
    {
        int cx = (int)Math.Floor(at.X / _cell), cy = (int)Math.Floor(at.Y / _cell);
        var ground = _world.Ground;
        if (RoadBridges.TopGround(ground, cx, cy) is not int top) return false;
        int col = cx - (ground.Origin.Length > 0 ? ground.Origin[0] : 0), row = cy - (ground.Origin.Length > 1 ? ground.Origin[1] : 0);
        int material = ground.CellAt(col, row, top);
        return material != GroundData.Empty && _isWater(material);
    }

    /// <summary>On a paved road's band: a road sample within its own sampling step (a low bridge carries such a road over water).</summary>
    private bool OnPavedRoad(Vector2 at)
    {
        int cx = (int)Math.Floor(at.X / _cell), cy = (int)Math.Floor(at.Y / _cell);
        for (int y = cy - 1; y <= cy + 1; y++)
            for (int x = cx - 1; x <= cx + 1; x++)
                if (_paved.TryGetValue((x, y), out var list))
                    foreach (var s in list)
                        if (Vector2.DistanceSquared(at, new Vector2((float)s.X, (float)s.Y)) <= s.Step * s.Step) return true;
        return false;
    }

    /// <summary>A sprite placement's collision boxes as one world shape (their bounding box), or null when it has none:
    /// position, scale and turn applied about the sprite's top-left anchor; the asset's mirrors not yet (D2).</summary>
    private static Vector2[]? SpriteSolid(WorldFile world, Placement p, Func<string, (int Width, int Height)?> pictureSize)
    {
        if (world.AssetById(p.Asset) is not { Class: AssetClass.Sprite } asset) return null;
        var series = SpriteScene.PosterSeries(asset);
        var frame = series is { Frames.Count: > 0 } ? asset.FrameById(series.Frames[0].Frame) : null;
        var boxes = Collision.For(asset, frame);
        if (boxes.Count == 0) return null;
        float x0 = boxes.Min(b => b.X), y0 = boxes.Min(b => b.Y), x1 = boxes.Max(b => b.X + b.Width), y1 = boxes.Max(b => b.Y + b.Height);
        float s = (float)p.Scale, a = (float)p.Rotation;   // radians, as the sprite pass draws it
        var origin = new Vector2((float)p.X, (float)p.Y);
        Vector2 W(float x, float y) => origin + Vector2.Transform(new Vector2(x, y) * s, Matrix.CreateRotationZ(a));
        return new[] { W(x0, y0), W(x1, y0), W(x1, y1), W(x0, y1) };
    }
}

/// <summary>Convex polygon overlap by separating axes, with the axis of least overlap as the push-out normal.</summary>
public static class Geometry
{
    public static bool Overlap(Vector2[] a, Vector2[] b, out Vector2 normal)
    {
        normal = Vector2.Zero;
        float least = float.MaxValue;
        foreach (var poly in new[] { a, b })
            for (int i = 0; i < poly.Length; i++)
            {
                var e = poly[(i + 1) % poly.Length] - poly[i];
                if (e.LengthSquared() < 1e-9f) continue;
                var axis = Vector2.Normalize(new Vector2(-e.Y, e.X));
                var (amin, amax) = Project(a, axis);
                var (bmin, bmax) = Project(b, axis);
                float overlap = Math.Min(amax, bmax) - Math.Max(amin, bmin);
                if (overlap <= 0) return false;
                if (overlap < least) { least = overlap; normal = axis; }
            }
        // Point the normal from b (the solid) toward a (the car).
        if (Vector2.Dot(Centre(a) - Centre(b), normal) < 0) normal = -normal;
        return true;
    }

    private static (float Min, float Max) Project(Vector2[] poly, Vector2 axis)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var p in poly) { float d = Vector2.Dot(p, axis); min = Math.Min(min, d); max = Math.Max(max, d); }
        return (min, max);
    }

    public static Vector2 Centre(Vector2[] poly)
    {
        var sum = Vector2.Zero;
        foreach (var p in poly) sum += p;
        return sum / poly.Length;
    }
}

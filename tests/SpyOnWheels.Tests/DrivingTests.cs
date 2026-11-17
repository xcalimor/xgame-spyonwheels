using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using SpyOnWheels;
using Xunit;

namespace SpyOnWheels.Tests;

/// <summary>WI-016: the car in the world, on a small made-up world (level 256 px, 64 px per metre, north is -y).</summary>
public class DrivingTests
{
    private const float Level = 256, Ppm = 64, Dt = 1 / 60f;
    private static readonly CarBody Body = new(300, 125);

    /// <summary>A world of functions: the ground's height and water at a point, decks as rectangles with a height, solids.</summary>
    private sealed class World : IDriveWorld
    {
        public Func<Vector2, float?> Ground = _ => 0;
        public Func<Vector2, bool> Water = _ => false;
        public readonly List<(Rectangle Area, float Height)> Decks = new();
        public readonly List<Vector2[]> Solids = new();

        public Surface? SurfaceNear(Vector2 at, float heightPx)
        {
            Surface? best = null;
            float bestGap = float.MaxValue;
            void Consider(float h, SurfaceKind k)
            {
                float gap = Math.Abs(h - heightPx);
                if (gap <= DriveWorld.StepLevels * Level && (gap < bestGap || (gap == bestGap && k == SurfaceKind.Deck))) { best = new Surface(h, k); bestGap = gap; }
            }
            if (Ground(at) is float g) Consider(g, Water(at) ? SurfaceKind.Water : SurfaceKind.Ground);
            foreach (var (area, h) in Decks)
                if (area.Contains((int)at.X, (int)at.Y)) Consider(h, SurfaceKind.Deck);
            return best;
        }

        public bool HitsSolid(Vector2[] shape, float heightPx, out Vector2 normal)
        {
            foreach (var s in Solids)
                if (Geometry.Overlap(shape, s, out normal)) return true;
            normal = Vector2.Zero;
            return false;
        }
    }

    private static (Car Car, List<DriveEvent> Events) Drive(World world, Vector2 from, float heading, double seconds, float height = 0, float steer = 0)
    {
        var car = new Car(from, heading, Ppm) { HeightPx = height };
        var events = new List<DriveEvent>();
        for (double t = 0; t < seconds; t += Dt)
            events.Add(Driving.Step(car, new Drive(true, false, steer), Dt, world, Body, Ppm, (from, heading, height)));
        return (car, events);
    }

    private static Vector2[] Box(float x0, float y0, float x1, float y1) => new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) };

    [Fact]
    public void Flat_ground_drives_freely()
    {
        var (car, events) = Drive(new World(), Vector2.Zero, 0, 3);
        Assert.All(events, e => Assert.Equal(DriveEvent.None, e));
        Assert.True(car.Position.Y < -1000);
    }

    [Fact]
    public void Head_on_into_a_solid_stops_the_car_short_of_it()
    {
        var world = new World();
        world.Solids.Add(Box(-500, -1200, 500, -1000));   // a wall across the way, 1000 px north
        var (car, events) = Drive(world, Vector2.Zero, 0, 6);
        Assert.Contains(DriveEvent.Stopped, events);
        Assert.Equal(0, car.SpeedMs, 3);
        Assert.True(car.Position.Y - Body.LengthPx / 2 >= -1000.5f);
    }

    [Fact]
    public void A_glancing_hit_slides_along_the_solid_and_keeps_most_speed()
    {
        var world = new World();
        world.Solids.Add(Box(200, -5000, 400, 5000));      // a long wall to the east, running north-south
        var (car, events) = Drive(world, Vector2.Zero, MathHelper.ToRadians(10), 6);
        Assert.Contains(DriveEvent.Scraped, events);
        Assert.True(car.Position.Y < -2000, $"slid north along the wall, at {car.Position}");
        Assert.True(car.SpeedMs > Car.TopSpeedMs * 0.5, $"kept {car.SpeedMs:0.0} m/s");
    }

    [Fact]
    public void A_sheer_step_up_is_a_wall()
    {
        var world = new World { Ground = p => p.Y < -1000 ? Level : 0 };   // a whole level, at once
        var (car, events) = Drive(world, Vector2.Zero, 0, 6);
        Assert.Contains(DriveEvent.Stopped, events);
        Assert.Equal(0, car.HeightPx);
        Assert.True(car.Position.Y > -1000);
    }

    [Fact]
    public void A_sheer_drop_down_is_a_wall_too()
    {
        var world = new World { Ground = p => p.Y < -1000 ? 0 : Level };
        var (car, events) = Drive(world, Vector2.Zero, 0, 6, height: Level);
        Assert.Contains(DriveEvent.Stopped, events);
        Assert.Equal(Level, car.HeightPx);
    }

    [Fact]
    public void A_ramp_is_driven_up()
    {
        // One level over one cell (256 px), the steepest ramp the ground makes.
        var world = new World { Ground = p => Math.Clamp(-(p.Y + 1000), 0, Level) };
        var (car, events) = Drive(world, Vector2.Zero, 0, 4);
        Assert.DoesNotContain(DriveEvent.Stopped, events);
        Assert.Equal(Level, car.HeightPx);
        Assert.True(car.Position.Y < -1300);
    }

    [Fact]
    public void Water_sinks_the_car_back_to_its_start()
    {
        var world = new World { Water = p => p.Y < -1000 && p.Y > -2000 };
        var (car, events) = Drive(world, Vector2.Zero, 0, 3);
        Assert.Contains(DriveEvent.Sank, events);
        Assert.True(car.Position.Y > -1000);
    }

    [Fact]
    public void A_deck_carries_the_car_over_water()
    {
        var world = new World { Water = p => p.Y < -1000 && p.Y > -2000 };
        world.Decks.Add((new Rectangle(-200, -2100, 400, 1200), 0));   // a deck at the banks' level, over the water
        var (car, events) = Drive(world, Vector2.Zero, 0, 4);
        Assert.DoesNotContain(DriveEvent.Sank, events);
        Assert.True(car.Position.Y < -2500);
    }

    [Fact]
    public void A_car_under_a_bridge_drives_beneath_it()
    {
        var world = new World();
        world.Decks.Add((new Rectangle(-1000, -2000, 2000, 400), 2 * Level));   // a deck two levels up, across the way
        var (car, events) = Drive(world, Vector2.Zero, 0, 4);
        Assert.All(events, e => Assert.Equal(DriveEvent.None, e));
        Assert.Equal(0, car.HeightPx);
        Assert.True(car.Position.Y < -2500);
    }

    [Fact]
    public void A_car_on_a_deck_stays_on_it_over_a_lower_road()
    {
        // A deck one level up over ground at 0, reached from a bank at the deck's level.
        var world = new World { Ground = p => p.Y > -500 ? Level : 0 };
        world.Decks.Add((new Rectangle(-200, -2500, 400, 2100), Level));   // it overlaps the bank a little, as a landed deck does
        var (car, events) = Drive(world, Vector2.Zero, 0, 1.5, height: Level);
        Assert.DoesNotContain(DriveEvent.Stopped, events);
        Assert.Equal(Level, car.HeightPx);
    }

    [Fact]
    public void Turning_into_a_wall_on_the_spot_is_refused()
    {
        var world = new World();
        world.Solids.Add(Box(64, -500, 300, 500));          // just right of the car's side
        var car = new Car(Vector2.Zero, 0, Ppm);
        for (int i = 0; i < 120; i++) Driving.Step(car, new Drive(true, false, 1), Dt, world, Body, Ppm, (Vector2.Zero, 0, 0));
        Assert.False(world.HitsSolid(Body.Shape(car.Position, car.Heading), 0, out _));
    }
}

/// <summary>WI-016 round 1, his drive: the car slipped off a deck's side onto the ramp under it and drove under the bridge.</summary>
public class DeckWallTests
{
    private const float Level = 256, Ppm = 64, Dt = 1 / 60f;

    [Fact]
    public void A_decks_sides_are_walls_at_its_height()
    {
        // A deck two levels up running north from y = 0, 2 lanes of 224 px, 10 sections of 256 px.
        var bridge = new XGame.WorldData.LaneBridge { RoadId = "r", Sections = 10, Z = 2 };
        var frame = new XGame.WorldData.LaneBridgeGeometry.Frame(new[] { 0, 0 }, (0, -1), new XGame.WorldData.RoadPoint(0, 0),
            new XGame.WorldData.RoadPoint(0, -1), 256, 2, 224);
        var walls = DriveWorld.DeckWalls(bridge, frame, 0.8f * Ppm).ToList();
        Assert.Equal(20, walls.Count);

        // Under the deck the ground is a ramp reaching 1.7 levels: near enough the deck to be stepped onto, were the side open.
        var world = new HeightWorld(p => p.Y > -1280 && p.Y < 0 && Math.Abs(p.X) <= 224 ? 2 * Level : (float?)null,
            p => p.Y > -1280 && p.Y < 0 && Math.Abs(p.X) < 900 ? 1.7f * Level : 0f, walls, 2 * Level);   // beyond its ends a sheer drop
        var car = new Car(new Vector2(0, -300), 0, Ppm) { HeightPx = 2 * Level };
        var body = new CarBody(300, 125);
        for (int i = 0; i < 240; i++)
            Driving.Step(car, new Drive(true, false, 1), Dt, world, body, Ppm, (new Vector2(0, -300), 0, 2 * Level));
        Assert.Equal(2 * Level, car.HeightPx);
    }

    /// <summary>One deck (a function of the point) over ground, and walls standing at the deck's height.</summary>
    private sealed class HeightWorld : IDriveWorld
    {
        private readonly Func<Vector2, float?> _deck;
        private readonly Func<Vector2, float> _ground;
        private readonly List<Vector2[]> _walls;
        private readonly float _wallBase;

        public HeightWorld(Func<Vector2, float?> deck, Func<Vector2, float> ground, List<Vector2[]> walls, float wallBase) =>
            (_deck, _ground, _walls, _wallBase) = (deck, ground, walls, wallBase);

        public Surface? SurfaceNear(Vector2 at, float heightPx)
        {
            Surface? best = null;
            float bestGap = float.MaxValue;
            void Consider(float h, SurfaceKind k)
            {
                float gap = Math.Abs(h - heightPx);
                if (gap <= DriveWorld.StepLevels * Level && (gap < bestGap || (gap == bestGap && k == SurfaceKind.Deck))) { best = new Surface(h, k); bestGap = gap; }
            }
            Consider(_ground(at), SurfaceKind.Ground);
            if (_deck(at) is float d) Consider(d, SurfaceKind.Deck);
            return best;
        }

        public bool HitsSolid(Vector2[] shape, float heightPx, out Vector2 normal)
        {
            normal = Vector2.Zero;
            if (heightPx < _wallBase - DriveWorld.StepLevels * Level) return false;
            foreach (var w in _walls)
                if (Geometry.Overlap(shape, w, out normal)) return true;
            return false;
        }
    }
}

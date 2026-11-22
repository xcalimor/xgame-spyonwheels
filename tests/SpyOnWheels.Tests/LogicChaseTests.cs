using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using SpyOnWheels;
using XGame.WorldData;
using XGame.WorldScene;
using Xunit;

namespace SpyOnWheels.Tests;

/// <summary>FEAT-009 S4: the chase a stage's logic gives (the player where the Hero stands, each Runner where it stands on its
/// own route), on the open flat world of <see cref="ChaseTests"/> (64 px per metre, north is -y).</summary>
public class LogicChaseTests
{
    private const float Ppm = 64, Level = 256, Dt = 1 / 60f;
    private static readonly CarBody Body = new(300, 125);

    private sealed class Open : IDriveWorld
    {
        public Surface? SurfaceNear(Vector2 at, float heightPx) =>
            Math.Abs(heightPx) <= DriveWorld.StepLevels * Level ? new Surface(0, SurfaceKind.Ground) : new Surface(heightPx, SurfaceKind.Deck);
        public bool HitsSolid(Vector2[] shape, float heightPx, out Vector2 normal) { normal = Vector2.Zero; return false; }
    }

    private static float M(double metres) => (float)(metres * Ppm);

    /// <summary>A straight route north along x = <paramref name="xM"/>, from y = 0, <paramref name="metres"/> long.</summary>
    private static Route North(double xM, double metres) =>
        new(Enumerable.Range(0, (int)metres + 1).Select(i => new Vector2(M(xM), -M(i))).ToList());

    private static ChaseTarget Runner(string name, Route route, double startM, bool loop = false)
    {
        var at = route.PointAt(M(startM));
        // Equal cars (WI-022): these tests hold the chase's rules, not the masses (HandlingTests does).
        return new ChaseTarget(name, route, loop, (at, route.HeadingAt(M(startM)), 0f), Body, Handling.SportsCar);
    }

    private static Chase ChaseWith(params ChaseTarget[] targets) =>
        new(new Open(), Ppm, (new Vector2(0, M(5)), 0f, 0f), Body, targets, Level);

    [Fact]
    public void The_player_starts_where_the_hero_stands_and_each_runner_where_it_stands_on_its_route()
    {
        var chase = ChaseWith(Runner("Pickup", North(-20, 500), 40), Runner("Truck", North(20, 500), 60));
        Assert.Equal(new Vector2(0, M(5)), chase.Player.Position);
        Assert.Equal(new[] { -M(40), -M(60) }, chase.Targets.Select(t => t.Car.Position.Y));
        Assert.Equal(new[] { M(40), M(60) }, chase.Targets.Select(t => t.Runner.Along));
        Assert.Equal(new[] { "Pickup", "Truck" }, chase.Targets.Select(t => t.Name));
    }

    [Fact]
    public void Left_alone_a_runner_gets_away_at_its_own_border()
    {
        var chase = ChaseWith(Runner("Pickup", North(-20, 2000), 40), Runner("Truck", North(20, 300), 40));
        while (chase.State == ChaseState.Running && chase.Clock < 60) chase.Step(new Drive(false, false, 0), Dt);
        Assert.Equal("IT GOT AWAY", chase.Outcome);
        var truck = chase.Targets[1];
        Assert.True(truck.Runner.Along >= truck.BorderAlong);                        // the short route's runner got there
        Assert.True(chase.Targets[0].Runner.Along < chase.Targets[0].BorderAlong);   // the long one is still on its way
    }

    [Fact]
    public void Stopping_one_runner_is_not_enough_every_one_must_be_stopped()
    {
        var chase = ChaseWith(Runner("Pickup", North(-4, 2500), 35), Runner("Truck", North(4, 2500), 70));
        bool oneStoppedWhileRunning = false;
        for (int i = 0; i < 60 * 120 && chase.State == ChaseState.Running; i++)
        {
            // At the nearest one still fleeing, flat out.
            // WI-028: a finished one only brakes; once none flees, wait for the last to stop.
            if (chase.Fleeing.OrderBy(t => Vector2.Distance(t.Car.Position, chase.Player.Position)).FirstOrDefault() is not { } prey) { chase.Step(new Drive(false, true, 0), Dt); continue; }
            var to = prey.Car.Position - chase.Player.Position;
            float off = MathHelper.WrapAngle(MathF.Atan2(to.X, -to.Y) - chase.Player.Heading);
            chase.Step(new Drive(true, false, Math.Clamp(off * 3, -1, 1)), Dt);
            oneStoppedWhileRunning |= chase.State == ChaseState.Running && chase.Targets.Count(t => t.Stopped || t.Finished) == 1;
        }
        Assert.True(oneStoppedWhileRunning, "one stopped, and the chase went on");
        Assert.Equal("STOPPED", chase.Outcome);
        Assert.All(chase.Targets, t => Assert.True(t.Stopped));
    }

    [Fact]
    public void A_runner_on_a_loop_has_no_border_and_never_gets_away()
    {
        var chase = ChaseWith(Runner("Pickup", North(0, 300), 40, loop: true));
        Assert.True(float.IsPositiveInfinity(chase.BorderAlong));
        while (chase.State == ChaseState.Running && chase.Clock < 30) chase.Step(new Drive(false, false, 0), Dt);
        Assert.Equal(ChaseState.Running, chase.State);
    }

    [Fact]
    public void A_routes_legs_are_one_line_to_drive_the_shared_points_once()
    {
        var legs = new[]
        {
            new RouteLeg(0, 1, new[] { new RoadPoint(0, 0), new RoadPoint(0, -100), new RoadPoint(0, -200) }, RouteLegKind.Road),
            new RouteLeg(1, 2, new[] { new RoadPoint(0, -200), new RoadPoint(100, -200) }, RouteLegKind.Straight),
        };
        var route = StageLogic.Path(legs)!;
        Assert.Equal(4, route.Points.Count);
        Assert.Equal(300, route.Length, 3f);
        Assert.Null(StageLogic.Path(Array.Empty<RouteLeg>()));
    }

    [Fact]
    public void A_placed_cars_box_is_read_from_its_solid_part_turned_back_to_face_north()
    {
        // A 2 x 4.5 m solid part (128 x 288 px), placed turned a quarter clockwise: it lies east-west on the ground.
        var at = new Vector2(1000, 1000);
        var solid = new[] { at + new Vector2(-144, -64), at + new Vector2(144, -64), at + new Vector2(144, 64), at + new Vector2(-144, 64) };
        var picture = new PlacedPicture("car", "car.png", at, MathHelper.PiOver2, new Vector2(160, 320), 0, solid, 100);
        var body = StageLogic.BodyOf(picture);
        Assert.Equal(288, body.LengthPx, 2f);
        Assert.Equal(128, body.WidthPx, 2f);
        // No solid part: most of the picture, as the sprite car's fallback.
        var bare = StageLogic.BodyOf(picture with { Solid = null });
        Assert.Equal(320 * 0.94f, bare.LengthPx, 2f);
    }
}

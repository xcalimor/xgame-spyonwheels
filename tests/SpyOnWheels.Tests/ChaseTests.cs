using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using SpyOnWheels;
using Xunit;

namespace SpyOnWheels.Tests;

/// <summary>WI-020: the route, the target's driver, car meets car, and the Stop task, on an open flat asphalt world
/// (64 px per metre, north is -y, level 256 px).</summary>
public class ChaseTests
{
    private const float Ppm = 64, Level = 256, Dt = 1 / 60f;
    private static readonly CarBody Body = new(300, 125);

    /// <summary>Flat asphalt everywhere; <see cref="Heights"/> may lift a region (a deck seen from below, say).</summary>
    private sealed class Open : IDriveWorld
    {
        public Surface? SurfaceNear(Vector2 at, float heightPx) =>
            Math.Abs(heightPx) <= DriveWorld.StepLevels * Level ? new Surface(0, SurfaceKind.Ground)
            : new Surface(heightPx, SurfaceKind.Deck);   // a car put up high stays up (a deck everywhere at its height)
        public bool HitsSolid(Vector2[] shape, float heightPx, out Vector2 normal) { normal = Vector2.Zero; return false; }
    }

    private static float M(double metres) => (float)(metres * Ppm);

    /// <summary>A straight route north from the origin, <paramref name="metres"/> long.</summary>
    private static Route North(double metres) => new(Enumerable.Range(0, (int)metres + 1).Select(i => new Vector2(0, -M(i))).ToList());

    private static Chase ChaseOn(Route route) => new(route, new Open(), Ppm, Body, Body, _ => 0f, Level);

    private static Car CarAt(double xM, double yM, double headingDeg, double speedMs)
    {
        var car = new Car(new Vector2(M(xM), M(yM)), MathHelper.ToRadians((float)headingDeg), Ppm);
        if (speedMs != 0) car.Push(car.Forward * (float)speedMs);
        return car;
    }

    // ---- the route ----

    [Fact]
    public void A_route_walks_by_distance_and_finds_a_point_near_where_the_car_was()
    {
        // 100 m north, then 100 m east: an L.
        var pts = Enumerable.Range(0, 101).Select(i => new Vector2(0, -M(i)))
            .Concat(Enumerable.Range(1, 100).Select(i => new Vector2(M(i), -M(100)))).ToList();
        var r = new Route(pts);
        Assert.Equal(M(200), r.Length, 1f);
        Assert.Equal(-M(50), r.PointAt(M(50)).Y, 1f);
        Assert.Equal(0, MathHelper.ToDegrees(r.HeadingAt(M(50))), 1f);
        Assert.Equal(90, MathHelper.ToDegrees(r.HeadingAt(M(150))), 1f);
        Assert.Equal(M(150), r.Project(new Vector2(M(50), -M(103))), 2f);
        // Searched only near where it was: the same point, looked for in the first 60 m, lands on the first leg.
        Assert.True(r.Project(new Vector2(M(50), -M(103)), 0, M(60)) <= M(61));   // the segment the limit falls in is searched whole
    }

    // ---- the target's driver ----

    [Fact]
    public void The_target_follows_a_bending_route_to_its_end_close_to_its_line()
    {
        // North 150 m, then a 90-degree bend of radius 24 m (the island's bends), then east 150 m.
        var pts = new List<Vector2>();
        for (int i = 0; i <= 150; i++) pts.Add(new Vector2(0, -M(i)));
        for (int k = 1; k <= 38; k++) { double a = k / 38.0 * Math.PI / 2; pts.Add(new Vector2(M(24 - 24 * Math.Cos(a)), -M(150 + 24 * Math.Sin(a)))); }
        for (int i = 1; i <= 150; i++) pts.Add(new Vector2(M(24 + i), -M(174)));
        var route = new Route(pts);
        var chase = ChaseOn(route);
        float worst = 0;
        double top = 0;
        while (chase.State == ChaseState.Running && chase.Clock < 60)
        {
            chase.Step(new Drive(false, false, 0), Dt);
            worst = MathF.Max(worst, Vector2.Distance(chase.Target.Position, route.PointAt(chase.Runner.Along)));
            top = Math.Max(top, chase.Target.SpeedMs);
        }
        Assert.Equal("IT GOT AWAY", chase.Outcome);
        Assert.True(worst < M(1.5), $"strayed {worst / Ppm:0.0} m off its line");
        Assert.True(top > 28, $"top {top * 3.6:0} km/h: it should flee near its cruise");
    }

    // ---- car meets car ----

    [Theory]
    [InlineData(0, 6, 0, ContactKind.Rear)]         // straight behind, same way
    [InlineData(-2.2, 0, 0, ContactKind.Side)]      // alongside on its left, same way
    [InlineData(-3, 1, 60, ContactKind.Angled)]     // into its left flank at 60 degrees
    [InlineData(0, -6, 180, ContactKind.Frontal)]   // nose to nose
    public void A_contact_is_read_from_where_it_lands_and_how_far_apart_the_cars_face(double dx, double dy, double headingDeg, ContactKind kind)
    {
        var struck = CarAt(0, 0, 0, 20);
        var attacker = CarAt(dx, dy, headingDeg, 30);
        // The reading needs only the contact's line, not overlapping boxes.
        var normal = Vector2.Normalize(attacker.Position - struck.Position);
        if (kind == ContactKind.Side) normal = -struck.Right;
        if (kind == ContactKind.Rear) normal = -struck.Forward;
        Assert.Equal(kind, Contact.Classify(attacker, struck, normal));
    }

    [Fact]
    public void A_hard_hit_from_behind_spins_the_target_out_a_soft_one_only_wobbles_it()
    {
        var soft = Hit(behindMs: 4);
        Assert.Equal(ContactKind.Rear, soft.Kind);
        Assert.False(soft.SpunOut);
        var hard = Hit(behindMs: 20);
        Assert.True(hard.SpunOut);

        static ContactResult Hit(double behindMs)
        {
            var struck = CarAt(0, 0, 0, 25);
            var attacker = CarAt(0.3, 4.8, 0, 25 + behindMs);
            return Contact.Resolve(attacker, struck, -struck.Forward)!.Value;
        }
    }

    [Fact]
    public void The_wider_the_angle_the_harder_the_target_turns()
    {
        double Yaw(double angleDeg)
        {
            var struck = CarAt(0, 0, 0, 20);
            var attacker = CarAt(-3, 1, angleDeg, 25);
            return Math.Abs(Contact.Resolve(attacker, struck, -struck.Right)!.Value.YawRadS);
        }
        Assert.True(Yaw(80) > Yaw(40) * 1.3, $"80 deg {Yaw(80):0.00} vs 40 deg {Yaw(40):0.00}");
    }

    [Fact]
    public void A_side_hit_throws_the_target_sideways_and_does_not_speed_it_up()
    {
        var struck = CarAt(0, 0, 0, 25);
        var attacker = CarAt(-2.2, 0, 10, 26);
        var hit = Contact.Resolve(attacker, struck, -struck.Right)!.Value;
        Assert.Equal(ContactKind.Side, hit.Kind);
        Assert.False(hit.SpunOut);
        var right = struck.Right;
        var from = struck.Position;
        for (int i = 0; i < 30; i++) struck.Step(new Drive(false, false, 0), Dt);
        float sideways = Vector2.Dot(struck.Position - from, right);
        Assert.True(sideways > M(1), $"thrown {sideways / Ppm:0.00} m sideways in 0.5 s");
        Assert.True(struck.SpeedMs <= 25.5, $"forward {struck.SpeedMs:0.0} m/s: the shove must not become speed");
    }

    [Fact]
    public void A_spun_out_car_takes_no_orders_until_it_has_skidded_to_a_stop()
    {
        var car = CarAt(0, 0, 0, 30);
        car.Kick(4, spinOut: true);
        double t = 0;
        while (car.Spinning && t < 10) { car.Step(new Drive(true, false, 1), Dt); t += Dt; Assert.True(car.SpeedMs <= 30.01 || car.Spinning); }
        Assert.False(car.Spinning);
        Assert.InRange(t, 0.5, 6);
        Assert.Equal(0, car.Velocity.Length(), 2);
        car.Step(new Drive(true, false, 0), Dt * 10);
        Assert.True(car.SpeedMs > 0, "once stopped it drives again");
    }

    [Fact]
    public void The_slower_car_takes_more_damage()
    {
        double Damage(double attackerMs, double struckMs)
        {
            var struck = CarAt(0, 0, 0, struckMs);
            var attacker = CarAt(-3, 1, 60, attackerMs);
            return Contact.Resolve(attacker, struck, -struck.Right)!.Value.Damage;
        }
        Assert.True(Damage(10, 30) > Damage(30, 10));
    }

    /// <summary>Open flat asphalt with a wall: everything west of <see cref="WallX"/> px is solid.</summary>
    private sealed class Walled : IDriveWorld
    {
        public float WallX;
        public Surface? SurfaceNear(Vector2 at, float heightPx) => new Surface(0, SurfaceKind.Ground);
        public bool HitsSolid(Vector2[] shape, float heightPx, out Vector2 normal)
        {
            normal = Vector2.UnitX;
            return shape.Any(p => p.X < WallX);
        }
    }

    [Fact]
    public void Cars_pushed_apart_are_never_pushed_into_a_wall()
    {
        // Your report (2026-11-21): the car went through everything. Two cars side by side, yours against a wall on its
        // left, overlapping the target on its right: parting them must not put yours inside the wall, where the driving
        // lets a car that is "caught" move freely through everything until it is out.
        var wall = new Walled();
        var chase = new Chase(North(400), wall, Ppm, Body, Body, _ => 0f, Level);
        float half = Body.WidthPx / 2;
        wall.WallX = chase.Player.Position.X - half - 2;   // 2 px of air between your car and the wall
        chase.Target.Reset(chase.Player.Position + new Vector2(Body.WidthPx - 20, 0), chase.Player.Heading, 0);   // 20 px overlap
        chase.Step(new Drive(false, false, 0), Dt);
        Assert.True(Driving.Fits(chase.Player.Position, chase.Player.Heading, 0, wall, Body, out _, out _), "your car was pushed into the wall");
        Assert.True(Driving.Fits(chase.Target.Position, chase.Target.Heading, 0, wall, Body, out _, out _), "the target was pushed into the wall");
        // And steering into the wall afterwards, it stays out.
        for (int i = 0; i < 120; i++) chase.Step(new Drive(true, false, -1), Dt);
        Assert.True(chase.Player.Position.X - half >= wall.WallX - 1, "your car drove into the wall");
    }

    // ---- the Stop task ----

    [Fact]
    public void Rammed_head_on_the_player_dies()
    {
        var chase = ChaseOn(North(400));
        // Turn the player round to face the target and drive into it.
        chase.Player.Reset(chase.Target.Position + new Vector2(0, -M(10)), MathF.PI, 0);
        for (int i = 0; i < 300 && chase.State == ChaseState.Running; i++) chase.Step(new Drive(true, false, 0), Dt);
        Assert.Equal("HEAD-ON", chase.Outcome);
        Assert.Equal(ChaseState.Lost, chase.State);
    }

    [Fact]
    public void Every_push_costs_health_and_spinning_the_target_to_a_stop_wins()
    {
        var chase = ChaseOn(North(1500));
        // Flat out up the road behind it: the player is faster, so it catches it and rams it from behind.
        for (int i = 0; i < 60 * 40 && chase.State == ChaseState.Running; i++)
        {
            var to = chase.Target.Position - chase.Player.Position;
            float off = MathHelper.WrapAngle(MathF.Atan2(to.X, -to.Y) - chase.Player.Heading);
            chase.Step(new Drive(true, false, Math.Clamp(off * 3, -1, 1)), Dt);
        }
        Assert.NotEmpty(chase.Contacts);
        Assert.True(chase.Health < Chase.MaxHealth);
        Assert.Equal(Chase.MaxHealth - chase.Contacts.Sum(c => c.Result.Damage), chase.Health, 3);
        Assert.Equal("STOPPED", chase.Outcome);
        Assert.Equal(ChaseState.Won, chase.State);
    }

    [Fact]
    public void A_target_left_alone_crosses_the_border_and_the_player_loses()
    {
        var chase = ChaseOn(North(300));
        while (chase.State == ChaseState.Running && chase.Clock < 60) chase.Step(new Drive(false, false, 0), Dt);
        Assert.Equal("IT GOT AWAY", chase.Outcome);
        Assert.True(chase.Runner.Along >= chase.BorderAlong);
        Assert.Equal(M(300 - Chase.BorderBeforeEndMetres), chase.BorderAlong, 1f);
    }

    [Fact]
    public void Cars_at_different_heights_never_touch()
    {
        var chase = ChaseOn(North(400));
        // The player a whole level up (on a deck over the target's road), coming up fast from just behind it: on one level
        // that is a rear hit within the second.
        chase.Player.Reset(chase.Target.Position + new Vector2(0, M(6)), chase.Target.Heading, Level);
        chase.Player.Push(chase.Player.Forward * 30);
        for (int i = 0; i < 60; i++) chase.Step(new Drive(true, false, 0), Dt);
        Assert.True(chase.Player.Position.Y < chase.Target.Position.Y, "it passed over the target");
        Assert.Empty(chase.Contacts);
    }
}

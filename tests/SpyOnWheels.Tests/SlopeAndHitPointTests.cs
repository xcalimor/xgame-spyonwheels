using System;
using System.Linq;
using Microsoft.Xna.Framework;
using SpyOnWheels;
using Xunit;

namespace SpyOnWheels.Tests;

/// <summary>WI-028: gravity along slopes, and the target's hit points (64 px per metre, north is -y).</summary>
public class SlopeAndHitPointTests
{
    private const float Ppm = 64, Level = 256, Dt = 1 / 60f;
    private static readonly CarBody Body = new(300, 125);

    /// <summary>Open ground whose height rises northward by <paramref name="grade"/> (metres up per metre north).</summary>
    private sealed class Ramp : IDriveWorld
    {
        private readonly double _grade;
        public Ramp(double grade) => _grade = grade;
        public Surface? SurfaceNear(Vector2 at, float heightPx) => new Surface((float)(-at.Y * _grade), SurfaceKind.Ground);
        public bool HitsSolid(Vector2[] shape, float heightPx, out Vector2 normal) { normal = Vector2.Zero; return false; }
    }

    private static float M(double metres) => (float)(metres * Ppm);

    /// <summary>A car at 20 m/s heading north (+1) or south (-1) on the ramp, coasting <paramref name="seconds"/>.</summary>
    private static double Coast(double grade, int north, double seconds)
    {
        var car = new Car(new Vector2(0, north > 0 ? 0 : -M(400)), north > 0 ? 0f : MathF.PI, Ppm);
        car.HeightPx = (float)(-car.Position.Y * grade);
        car.Push(new Vector2(0, -20 * north));
        var start = (car.Position, car.Heading, car.HeightPx);
        for (double t = 0; t < seconds; t += Dt) Driving.Step(car, new Drive(false, false, 0), Dt, new Ramp(grade), Body, Ppm, start);
        return car.SpeedMs;
    }

    [Fact]
    public void Uphill_slows_a_car_and_downhill_speeds_it_up()
    {
        double flat = Coast(0, 1, 2), up = Coast(0.3, 1, 2), down = Coast(0.3, -1, 2);   // a 30 % slope
        Assert.True(up < flat - 1.5, $"up {up:0.0} vs flat {flat:0.0} m/s");
        Assert.True(down > flat + 1.5, $"down {down:0.0} vs flat {flat:0.0} m/s");
    }

    [Fact]
    public void A_climb_costs_speed_by_energy_whatever_its_shape()
    {
        var car = new Car(Vector2.Zero, 0, Ppm);
        car.Push(new Vector2(0, -20));
        car.Climb(4);   // one 4 m step
        double expected = Math.Sqrt(400 - 2 * Car.GravityMs2 * Car.SlopeShare * 4);
        Assert.Equal(expected, car.Velocity.Length(), 3);
        car.Climb(-4);  // and back down: the speed comes back
        Assert.Equal(20, car.Velocity.Length(), 3);
    }

    [Fact]
    public void Downhill_carries_a_car_past_its_top_but_not_far_and_the_level_takes_it_back()
    {
        var car = new Car(Vector2.Zero, 0, Ppm);
        for (double t = 0; t < 6; t += Dt) car.Step(new Drive(true, false, 0), Dt);
        car.Climb(-40);   // a long drop
        car.Step(new Drive(true, false, 0), Dt);
        Assert.True(car.SpeedMs > Car.TopSpeedMs + 1);
        Assert.True(car.SpeedMs <= Car.TopSpeedMs * Car.DownhillOverTop + 1e-6);
        for (double t = 0; t < 5; t += Dt) car.Step(new Drive(true, false, 0), Dt);
        Assert.Equal(Car.TopSpeedMs, car.SpeedMs, 3);
    }

    // ---- hit points ----

    private sealed class Open : IDriveWorld
    {
        public Surface? SurfaceNear(Vector2 at, float heightPx) => new Surface(0, SurfaceKind.Ground);
        public bool HitsSolid(Vector2[] shape, float heightPx, out Vector2 normal) { normal = Vector2.Zero; return false; }
    }

    private static Chase OneTarget()
    {
        var route = new Route(Enumerable.Range(0, 3001).Select(i => new Vector2(0, -M(i))).ToList());
        var target = new ChaseTarget("Pickup", route, false, (route.PointAt(M(35)), 0f, 0f), Body, Handling.Pickup);
        return new Chase(new Open(), Ppm, (new Vector2(0, M(5)), 0f, 0f), Body, new[] { target }, Level);
    }

    /// <summary>The player flat out at the target's tail, a little off its centre (so hits turn it), for up to 90 s.</summary>
    private static void Ram(Chase chase, Func<Chase, bool> until)
    {
        while (chase.State == ChaseState.Running && chase.Clock < 90 && !until(chase))
        {
            var t = chase.Target;
            var goal = t.Position + t.Right * M(0.9);
            var to = goal - chase.Player.Position;
            float off = MathHelper.WrapAngle(MathF.Atan2(to.X, -to.Y) - chase.Player.Heading);
            chase.Step(new Drive(true, false, Math.Clamp(off * 3, -1, 1)), Dt);
        }
    }

    [Fact]
    public void Every_hit_takes_the_targets_hit_points_and_one_spin_out_no_longer_stops_it()
    {
        var chase = OneTarget();
        Ram(chase, c => c.Contacts.Count >= 1);
        var first = chase.Contacts[0].Result;
        double expected = Chase.TargetMaxHealth - (Chase.TargetHitDamage + Chase.TargetDamagePerMs * first.ClosingMs + (first.SpunOut ? Chase.TargetSpinDamage : 0));
        Assert.Equal(expected, chase.Targets[0].Health, 3);
        Assert.True(chase.Targets[0].Health > 0, "one hit does not finish it");

        // Spun out by hand, with hit points left: it recovers and flees.
        chase.Target.Kick(4, spinOut: true);
        while (chase.Target.Spinning) chase.Step(new Drive(false, true, 0), Dt);
        for (int i = 0; i < 60; i++) chase.Step(new Drive(false, true, 0), Dt);
        Assert.False(chase.Targets[0].Stopped);
        Assert.Equal(ChaseState.Running, chase.State);
    }

    [Fact]
    public void Worn_down_to_nothing_the_target_brakes_to_a_stop_and_the_chase_is_won()
    {
        var chase = OneTarget();
        Ram(chase, c => c.Targets[0].Finished);
        Assert.True(chase.Targets[0].Finished, $"health {chase.Targets[0].Health:0} after {chase.Contacts.Count} hits, {chase.Outcome}");
        Assert.True(chase.Contacts.Count >= 2, "harder to stop: it takes more than one hit");
        while (chase.State == ChaseState.Running && chase.Clock < 120) chase.Step(new Drive(false, true, 0), Dt);
        Assert.Equal("STOPPED", chase.Outcome);
    }
}

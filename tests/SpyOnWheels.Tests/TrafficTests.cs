using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using SpyOnWheels;
using Xunit;

namespace SpyOnWheels.Tests;

/// <summary>WI-026: traffic on the open flat world of <see cref="LogicChaseTests"/> (64 px per metre, north is -y).</summary>
public class TrafficTests
{
    private const float Ppm = 64, Level = 256, Dt = 1 / 60f;
    private static readonly CarBody Body = new(300, 125);

    private sealed class Open : IDriveWorld
    {
        public Surface? SurfaceNear(Vector2 at, float heightPx) => new Surface(0, SurfaceKind.Ground);
        public bool HitsSolid(Vector2[] shape, float heightPx, out Vector2 normal) { normal = Vector2.Zero; return false; }
    }

    /// <summary>Hands out the trips it was given, in order; drives nobody on.</summary>
    private sealed class Fixed : ITrafficSource
    {
        private readonly Queue<TrafficTrip> _trips;
        public Fixed(params TrafficTrip[] trips) => _trips = new Queue<TrafficTrip>(trips);
        public int Asked { get; private set; }
        public TrafficTrip? Next(Random rng, Vector2 player, IReadOnlyList<Vector2> taken, bool initial) { Asked++; return _trips.Count > 0 ? _trips.Dequeue() : null; }
        public (Route Route, bool ToExit)? Onward(Random rng, Car car) => null;
    }

    private static float M(double metres) => (float)(metres * Ppm);

    /// <summary>A straight line along x = <paramref name="xM"/> from y = <paramref name="fromM"/> to <paramref name="toM"/> (metres, north negative).</summary>
    private static Route Line(double xM, double fromM, double toM)
    {
        int n = (int)Math.Abs(toM - fromM);
        return new Route(Enumerable.Range(0, n + 1).Select(i => new Vector2(M(xM), M(fromM + Math.Sign(toM - fromM) * i))).ToList());
    }

    private static TrafficTrip Trip(Route route, double cruiseMs, double startM = 0, bool toExit = false)
    {
        var at = route.PointAt(M(startM));
        return new TrafficTrip(route, (at, route.HeadingAt(M(startM)), 0f), Body, Handling.SportsCar, 0, Color.Blue, cruiseMs, toExit);
    }

    /// <summary>The player parked at (30, 30) m, out of everyone's way; one target on <paramref name="targetRoute"/>.</summary>
    private static Chase ChaseWith(Route targetRoute, double targetStartM = 0, Vector2? player = null)
    {
        var at = targetRoute.PointAt(M(targetStartM));
        var target = new ChaseTarget("Pickup", targetRoute, false, (at, targetRoute.HeadingAt(M(targetStartM)), 0f), Body, Handling.SportsCar);
        return new Chase(new Open(), Ppm, (player ?? new Vector2(M(30), M(30)), 0f, 0f), Body, new[] { target }, Level);
    }

    private static Car At(double xM, double yM, float heading, double northMs = 0)
    {
        var car = new Car(new Vector2(M(xM), M(yM)), heading, Ppm);
        if (northMs != 0) car.Push(new Vector2(0, (float)-northMs));
        return car;
    }

    [Fact]
    public void A_driver_sees_the_car_ahead_in_its_lane_and_not_one_behind_or_one_lane_over()
    {
        var me = At(0, 0, 0, northMs: 20);
        Assert.Null(Traffic.Ahead(me, new[] { At(0, 10, 0) }, Ppm));            // behind
        Assert.Null(Traffic.Ahead(me, new[] { At(3.6, -20, 0) }, Ppm));         // next lane
        Assert.Null(Traffic.Ahead(me, new[] { At(0, -60, 0) }, Ppm));           // beyond its look
        var ahead = Traffic.Ahead(me, new[] { At(0, -20, 0, northMs: 10) }, Ppm);
        Assert.NotNull(ahead);
        Assert.Equal(15, ahead!.Value.GapM, 3);
        Assert.InRange(ahead.Value.CapMs, 10, 21);                              // it may close, braking as the gap shrinks
        Assert.Equal(0, Traffic.Ahead(me, new[] { At(0, -10, 0) }, Ppm)!.Value.CapMs);   // inside the gap it keeps: stop
    }

    [Fact]
    public void Traffic_slows_behind_a_slower_car_and_never_hits_it()
    {
        var road = Line(-20, 0, -2000);
        var chase = ChaseWith(Line(20, 0, -3000));
        chase.UseTraffic(new Fixed(Trip(road, 8, startM: 60), Trip(road, 24, startM: 0)), 2, 1);
        double closest = double.MaxValue;
        while (chase.State == ChaseState.Running && chase.Clock < 40)
        {
            chase.Step(new Drive(false, false, 0), Dt);
            closest = Math.Min(closest, Vector2.Distance(chase.Traffic[0].Car.Position, chase.Traffic[1].Car.Position) / Ppm);
        }
        Assert.Equal(0, chase.OtherContacts);
        Assert.True(closest >= Traffic.CarLengthMetres + Traffic.KeepGapMetres - 2, $"closest {closest:0.0} m centre to centre");
        Assert.InRange(chase.Traffic[1].Car.SpeedMs, 6, 10);   // settled at the slow car's pace
    }

    [Fact]
    public void The_target_pulls_out_and_passes_a_slow_car_in_its_lane()
    {
        var route = Line(0, 0, -1500);
        var chase = ChaseWith(route);
        chase.UseTraffic(new Fixed(Trip(route, 10, startM: 60)), 1, 1);
        var slow = chase.Traffic[0];
        float widest = 0;
        while (chase.State == ChaseState.Running && chase.Clock < 20)
        {
            chase.Step(new Drive(false, false, 0), Dt);
            widest = Math.Max(widest, Math.Abs(chase.Runner.Offset));
        }
        Assert.True(chase.Target.Position.Y < slow.Car.Position.Y - M(20), "the target got well past the slow car");
        Assert.True(widest > M(2.5), $"pulled out {widest / Ppm:0.0} m");
        Assert.Equal(0, chase.OtherContacts);
        Assert.True(Math.Abs(chase.Target.Position.X - route.PointAt(chase.Runner.Along).X) < M(1), "and back in its lane after");
    }

    [Fact]
    public void A_target_spun_out_by_anything_but_the_player_is_not_stopped()
    {
        var chase = ChaseWith(Line(0, 0, -3000), targetStartM: 40);
        for (int i = 0; i < 120; i++) chase.Step(new Drive(false, false, 0), Dt);
        chase.Target.Kick(4, spinOut: true);   // as a traffic contact would
        while (chase.Target.Spinning) chase.Step(new Drive(false, false, 0), Dt);
        for (int i = 0; i < 60; i++) chase.Step(new Drive(false, false, 0), Dt);
        Assert.Equal(ChaseState.Running, chase.State);
        Assert.False(chase.Targets[0].Stopped);
        Assert.True(chase.Target.SpeedMs > 3, "it drives on");
    }

    [Fact]
    public void Driving_head_on_into_an_oncoming_traffic_car_ends_the_chase()
    {
        var chase = ChaseWith(Line(40, 0, -3000), player: new Vector2(0, 0));
        chase.UseTraffic(new Fixed(Trip(Line(0, -150, 500), 20)), 1, 1);   // coming south down x = 0, at the player
        while (chase.State == ChaseState.Running && chase.Clock < 10) chase.Step(new Drive(true, false, 0), Dt);
        Assert.Equal("HEAD-ON", chase.Outcome);
    }

    [Fact]
    public void Hitting_traffic_from_behind_costs_health_as_any_push()
    {
        var chase = ChaseWith(Line(40, 0, -3000), player: new Vector2(0, 0));
        chase.UseTraffic(new Fixed(Trip(Line(0, -30, -2000), 8)), 1, 1);
        while (chase.Contacts.Count == 0 && chase.Clock < 10) chase.Step(new Drive(true, false, 0), Dt);
        Assert.Equal(ContactKind.Rear, chase.Contacts[0].Result.Kind);
        Assert.True(chase.Health < Chase.MaxHealth);
        Assert.Equal(ChaseState.Running, chase.State);
    }

    [Fact]
    public void A_car_at_an_exit_drives_off_the_map_at_once_and_a_new_one_comes_in()
    {
        var source = new Fixed(Trip(Line(0, -10, -100), 20, toExit: true), Trip(Line(-20, -500, -900), 20));
        var chase = ChaseWith(Line(40, 0, -3000), player: new Vector2(M(10), M(-50)));   // beside its lane, in sight
        chase.UseTraffic(source, 1, 1);
        var first = chase.Traffic[0];
        while (chase.Traffic.Contains(first) && chase.Clock < 20) chase.Step(new Drive(false, false, 0), Dt);
        Assert.False(chase.Traffic.Contains(first));   // gone, though in sight
        Assert.Equal(1, chase.Replaced);
        Assert.Single(chase.Traffic);
    }

    [Fact]
    public void A_car_arriving_somewhere_in_sight_waits_and_is_not_whisked_away()
    {
        var chase = ChaseWith(Line(40, 0, -3000), player: new Vector2(M(10), M(-50)));   // beside its lane, in sight
        chase.UseTraffic(new Fixed(Trip(Line(0, -10, -100), 20)), 1, 1);
        for (int i = 0; i < 60 * 15; i++) chase.Step(new Drive(false, false, 0), Dt);
        Assert.True(chase.Traffic[0].Arrived);
        Assert.Equal(0, chase.Replaced);
    }
}

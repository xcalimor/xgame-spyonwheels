using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using SpyOnWheels;
using XGame.WorldData;
using Xunit;

namespace SpyOnWheels.Tests;

/// <summary>WI-027: a train shuttling on a straight 200 m line east-west along y = 0, on the open flat world (64 px per metre,
/// one cell a metre, north is -y).</summary>
public class TrainTests
{
    private const float Ppm = 64, Level = 256, Dt = 1 / 60f;
    private static readonly CarBody Body = new(300, 125);

    private sealed class Open : IDriveWorld
    {
        public Surface? SurfaceNear(Vector2 at, float heightPx) => new Surface(0, SurfaceKind.Ground);
        public bool HitsSolid(Vector2[] shape, float heightPx, out Vector2 normal) { normal = Vector2.Zero; return false; }
    }

    private sealed class Fixed : ITrafficSource
    {
        private readonly Queue<TrafficTrip> _trips;
        public Fixed(params TrafficTrip[] trips) => _trips = new Queue<TrafficTrip>(trips);
        public TrafficTrip? Next(Random rng, Vector2 player, IReadOnlyList<Vector2> taken, bool initial) => _trips.Count > 0 ? _trips.Dequeue() : null;
        public (Route Route, bool ToExit)? Onward(Random rng, Car car) => null;
    }

    private static float M(double metres) => (float)(metres * Ppm);

    private static Train MakeTrain()
    {
        var road = new LaneRoad { Points = new List<int[]> { new[] { -100, 0 }, new[] { 100, 0 } }, Lanes = 1, Theme = "railroads" };
        var world = new WorldFile { LaneRoads = new List<LaneRoad> { road } };
        world.Project.CellSize = 64;
        var walk = TrackWalk.Of(world, road, 3.6 * Ppm)!;
        return new Train(walk, Ppm, _ => 0f, Level);
    }

    /// <summary>A chase whose target flees far off (x = 300 m), the player at <paramref name="player"/>, and the train.</summary>
    private static Chase ChaseWithTrain(Vector2 player, float playerHeading = 0)
    {
        var route = new Route(Enumerable.Range(0, 3001).Select(i => new Vector2(M(300), -M(i))).ToList());
        var target = new ChaseTarget("Pickup", route, false, (route.PointAt(0), 0f, 0f), Body, Handling.SportsCar);
        var chase = new Chase(new Open(), Ppm, (player, playerHeading, 0f), Body, new[] { target }, Level);
        chase.UseTrains(new[] { MakeTrain() });
        return chase;
    }

    [Fact]
    public void The_train_shuttles_end_to_end_waits_and_never_leaves_its_line()
    {
        var train = MakeTrain();
        double low = double.MaxValue, high = 0;
        int ends = 0; bool was = false;
        for (double t = 0; t < 120; t += Dt)
        {
            train.Step(Dt);
            foreach (var (car, _) in train.Vehicles) { low = Math.Min(low, car.Position.X); high = Math.Max(high, car.Position.X); }
            if (train.Waiting && !was) ends++;
            was = train.Waiting;
            Assert.True(Math.Abs(train.SpeedMs) <= Train.CruiseMs + 1e-6);
        }
        Assert.True(ends >= 3, $"reached an end {ends} times");
        Assert.True(low >= -M(100) && high <= M(100), "on its line");
        Assert.True(high - low > M(150), "and over nearly all of it");
        Assert.All(train.Vehicles, v => Assert.Equal(0, v.Car.Position.Y, 1));
    }

    [Fact]
    public void The_vehicles_stay_coupled_one_behind_the_other()
    {
        var train = MakeTrain();
        for (int i = 0; i < 60 * 20; i++) train.Step(Dt);
        var xs = train.Vehicles.Select(v => v.Car.Position.X).ToList();
        for (int i = 1; i < xs.Count; i++)
        {
            double gap = Math.Abs(xs[i - 1] - xs[i]) / Ppm;
            double expected = (train.Vehicles[i - 1].Body.LengthPx + train.Vehicles[i].Body.LengthPx) / 2 / Ppm + TrainPreview.CouplingMeters;
            Assert.Equal(expected, gap, 1);
        }
    }

    [Fact]
    public void Traffic_waits_at_the_crossing_and_the_train_never_touches_it()
    {
        // A car coming north up x = 0, crossing the line at y = 0: from y = 150 m, at 20 m/s.
        var chase = ChaseWithTrain(new Vector2(M(200), M(200)));
        var road = new Route(Enumerable.Range(0, 401).Select(i => new Vector2(0, M(150 - i))).ToList());
        chase.UseTraffic(new Fixed(new TrafficTrip(road, (road.PointAt(0), 0f, 0f), Body, Handling.SportsCar, 0, Color.Blue, 20)), 1, 1);
        var car = chase.Traffic[0].Car;
        bool waited = false;
        while (chase.State == ChaseState.Running && chase.Clock < 60 && car.Position.Y > -M(50))
        {
            chase.Step(new Drive(false, false, 0), Dt);
            var blocking = chase.Trains[0].Vehicles.Any(v => Math.Abs(v.Car.Position.X) < M(12));
            if (blocking && car.Position.Y > 0 && car.SpeedMs < 1) waited = true;
        }
        Assert.Equal(0, chase.TrainContacts);
        Assert.True(car.Position.Y < -M(50), "it got across");
        Assert.True(waited, "it stood and waited while the train was on the crossing");
    }

    [Fact]
    public void Driving_into_the_train_at_speed_ends_the_chase()
    {
        var chase = ChaseWithTrain(new Vector2(M(-60), M(60)));   // south of the line, near the train's first stretch
        while (chase.State == ChaseState.Running && chase.Clock < 15) chase.Step(new Drive(true, false, 0), Dt);
        Assert.Equal("HIT BY THE TRAIN", chase.Outcome);
    }

    [Fact]
    public void Leaning_on_the_train_slowly_costs_health_and_goes_on()
    {
        var train = MakeTrain();
        var at = train.Vehicles[1].Car.Position;   // the first wagon, the train standing at its start
        var chase = ChaseWithTrain(at + new Vector2(0, M(4.5)));
        for (int i = 0; i < 40; i++) chase.Step(new Drive(true, false, 0), Dt);   // creeping up from 1 m away
        Assert.Equal(ChaseState.Running, chase.State);
        Assert.True(chase.Health < Chase.MaxHealth, $"health {chase.Health}");
    }
}

using System;
using Microsoft.Xna.Framework;
using SpyOnWheels;
using Xunit;

namespace SpyOnWheels.Tests;

/// <summary>WI-029: a climb that ends, taken fast, throws the car into the air (64 px per metre, north is -y). The ground is
/// flat to y = -60 m, rises 4 m over the next 8 m (a slope of one level over two cells), then is flat again.</summary>
public class JumpTests
{
    private const float Ppm = 64, Dt = 1 / 60f;
    private static readonly CarBody Body = new(300, 125);

    private sealed class Crest : IDriveWorld
    {
        public static double HeightM(float y) => Math.Clamp((-y / Ppm - 60) * 0.5, 0, 4);
        public Surface? SurfaceNear(Vector2 at, float heightPx) => new Surface((float)(HeightM(at.Y) * Ppm), SurfaceKind.Ground);
        public bool HitsSolid(Vector2[] shape, float heightPx, out Vector2 normal) { normal = Vector2.Zero; return false; }
    }

    /// <summary>North at <paramref name="ms"/> held by the throttle (or coasting below it), over the crest; what happened.</summary>
    private static (bool Flew, double Highest, double Flight, Car Car, float HeadingInAir, float HeadingTookOff, double LooseAfter)
        Over(double ms, float steerInAir = 0)
    {
        var car = new Car(Vector2.Zero, 0, Ppm);
        for (double t = 0; t < 8 && car.SpeedMs < ms; t += Dt) car.Step(new Drive(true, false, 0), Dt);   // up to speed on the level, far behind
        var start = (car.Position, car.Heading, car.HeightPx);
        bool flew = false; double highest = 0, flight = 0, looseAfter = 0;
        float tookOff = 0, inAir = 0;
        for (double t = 0; t < 6; t += Dt)
        {
            var drive = car.Airborne ? new Drive(true, false, steerInAir) : new Drive(car.SpeedMs < ms, false, 0);
            bool wasUp = car.Airborne;
            Driving.Step(car, drive, Dt, new Crest(), Body, Ppm, start);
            if (car.Airborne && !wasUp) tookOff = car.Heading;
            if (car.Airborne) { flew = true; flight += Dt; highest = Math.Max(highest, car.AirMetres); inAir = car.Heading; }
            if (wasUp && !car.Airborne) looseAfter = car.Loose ? 1 : 0;
        }
        return (flew, highest, flight, car, inAir, tookOff, looseAfter);
    }

    [Fact]
    public void Flat_out_over_the_crest_the_car_jumps_a_little_and_lands()
    {
        var r = Over(Car.TopSpeedMs);
        Assert.True(r.Flew, "it left the ground");
        Assert.InRange(r.Highest, 0.2, 1.0);
        Assert.InRange(r.Flight, 0.2, 0.8);
        Assert.False(r.Car.Airborne, "and came down");
        Assert.Equal(1, r.LooseAfter);   // the landing skids
    }

    [Fact]
    public void Slower_than_the_jump_speed_it_keeps_to_the_ground()
    {
        var r = Over(Car.JumpFromMs - 3);
        Assert.False(r.Flew);
    }

    [Fact]
    public void Faster_jumps_higher()
    {
        Assert.True(Over(Car.TopSpeedMs).Highest > Over(Car.JumpFromMs + 6).Highest);
    }

    [Fact]
    public void In_the_air_the_steering_does_nothing_and_landing_with_it_held_throws_the_nose()
    {
        var r = Over(Car.TopSpeedMs, steerInAir: 1);
        Assert.Equal(r.HeadingTookOff, r.HeadingInAir, 4);
        Assert.True(r.Car.YawRadS != 0 || Math.Abs(r.Car.Heading - r.HeadingInAir) > 0.05f, "the landing turned it");
    }
}

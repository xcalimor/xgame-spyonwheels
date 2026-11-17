using System;
using Microsoft.Xna.Framework;
using SpyOnWheels;
using Xunit;

namespace SpyOnWheels.Tests;

/// <summary>WI-015 D4: the car as the vision describes it, as plain simulation.</summary>
public class CarTests
{
    private const double Ppm = 64, Dt = 1 / 60.0;

    private static Car Run(Car car, Drive drive, double seconds)
    {
        for (double t = 0; t < seconds; t += Dt) car.Step(drive, Dt);
        return car;
    }

    private static Car Parked() => new(Vector2.Zero, 0f, Ppm);

    [Fact]
    public void Up_accelerates_to_the_top_speed_and_holds_it()
    {
        var car = Run(Parked(), new Drive(true, false, 0), 20);
        Assert.Equal(Car.TopSpeedMs, car.SpeedMs, 6);
    }

    [Fact]
    public void Heading_zero_drives_north_up_the_screen()
    {
        var car = Run(Parked(), new Drive(true, false, 0), 1);
        Assert.True(car.Position.Y < 0);
        Assert.Equal(0f, car.Position.X, 3);
    }

    [Fact]
    public void Down_brakes_to_a_stop_then_backs_up_slowly()
    {
        var car = Run(Parked(), new Drive(true, false, 0), 5);
        Run(car, new Drive(false, true, 0), 2);
        Assert.True(car.SpeedMs <= 0);
        Run(car, new Drive(false, true, 0), 10);
        Assert.Equal(-Car.ReverseSpeedMs, car.SpeedMs, 6);
    }

    [Fact]
    public void A_car_standing_still_does_not_turn()
    {
        var car = Run(Parked(), new Drive(false, false, 1), 2);
        Assert.Equal(0f, car.Heading);
    }

    [Fact]
    public void It_turns_quicker_slow_than_fast()
    {
        float TurnAt(double speedSeconds)
        {
            var car = Run(Parked(), new Drive(true, false, 0), speedSeconds);
            float before = car.Heading;
            car.Step(new Drive(false, false, 1), Dt);
            return car.Heading - before;
        }
        Assert.True(TurnAt(0.8) > TurnAt(10));
    }

    [Fact]
    public void Right_turns_clockwise_going_forward()
    {
        var car = Run(Parked(), new Drive(true, false, 0), 2);
        Run(car, new Drive(true, false, 1), 0.5);
        Assert.True(car.Heading > 0);
    }

    [Fact]
    public void Coasting_slows_the_car()
    {
        var car = Run(Parked(), new Drive(true, false, 0), 3);
        double v = car.SpeedMs;
        Run(car, new Drive(false, false, 0), 2);
        Assert.True(car.SpeedMs < v && car.SpeedMs > 0);
    }
}

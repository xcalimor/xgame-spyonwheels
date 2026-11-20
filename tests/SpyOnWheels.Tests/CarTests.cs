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
        Run(car, new Drive(false, true, 0), car.SpeedMs / Car.BrakeMs2 + 0.1);   // long enough to stop from any tuning
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

    // ------------------------------------------------ WI-018 grip: turning under power slides

    /// <summary>Up to <paramref name="speedMs"/> in a straight line, then <paramref name="seconds"/> of full right steering;
    /// the largest slide seen.</summary>
    private static (Car Car, double MaxSlip) TurnFrom(double speedMs, bool throttle, double seconds)
    {
        var car = Parked();
        while (car.SpeedMs < speedMs) car.Step(new Drive(true, false, 0), Dt);
        double max = 0;
        for (double t = 0; t < seconds; t += Dt)
        {
            car.Step(new Drive(throttle, false, 1), Dt);
            max = Math.Max(max, Math.Abs(car.SlipMs));
        }
        return (car, max);
    }

    [Fact]
    public void Flat_out_in_a_straight_line_never_slides()
    {
        var car = Run(Parked(), new Drive(true, false, 0), 8);
        Assert.Equal(0, car.SlipMs, 6);
    }

    [Fact]
    public void Turning_under_power_slides_more_than_coasting_through_it()
    {
        var (_, power) = TurnFrom(30, throttle: true, 1.5);
        var (_, coast) = TurnFrom(30, throttle: false, 1.5);
        Assert.True(power > 2 * coast, $"power slid {power:0.0} m/s, coasting {coast:0.0}");
        Assert.True(power > 3, $"power slid only {power:0.0} m/s: it should feel");
    }

    [Fact]
    public void Let_go_and_the_tyres_bite_again()
    {
        var (car, _) = TurnFrom(30, throttle: true, 1.5);
        Run(car, new Drive(false, false, 0), 1.5);
        Assert.True(Math.Abs(car.SlipMs) < 0.05, $"still sliding {car.SlipMs:0.00} m/s");
    }

    [Fact]
    public void A_slide_never_makes_the_car_faster_than_its_top_speed()
    {
        var (car, _) = TurnFrom(Car.TopSpeedMs - 0.1, throttle: true, 4);
        Assert.True(car.Velocity.Length() <= Car.TopSpeedMs + 1e-3, $"{car.Velocity.Length():0.00} m/s");
    }

    [Fact]
    public void Under_power_it_drifts_but_never_spins()
    {
        var car = Parked();
        while (car.SpeedMs < 35) car.Step(new Drive(true, false, 0), Dt);
        double worst = 0;
        for (double t = 0; t < 4; t += Dt)
        {
            car.Step(new Drive(true, false, 1), Dt);
            worst = Math.Max(worst, Math.Atan2(Math.Abs(car.SlipMs), Math.Abs(car.SpeedMs)) * 180 / Math.PI);
        }
        Assert.True(worst > 15, $"drifted only {worst:0} deg");
        Assert.True(worst <= 45, $"spun: {worst:0} deg");   // past 45 degrees it is a spin, whatever the tuning
    }

    [Fact]
    public void Hard_brakes_stop_from_top_speed_in_about_a_second()
    {
        // His drive (WI-018 round 1): "I need much harder brakes". 50 m/s2: 165 km/h to nothing in ~0.9 s, ~21 m.
        var car = Parked();
        while (car.SpeedMs < Car.TopSpeedMs - 0.01) car.Step(new Drive(true, false, 0), Dt);
        float y0 = car.Position.Y;
        double t = 0;
        while (car.SpeedMs > 0) { car.Step(new Drive(false, true, 0), Dt); t += Dt; }
        Assert.True(t < 1.1, $"{t:0.00} s");
        Assert.True(y0 - car.Position.Y < 25 * Ppm, $"{(y0 - car.Position.Y) / Ppm:0} m");
    }
}

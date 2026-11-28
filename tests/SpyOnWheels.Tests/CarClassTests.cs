using System;
using Microsoft.Xna.Framework;
using SpyOnWheels;
using Xunit;
using Xunit.Abstractions;

namespace SpyOnWheels.Tests;

/// <summary>WI-031: car classes handle as their kind. A sports car holds what its steering asks while its tyres grip (it
/// slides only at the limit); a pickup runs wide under power.</summary>
public class CarClassTests
{
    private const double Ppm = 64, Dt = 1 / 60.0;
    private readonly ITestOutputHelper _out;
    public CarClassTests(ITestOutputHelper output) => _out = output;

    /// <summary>The worst slide angle, degrees, in 2 s of full lock from <paramref name="kmh"/>, on or off the throttle.</summary>
    private static double FullLockSlide(Handling h, double kmh, bool power)
    {
        var car = new Car(Vector2.Zero, 0f, Ppm, h);
        while (car.SpeedMs < kmh / 3.6) car.Step(new Drive(true, false, 0), Dt);
        double worst = 0;
        for (double t = 0; t < 2; t += Dt)
        {
            car.Step(new Drive(power, false, 1), Dt);
            worst = Math.Max(worst, MathHelper.ToDegrees((float)Math.Atan2(Math.Abs(car.SlipMs), Math.Abs(car.SpeedMs))));
        }
        return worst;
    }

    [Fact]
    public void Off_the_throttle_a_sports_car_holds_full_lock_at_any_speed()
    {
        foreach (double kmh in new[] { 60.0, 100, 140, 160 })
        {
            double slide = FullLockSlide(Handling.SportsCar, kmh, power: false);
            _out.WriteLine($"full lock coasting from {kmh} km/h: sports car slides {slide:0.0} deg, pickup {FullLockSlide(Handling.Pickup, Math.Min(kmh, 145), false):0.0}");
            Assert.True(slide < 1.5, $"slid {slide:0.0} deg from {kmh} km/h");
        }
    }

    [Fact]
    public void Under_power_a_sports_car_slides_far_less_than_the_pickup()
    {
        double sports = FullLockSlide(Handling.SportsCar, 90, true), pickup = FullLockSlide(Handling.Pickup, 90, true);
        _out.WriteLine($"full lock under power from 90 km/h: sports car slides {sports:0.0} deg, pickup {pickup:0.0} deg");
        Assert.True(sports < pickup * 0.7);
    }

    [Fact]
    public void Even_a_sports_car_slides_at_the_limit()
    {
        double sports = FullLockSlide(Handling.SportsCar, 140, true);
        _out.WriteLine($"full lock under power from 140 km/h: sports car slides {sports:0.0} deg");
        Assert.InRange(sports, 5, 25);
    }
    [Fact]
    public void A_sports_car_steers_sharper_at_speed_and_keeps_more_grip_under_power()
    {
        Assert.True(Handling.SportsCar.TurnFastRad > Handling.Pickup.TurnFastRad * 1.4);
        Assert.True(Handling.SportsCar.GripPowerMs2 / Handling.SportsCar.GripMs2 > 0.75);
        Assert.True(Handling.Pickup.GripPowerMs2 / Handling.Pickup.GripMs2 <= 0.5);
    }

    [Fact]
    public void The_runner_paces_its_bends_as_it_was_tuned_whatever_the_sports_car_does()
    {
        Assert.Equal(0.9, Runner.PacedForTurnFastRad);
    }
}

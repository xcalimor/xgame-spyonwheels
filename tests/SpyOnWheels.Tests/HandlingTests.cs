using System;
using System.Linq;
using Microsoft.Xna.Framework;
using SpyOnWheels;
using Xunit;

namespace SpyOnWheels.Tests;

/// <summary>WI-022: two hero cars, each with its own handling and mass (64 px per metre, north is -y).</summary>
public class HandlingTests
{
    private const double Ppm = 64, Dt = 1 / 60.0;

    private static Car Make(Handling h, float x = 0, float y = 0, float heading = 0) => new(new Vector2(x, y), heading, Ppm, h);

    private static double Run(Car car, Drive drive, double seconds)
    {
        for (double t = 0; t < seconds; t += Dt) car.Step(drive, Dt);
        return car.SpeedMs;
    }

    [Theory]
    [InlineData("agent-car", "sports car")]
    [InlineData("Agent Car", "sports car")]
    [InlineData("pickup", "pickup")]
    [InlineData("Pickup", "pickup")]
    [InlineData(null, "sports car")]
    public void A_placed_car_gets_its_handling_by_its_theme_item(string? item, string expected) =>
        Assert.Equal(expected, Handling.For(item).Name);

    [Fact]
    public void A_car_without_a_handling_is_the_sports_car_as_tuned_with_him() =>
        Assert.Same(Handling.SportsCar, new Car(Vector2.Zero, 0, Ppm).Handling);

    [Fact]
    public void The_pickup_is_slower_slower_to_speed_longer_to_stop_and_turns_wider()
    {
        Car sports = Make(Handling.SportsCar), pickup = Make(Handling.Pickup);
        Assert.True(Run(pickup, new Drive(true, false, 0), 10) < Run(sports, new Drive(true, false, 0), 10) - 3);   // top speed (WI-028: 150 vs 165 km/h)
        Assert.Equal(Handling.Pickup.TopSpeedMs, pickup.SpeedMs, 3);

        double ToHundred(Handling h) { var c = Make(h); double t = 0; while (c.SpeedMs < 27.8) { c.Step(new Drive(true, false, 0), Dt); t += Dt; } return t; }
        Assert.True(ToHundred(Handling.Pickup) > ToHundred(Handling.SportsCar) * 1.5);

        double Stop(Handling h)
        {
            var c = Make(h);
            Run(c, new Drive(true, false, 0), 8);
            while (c.SpeedMs > 27.8) c.Step(new Drive(false, true, 0), Dt);
            var from = c.Position;
            while (c.SpeedMs > 0) c.Step(new Drive(false, true, 0), Dt);
            return Vector2.Distance(from, c.Position) / Ppm;
        }
        Assert.True(Stop(Handling.Pickup) > Stop(Handling.SportsCar) * 1.4, $"pickup {Stop(Handling.Pickup):0.0} m, sports {Stop(Handling.SportsCar):0.0} m");

        // Both at 25 m/s, full lock for half a second, coasting: the pickup's nose comes round less.
        float Turned(Handling h) { var c = Make(h); Run(c, new Drive(true, false, 0), 3); while (c.SpeedMs > 25) c.Step(new Drive(false, true, 0), Dt); Run(c, new Drive(false, false, 1), 0.5); return c.Heading; }
        Assert.True(Turned(Handling.Pickup) < Turned(Handling.SportsCar) * 0.9);
    }

    [Fact]
    public void Off_the_road_the_pickup_keeps_more_of_its_speed_than_the_sports_car()
    {
        double Share(Handling h) { var c = Make(h); c.Ground = Traction.OffRoad; return Run(c, new Drive(true, false, 0), 20) / h.TopSpeedMs; }
        Assert.True(Share(Handling.Pickup) > Share(Handling.SportsCar));
    }

    /// <summary>The plan's test: car A drives at 20 m/s straight into the back of car B standing still.</summary>
    private static (Car Attacker, Car Struck) RearEnd(Handling attacker, Handling struck)
    {
        var a = Make(attacker, 0, (float)(4.5 * Ppm));
        var b = Make(struck);
        a.Push(new Vector2(0, -20));   // its speed, set as a push
        Contact.Resolve(a, b, new Vector2(0, 1));
        return (a, b);
    }

    [Fact]
    public void The_pickup_shoves_the_sports_car_further_than_the_other_way_round()
    {
        var (_, sportsShoved) = RearEnd(Handling.Pickup, Handling.SportsCar);
        var (_, pickupShoved) = RearEnd(Handling.SportsCar, Handling.Pickup);
        Assert.True(-sportsShoved.Velocity.Y > -pickupShoved.Velocity.Y * 1.4,
            $"sports car shoved to {-sportsShoved.Velocity.Y:0.0} m/s, pickup to {-pickupShoved.Velocity.Y:0.0} m/s");
    }

    [Fact]
    public void Momentum_is_kept_in_a_contact_whatever_the_masses()
    {
        var (a, b) = RearEnd(Handling.SportsCar, Handling.Pickup);
        double before = Handling.SportsCar.MassKg * 20;
        double after = Handling.SportsCar.MassKg * -a.Velocity.Y + Handling.Pickup.MassKg * -b.Velocity.Y;
        Assert.Equal(before, after, 1);
    }

    [Fact]
    public void Equal_cars_still_share_the_push_half_and_half_as_before_mass()
    {
        var (a, b) = RearEnd(Handling.SportsCar, Handling.SportsCar);
        Assert.Equal(20 * (1 - Contact.Restitution) / 2, -a.Velocity.Y, 3);
        Assert.Equal(20 * (1 + Contact.Restitution) / 2, -b.Velocity.Y, 3);
        Assert.Equal(1, Contact.MassTurn(1400, 1400), 6);
    }

    [Fact]
    public void A_rear_hit_that_spins_a_sports_car_out_only_wobbles_a_pickup()
    {
        // Both cruising north at 20 m/s, the attacker closing at 15 m/s: past the sports car's 14, short of the pickup's ~16.
        ContactResult Hit(Handling struck)
        {
            var a = Make(Handling.SportsCar, 0, (float)(4.5 * Ppm));
            var b = Make(struck);
            a.Push(new Vector2(0, -35));
            b.Push(new Vector2(0, -20));
            return Contact.Resolve(a, b, new Vector2(0, 1))!.Value;
        }
        Assert.True(Hit(Handling.SportsCar).SpunOut);
        Assert.False(Hit(Handling.Pickup).SpunOut);
        Assert.True(Contact.MassTurn(Handling.Pickup.MassKg, Handling.SportsCar.MassKg) > 1);   // and the pickup spins the sports car more easily
    }
}

using System;
using Microsoft.Xna.Framework;
using SpyOnWheels;
using Xunit;
using Xunit.Abstractions;

namespace SpyOnWheels.Tests;

/// <summary>WI-030: the player's lively car (a V8 launch that spins the rear, full brakes that lighten it at speed) and
/// one-finger steering.</summary>
public class LivelyCarTests
{
    private const double Ppm = 64, Dt = 1 / 60.0;
    private readonly ITestOutputHelper _out;
    public LivelyCarTests(ITestOutputHelper output) => _out = output;

    private static Car Run(Car car, Drive drive, double seconds)
    {
        for (double t = 0; t < seconds - 1e-9; t += Dt) car.Step(drive, Dt);
        return car;
    }

    private static Car New(bool lively) => new(Vector2.Zero, 0f, Ppm) { Lively = lively };
    private static Car AtTop(bool lively) => Run(New(lively), new Drive(true, false, 0), 10);
    private static double Turned(Car car) => Math.Abs(MathHelper.ToDegrees(car.Heading));

    [Fact]
    public void A_straight_launch_is_as_fast_and_as_straight_as_before()
    {
        var lively = Run(New(true), new Drive(true, false, 0), 3);
        var plain = Run(New(false), new Drive(true, false, 0), 3);
        Assert.Equal(plain.SpeedMs, lively.SpeedMs, 6);
        Assert.Equal(0f, lively.Position.X, 3);
        Assert.Equal(0f, lively.Heading, 4);
    }

    [Fact]
    public void Steering_while_the_rear_spins_swings_the_tail()
    {
        // Floored from standstill, a short tap right while the rear still spins, then straight on.
        (Car Car, double MaxSlip) Launch(bool l)
        {
            var c = Run(New(l), new Drive(true, false, 0), 0.4);
            Assert.True(c.WheelspinShare > 0);
            double most = 0;
            for (int i = 0; i < 30; i++) { c.Step(new Drive(true, false, i < 12 ? 1 : 0), Dt); most = Math.Max(most, Math.Abs(c.SlipMs)); }
            return (c, most);
        }
        var (lively, livelySlip) = Launch(true);
        var (plain, plainSlip) = Launch(false);
        _out.WriteLine($"launch, tap right 0.2 s at {0.4 * Car.AccelerationMs2 * 3.6:0} km/h, 0.3 s on: lively turned {Turned(lively):0} deg, most slip {livelySlip:0.0} m/s; plain {Turned(plain):0} deg, most slip {plainSlip:0.0}");
        Assert.True(Turned(lively) > Turned(plain) * 1.35);
        Assert.True(livelySlip > plainSlip + 0.5);
    }

    [Fact]
    public void Past_the_wheelspin_a_power_turn_is_what_it_was()
    {
        var lively = Run(New(true), new Drive(true, false, 0), 2.5);
        var plain = Run(New(false), new Drive(true, false, 0), 2.5);
        Assert.Equal(0, lively.WheelspinShare);
        Run(lively, new Drive(true, false, 1), 1);
        Run(plain, new Drive(true, false, 1), 1);
        Assert.Equal(plain.Heading, lively.Heading, 4);
    }

    [Fact]
    public void Full_brakes_in_a_straight_line_stay_straight_and_stop_as_short()
    {
        var lively = AtTop(true);
        var plain = AtTop(false);
        var from = lively.Position;
        Run(lively, new Drive(false, true, 0), 1.2);
        Run(plain, new Drive(false, true, 0), 1.2);
        _out.WriteLine($"stop from {Car.TopSpeedMs * 3.6:0} km/h: {Vector2.Distance(from, lively.Position) / Ppm:0.0} m");
        Assert.Equal(plain.Position.Y, lively.Position.Y, 1);
        Assert.Equal(0f, lively.Heading, 4);
    }

    [Fact]
    public void Braking_and_steering_at_top_speed_swings_the_rear()
    {
        var lively = AtTop(true);
        var plain = AtTop(false);
        Run(lively, new Drive(false, true, 1), 0.5);
        Run(plain, new Drive(false, true, 1), 0.5);
        _out.WriteLine($"brake + full right 0.5 s from the top: lively turned {Turned(lively):0} deg, slip {lively.SlipMs:0.0} m/s; plain {Turned(plain):0} deg, slip {plain.SlipMs:0.0}");
        Assert.True(Turned(lively) > Turned(plain) * 1.2);
        Assert.True(Math.Abs(lively.SlipMs) > Math.Abs(plain.SlipMs));
    }

    [Fact]
    public void Braking_and_steering_slower_than_90_kmh_is_what_it_was()
    {
        var lively = Run(New(true), new Drive(true, false, 0), 1.4);   // ~22 m/s, under BrakeLooseFromMs
        var plain = Run(New(false), new Drive(true, false, 0), 1.4);
        Assert.True(lively.SpeedMs < Handling.SportsCar.BrakeLooseFromMs);
        Run(lively, new Drive(false, true, 1), 0.5);
        Run(plain, new Drive(false, true, 1), 0.5);
        Assert.Equal(plain.Heading, lively.Heading, 4);
    }

    [Fact]
    public void Steering_the_other_way_catches_the_slide()
    {
        Car Swung() { var c = AtTop(true); Run(c, new Drive(false, true, 1), 0.4); return c; }
        var letGo = Run(Swung(), new Drive(false, true, 0), 0.2);
        var caught = Run(Swung(), new Drive(false, true, -1), 0.2);
        Assert.True(caught.YawRadS < letGo.YawRadS);
    }

    [Fact]
    public void Computer_drivers_are_spared_the_swing_but_their_tyres_still_spin()
    {
        var car = Run(New(false), new Drive(true, false, 1), 0.3);
        Assert.True(car.WheelspinShare > 0);
        Assert.Equal(0, car.YawRadS);
    }

    [Fact]
    public void The_pickup_spins_its_tyres_for_less_of_its_speed()
    {
        Assert.True(Handling.Pickup.TopSpeedMs * Handling.Pickup.WheelspinBelowShare < Handling.SportsCar.TopSpeedMs * Handling.SportsCar.WheelspinBelowShare);
    }

    // ---- One-finger steering ----

    [Fact]
    public void A_tap_is_a_small_correction_and_a_hold_is_full_lock()
    {
        var s = new SteerInput();
        Assert.InRange(s.Update(1, Dt), 0.05f, 0.2f);
        for (int i = 0; i < 8; i++) s.Update(1, Dt);
        Assert.Equal(1f, s.Value);   // 9 frames = 0.15 s
    }

    [Fact]
    public void Letting_go_or_reversing_comes_back_faster_than_it_went_out()
    {
        var s = new SteerInput();
        for (int i = 0; i < 20; i++) s.Update(1, Dt);
        for (int i = 0; i < 4; i++) s.Update(0, Dt);
        Assert.Equal(0f, s.Value);   // 4 frames ≈ 0.067 s
        for (int i = 0; i < 20; i++) s.Update(1, Dt);
        int frames = 0;
        while (s.Value > -1f && frames < 100) { s.Update(-1, Dt); frames++; }
        Assert.InRange(frames, 10, 15);   // back through straight (~4) and out to full the other way (~9)
    }
}

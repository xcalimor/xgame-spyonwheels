using System;
using System.Linq;
using Microsoft.Xna.Framework;
using SpyOnWheels;
using Xunit;

namespace SpyOnWheels.Tests;

/// <summary>WI-021: tyre marks where the tyres work hard (launch, brakes, slide, spin-out), none cruising or on grass, capped
/// and fading.</summary>
public class TyreMarkTests
{
    private const double Ppm = 64, Dt = 1 / 60.0;
    private static readonly CarBody Body = new(4.5f * 64, 1.9f * 64);
    private static readonly Func<Vector2, float, bool> Paved = (_, _) => true, Grass = (_, _) => false;

    /// <summary>Drive <paramref name="seconds"/>, the marks watching each step (or not, to get somewhere first).</summary>
    private static void Run(Car car, Drive drive, double seconds, TyreMarks? marks = null, Func<Vector2, float, bool>? paved = null)
    {
        for (double t = 0; t < seconds; t += Dt)
        {
            car.Step(drive, Dt);
            if (marks is null) continue;
            marks.Tick(Dt);
            marks.Observe(car, Body, Ppm, paved ?? Paved);
            marks.Forget();
        }
    }

    private static Car AtTop()
    {
        var car = new Car(Vector2.Zero, 0f, Ppm);
        Run(car, new Drive(true, false, 0), 10);
        return car;
    }

    [Fact]
    public void Full_throttle_from_standstill_leaves_short_marks_behind_the_rear_wheels_only()
    {
        var car = new Car(Vector2.Zero, 0f, Ppm);
        var marks = new TyreMarks();
        Run(car, new Drive(true, false, 0), 3, marks);
        Assert.True(marks.Count > 0);
        // The car drives north (y down the screen is south): every mark lies behind where the car started its last metre,
        // and from the rear wheels, which are behind its centre at each moment: none ahead of the launch's end.
        float launchEnd = -(float)(Car.TopSpeedMs * TyreMarks.LaunchBelowShare * Car.TopSpeedMs * TyreMarks.LaunchBelowShare / (2 * Car.AccelerationMs2) * Ppm);
        Assert.All(marks.Marks, m => Assert.True(m.To.Y > launchEnd - 0.5f * Ppm));
        Assert.Equal(2, marks.Marks.GroupBy(m => m.Born).Max(g => g.Count()));   // two wheels lay a stroke each step
        // Short: the launch ends at 40 % of the top speed, well under 30 m.
        Assert.True(-launchEnd / Ppm < 30);
    }

    [Fact]
    public void Cruising_or_coasting_leaves_none()
    {
        var car = AtTop();
        var marks = new TyreMarks();
        Run(car, new Drive(true, false, 0), 2, marks);
        Run(car, new Drive(false, false, 0), 2, marks);
        Run(car, new Drive(false, false, 0.3f), 1, marks);   // a gentle bend held by the tyres
        Assert.Equal(0, marks.Count);
    }

    [Fact]
    public void Braking_at_speed_marks_all_four_wheels()
    {
        var car = AtTop();
        var marks = new TyreMarks();
        Run(car, new Drive(false, true, 0), 1.5, marks);
        Assert.True(marks.Count > 0);
        Assert.Equal(4, marks.Marks.GroupBy(m => m.Born).Max(g => g.Count()));   // all four wheels
        Assert.All(marks.Marks, m => Assert.Equal(TyreMarks.StrengthOf(TyreWork.Brake), m.Strength));
    }

    [Fact]
    public void A_power_drift_leaves_a_sideways_smear()
    {
        var car = AtTop();
        var marks = new TyreMarks();
        Run(car, new Drive(true, false, 1), 1.5, marks);
        Assert.True(Math.Abs(car.SlipMs) > TyreMarks.SlideFromMs);
        Assert.Contains(marks.Marks, m => m.Strength == TyreMarks.StrengthOf(TyreWork.Slide));
    }

    [Fact]
    public void A_spin_out_marks_until_the_car_has_skidded_to_a_stop()
    {
        var car = AtTop();
        car.Kick(4, spinOut: true);
        var marks = new TyreMarks();
        Run(car, new Drive(false, false, 0), 0.5, marks);
        Assert.True(car.Spinning);
        Assert.True(marks.Count > 0);
        Assert.All(marks.Marks, m => Assert.Equal(TyreMarks.StrengthOf(TyreWork.Slide), m.Strength));
    }

    [Fact]
    public void Grass_takes_no_marks()
    {
        var car = AtTop();
        var marks = new TyreMarks();
        Run(car, new Drive(false, true, 0), 1.5, marks, Grass);
        Assert.Equal(0, marks.Count);
    }

    [Fact]
    public void Calm_traffic_does_not_mark_its_braking_but_does_its_slide()
    {
        var car = AtTop();
        var marks = new TyreMarks();
        Run(car, new Drive(false, true, 0), 0.3);
        Assert.Null(TyreMarks.Working(car, calm: true));
        Assert.Equal(TyreWork.Brake, TyreMarks.Working(car));
        car.Kick(4, spinOut: true);
        car.Step(new Drive(false, false, 0), Dt);
        Assert.Equal(TyreWork.Slide, TyreMarks.Working(car, calm: true));
    }

    [Fact]
    public void The_cap_holds_and_the_oldest_go_first()
    {
        var car = new Car(Vector2.Zero, 0f, Ppm);
        var marks = new TyreMarks(cap: 100);
        Run(car, new Drive(true, false, 0), 3, marks);   // a launch: far more than 100 strokes
        var first = marks.Marks.First();
        Run(car, new Drive(false, true, 0), 1, marks);
        Assert.Equal(100, marks.Count);
        Assert.True(marks.Marks.First().Born > first.Born);
        Assert.True(marks.Marks.Zip(marks.Marks.Skip(1)).All(p => p.First.Born <= p.Second.Born));
    }

    [Fact]
    public void Marks_hold_then_fade_and_are_gone_after_their_life()
    {
        var car = new Car(Vector2.Zero, 0f, Ppm);
        var marks = new TyreMarks();
        Run(car, new Drive(true, false, 0), 1, marks);
        var m = marks.Marks.Last();
        marks.Tick(TyreMarks.FadeSeconds * TyreMarks.HoldShare - 2);
        Assert.Equal(1f, marks.Alpha(m));
        marks.Tick(TyreMarks.FadeSeconds * (1 - TyreMarks.HoldShare) / 2);
        Assert.InRange(marks.Alpha(m), 0.3f, 0.7f);
        marks.Tick(TyreMarks.FadeSeconds);
        Assert.Equal(0, marks.Count);
    }

    [Fact]
    public void A_car_in_the_air_leaves_no_marks()
    {
        var car = AtTop();
        // In the air nothing the driver does reaches the tyres: launched over a crest, the brakes held mark nothing.
        car.FollowGround(2.5 * Dt, Dt);   // climbing fast...
        car.FollowGround(0, Dt);          // ...and the climb ends: off the ground
        Assert.True(car.Airborne);
        car.Step(new Drive(false, true, 0), Dt);
        Assert.Null(TyreMarks.Working(car));
    }
}

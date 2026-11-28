using System;

namespace SpyOnWheels;

/// <summary>
/// **A car type's handling (WI-022, his ask: "two types of hero cars, speedy sports car and the pickup").** Each type drives
/// the same simulation with its own numbers: top speed, acceleration, brakes, grip, turning, how much the ground off the road
/// holds it back, and **mass**, which decides who shoves whom in a contact (<see cref="Contact"/>). Every number is a first
/// guess, to be found in play (vision §11).
/// </summary>
public sealed record Handling(
    string Name,
    double TopSpeedMs,
    double AccelerationMs2,
    double BrakeMs2,
    double GripMs2,
    double GripPowerMs2,
    double TurnSlowRad,
    double TurnFastRad,
    double OffRoadDragScale,
    double MassKg,
    double WheelspinBelowShare = 0.4,   // WI-030: full throttle below this share of the top speed spins the rear tyres
    double BrakeLooseFromMs = 25)       // WI-030: braking faster than this (90 km/h) lightens the rear: steering then swings it
{
    /// <summary>The Agent Car: the handling WI-016 to WI-020 tuned with him (Car's constants).</summary>
    public static readonly Handling SportsCar = new("sports car",
        Car.TopSpeedMs, Car.AccelerationMs2, Car.BrakeMs2, Car.GripMs2, Car.GripPowerMs2, Car.TurnSlowRad, Car.TurnFastRad,
        OffRoadDragScale: 1, MassKg: 1400);

    /// <summary>The pickup: slower, softer, longer to stop, and heavier. Built for rough ground, so off the road it loses
    /// less: flat out on grass ~126 km/h of its ~150 (83 %), where the sports car keeps ~130 of 165 (78 %).</summary>
    public static readonly Handling Pickup = new("pickup",
        TopSpeedMs: 42,            // ~150 km/h (WI-028: the Runner cruises at 37 on the straight; was 38)
        AccelerationMs2: 9,        // 0-100 km/h in ~3 s, the sports car's ~1.7 s
        BrakeMs2: 30,              // from 100 km/h ~13 m to stop, the sports car's ~8 m
        GripMs2: 28,
        GripPowerMs2: 14,
        TurnSlowRad: 2.1,
        TurnFastRad: 0.7,
        OffRoadDragScale: 0.58,    // flat out off the road: 9 / (0.444 x 0.58) ≈ 35 m/s (~126 km/h, 83 % of its top; WI-028 top 42)
        MassKg: 2300,
        WheelspinBelowShare: 0.25, // less power for its weight: a shorter spin
        BrakeLooseFromMs: 22);     // softer and taller: it squirms from a bit lower

    /// <summary>The handling for a placed car, by its theme item id or its name ("pickup", "Pickup"); every other car is
    /// the sports car (decided by AI: the Vehicles theme holds two cars today).</summary>
    public static Handling For(string? itemOrName) =>
        itemOrName is not null && itemOrName.Contains("pickup", StringComparison.OrdinalIgnoreCase) ? Pickup : SportsCar;
}

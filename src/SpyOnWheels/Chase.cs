using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace SpyOnWheels;

public enum ChaseState { Running, Won, Lost }

/// <summary>
/// **The Stop task (WI-020, the vision's first playable slice):** a target flees along the route toward a border; the player
/// must stop it before it gets there. Stopped means spun out to a standstill (D5). The player loses when the target crosses
/// the border, when health runs out, on a frontal hit, or in the water. Plain simulation: no window, no drawing, so the
/// whole chase runs in a test.
/// </summary>
public sealed class Chase
{
    public const double MaxHealth = 100;
    public const double TargetLeadMetres = 35;      // the target's head start along the route
    public const double BorderBeforeEndMetres = 40; // the border lies across the route this far before its end (D4)
    public const double ContactCooldownS = 0.35;    // one touch is one contact, not one a frame

    private readonly IDriveWorld _world;
    private readonly double _ppm;
    private readonly float _levelTolerancePx;
    private double _cooldown;

    public Chase(Route route, IDriveWorld world, double pxPerMetre, CarBody playerBody, CarBody targetBody,
        Func<Vector2, float> heightAt, float levelPx)
    {
        Route = route;
        _world = world;
        _ppm = pxPerMetre;
        PlayerBody = playerBody;
        TargetBody = targetBody;
        _levelTolerancePx = DriveWorld.StepLevels * levelPx;
        BorderAlong = (float)Math.Max(route.Length * 0.5, route.Length - BorderBeforeEndMetres * pxPerMetre);

        float startAlong = (float)Math.Min(route.Length * 0.05, 2 * pxPerMetre);
        var at = route.PointAt(startAlong);
        PlayerStart = (at, route.HeadingAt(startAlong), heightAt(at));
        float targetAlong = (float)Math.Min(route.Length * 0.3, startAlong + TargetLeadMetres * pxPerMetre);
        var tat = route.PointAt(targetAlong);
        TargetStart = (tat, route.HeadingAt(targetAlong), heightAt(tat));
        Player = new Car(PlayerStart.At, PlayerStart.Heading, pxPerMetre) { HeightPx = PlayerStart.HeightPx };
        Target = new Car(TargetStart.At, TargetStart.Heading, pxPerMetre) { HeightPx = TargetStart.HeightPx };
        Runner = new Runner(route, pxPerMetre, targetAlong);
    }

    public Route Route { get; }
    public Car Player { get; }
    public Car Target { get; }
    public Runner Runner { get; }
    public CarBody PlayerBody { get; }
    public CarBody TargetBody { get; }
    public (Vector2 At, float Heading, float HeightPx) PlayerStart { get; }
    public (Vector2 At, float Heading, float HeightPx) TargetStart { get; }

    /// <summary>Where the border lies along the route, px.</summary>
    public float BorderAlong { get; }

    public double Health { get; private set; } = MaxHealth;
    public ChaseState State { get; private set; }

    /// <summary>Why it ended, in the player's words: "STOPPED", "IT GOT AWAY", "HEAD-ON", "WRECKED", "SANK".</summary>
    public string? Outcome { get; private set; }

    public double Clock { get; private set; }

    /// <summary>Every contact so far, for the HUD and the report.</summary>
    public List<(double At, ContactResult Result)> Contacts { get; } = new();

    /// <summary>The target has been spun out at least once and is still spinning.</summary>
    public bool TargetSpinning => Target.Spinning;

    /// <summary>Metres from the player to the target.</summary>
    public double GapMetres => Vector2.Distance(Player.Position, Target.Position) / _ppm;

    public void Step(Drive drive, double dt)
    {
        if (State != ChaseState.Running || dt <= 0) return;
        Clock += dt;
        _cooldown = Math.Max(0, _cooldown - dt);

        bool targetWasSpinning = Target.Spinning;
        var playerEvent = Driving.Step(Player, drive, dt, _world, PlayerBody, _ppm, PlayerStart);
        Driving.Step(Target, Runner.DriveFor(Target), dt, _world, TargetBody, _ppm, TargetStart);

        if (playerEvent == DriveEvent.Sank) { End(ChaseState.Lost, "SANK"); return; }

        // Car meets car: only at the same height (one on a deck, one under it, never touch).
        if (Math.Abs(Player.HeightPx - Target.HeightPx) <= _levelTolerancePx
            && Geometry.Overlap(PlayerBody.Shape(Player.Position, Player.Heading), TargetBody.Shape(Target.Position, Target.Heading), out var normal, out float depth))
        {
            // Apart again, half each, along the contact line (normal: target → player).
            Player.MoveTo(Player.Position + normal * (depth / 2 + 1));
            Target.MoveTo(Target.Position - normal * (depth / 2 + 1));
            if (_cooldown <= 0 && Contact.Resolve(Player, Target, normal) is { } hit)
            {
                _cooldown = ContactCooldownS;
                Contacts.Add((Clock, hit));
                Health = Math.Max(0, Health - hit.Damage);
                if (hit.Kind == ContactKind.Frontal) { End(ChaseState.Lost, "HEAD-ON"); return; }
                if (Health <= 0) { End(ChaseState.Lost, "WRECKED"); return; }
            }
        }

        // Stopped: it was spun out and has skidded to a standstill (D5).
        if (targetWasSpinning && !Target.Spinning) { End(ChaseState.Won, "STOPPED"); return; }
        if (Runner.Along >= BorderAlong) End(ChaseState.Lost, "IT GOT AWAY");
    }

    private void End(ChaseState state, string outcome)
    {
        State = state;
        Outcome = outcome;
    }
}

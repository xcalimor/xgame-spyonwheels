using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace SpyOnWheels;

public enum ChaseState { Running, Won, Lost }

/// <summary>A car the player must stop, as a chase is given it: its name (the HUD's), its route, whether the route loops (no
/// end to get away to), where it starts and its box.</summary>
public sealed record ChaseTarget(string Name, Route Route, bool Loop, (Vector2 At, float Heading, float HeightPx) Start, CarBody Body);

/// <summary>One fleeing car in a running chase: the car, its driver, its border, and whether it has been stopped.</summary>
public sealed class Fugitive
{
    internal Fugitive(ChaseTarget target, Car car, Runner runner, float borderAlong)
    {
        Name = target.Name; Route = target.Route; Loop = target.Loop; Start = target.Start; Body = target.Body;
        Car = car; Runner = runner; BorderAlong = borderAlong;
    }

    public string Name { get; }
    public Route Route { get; }
    public bool Loop { get; }
    public (Vector2 At, float Heading, float HeightPx) Start { get; }
    public CarBody Body { get; }
    public Car Car { get; }
    public Runner Runner { get; }
    /// <summary>Where its border lies along its route, px; infinite on a loop (it never gets away).</summary>
    public float BorderAlong { get; }
    /// <summary>Spun out to a standstill once: it stays where it stopped, braking (D5).</summary>
    public bool Stopped { get; internal set; }
    /// <summary>Metres still to its border (infinite on a loop).</summary>
    public double ToBorderPx => BorderAlong - Runner.Along;
}

/// <summary>
/// **The Stop task (WI-020, the vision's first playable slice):** targets flee along their routes toward a border; the player
/// must stop them before they get there. Stopped means spun out to a standstill (D5). The player loses when a target crosses
/// its border, when health runs out, on a frontal hit, or in the water. Plain simulation: no window, no drawing, so the
/// whole chase runs in a test.
///
/// <para>**FEAT-009 S4: a stage's logic gives the chase its cars**: the player starts where the Hero was placed, and every
/// Runner with a route is a target, starting where it was placed. The player wins when EVERY target is stopped and loses when
/// ANY gets away. A stopped target stays where it stopped. Targets do not touch each other (decided by AI: one Runner per
/// stage today; overturn when two meet).</para>
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

    /// <summary>WI-020: the stage's one route: the player near its start, the target ahead on it.</summary>
    public Chase(Route route, IDriveWorld world, double pxPerMetre, CarBody playerBody, CarBody targetBody,
        Func<Vector2, float> heightAt, float levelPx)
    {
        _world = world;
        _ppm = pxPerMetre;
        PlayerBody = playerBody;
        _levelTolerancePx = DriveWorld.StepLevels * levelPx;

        float startAlong = (float)Math.Min(route.Length * 0.05, 2 * pxPerMetre);
        var at = route.PointAt(startAlong);
        PlayerStart = (at, route.HeadingAt(startAlong), heightAt(at));
        float targetAlong = (float)Math.Min(route.Length * 0.3, startAlong + TargetLeadMetres * pxPerMetre);
        var tat = route.PointAt(targetAlong);
        Player = new Car(PlayerStart.At, PlayerStart.Heading, pxPerMetre) { HeightPx = PlayerStart.HeightPx };
        Targets = new List<Fugitive> { Make(new ChaseTarget(SpyGame.TargetAssetName, route, false, (tat, route.HeadingAt(targetAlong), heightAt(tat)), targetBody), targetAlong) };
    }

    /// <summary>FEAT-009 S4: the player at <paramref name="playerStart"/>, each target at its own start on its own route.</summary>
    public Chase(IDriveWorld world, double pxPerMetre, (Vector2 At, float Heading, float HeightPx) playerStart, CarBody playerBody,
        IReadOnlyList<ChaseTarget> targets, float levelPx)
    {
        if (targets.Count == 0) throw new ArgumentException("a chase has at least one target", nameof(targets));
        _world = world;
        _ppm = pxPerMetre;
        PlayerBody = playerBody;
        _levelTolerancePx = DriveWorld.StepLevels * levelPx;
        PlayerStart = playerStart;
        Player = new Car(playerStart.At, playerStart.Heading, pxPerMetre) { HeightPx = playerStart.HeightPx };
        Targets = targets.Select(t => Make(t, t.Route.Project(t.Start.At))).ToList();
    }

    private Fugitive Make(ChaseTarget t, float along)
    {
        var car = new Car(t.Start.At, t.Start.Heading, _ppm) { HeightPx = t.Start.HeightPx };
        float border = t.Loop ? float.PositiveInfinity : (float)Math.Max(t.Route.Length * 0.5, t.Route.Length - BorderBeforeEndMetres * _ppm);
        return new Fugitive(t, car, new Runner(t.Route, _ppm, along), border);
    }

    public IReadOnlyList<Fugitive> Targets { get; }

    // The first target, as WI-020's one-target chase named it.
    public Route Route => Targets[0].Route;
    public Car Target => Targets[0].Car;
    public Runner Runner => Targets[0].Runner;
    public CarBody TargetBody => Targets[0].Body;
    public (Vector2 At, float Heading, float HeightPx) TargetStart => Targets[0].Start;
    /// <summary>Where the first target's border lies along its route, px.</summary>
    public float BorderAlong => Targets[0].BorderAlong;

    public Car Player { get; }
    public CarBody PlayerBody { get; }
    public (Vector2 At, float Heading, float HeightPx) PlayerStart { get; }

    public double Health { get; private set; } = MaxHealth;
    public ChaseState State { get; private set; }

    /// <summary>Why it ended, in the player's words: "STOPPED", "IT GOT AWAY", "HEAD-ON", "WRECKED", "SANK".</summary>
    public string? Outcome { get; private set; }

    public double Clock { get; private set; }

    /// <summary>Every contact so far, for the HUD and the report.</summary>
    public List<(double At, ContactResult Result)> Contacts { get; } = new();

    /// <summary>The first target has been spun out and is still spinning.</summary>
    public bool TargetSpinning => Target.Spinning;

    /// <summary>The targets still fleeing.</summary>
    public IEnumerable<Fugitive> Fleeing => Targets.Where(t => !t.Stopped);

    /// <summary>Metres from the player to the nearest target still fleeing (to the first, once all are stopped).</summary>
    public double GapMetres => (Fleeing.Select(t => (double?)Vector2.Distance(Player.Position, t.Car.Position)).Min()
        ?? Vector2.Distance(Player.Position, Target.Position)) / _ppm;

    public void Step(Drive drive, double dt)
    {
        if (State != ChaseState.Running || dt <= 0) return;
        Clock += dt;
        _cooldown = Math.Max(0, _cooldown - dt);

        var wasSpinning = Targets.Select(t => t.Car.Spinning).ToArray();
        var playerEvent = Driving.Step(Player, drive, dt, _world, PlayerBody, _ppm, PlayerStart);
        foreach (var t in Targets)
            Driving.Step(t.Car, t.Stopped ? new Drive(false, true, 0f) : t.Runner.DriveFor(t.Car), dt, _world, t.Body, _ppm, t.Start);

        if (playerEvent == DriveEvent.Sank) { End(ChaseState.Lost, "SANK"); return; }

        // Car meets car: only at the same height (one on a deck, one under it, never touch).
        foreach (var t in Targets)
        {
            if (Math.Abs(Player.HeightPx - t.Car.HeightPx) > _levelTolerancePx
                || !Geometry.Overlap(PlayerBody.Shape(Player.Position, Player.Heading), t.Body.Shape(t.Car.Position, t.Car.Heading), out var normal, out float depth))
                continue;
            Part(t, normal, depth + 2);
            if (_cooldown <= 0 && Contact.Resolve(Player, t.Car, normal) is { } hit)
            {
                _cooldown = ContactCooldownS;
                Contacts.Add((Clock, hit));
                Health = Math.Max(0, Health - hit.Damage);
                if (hit.Kind == ContactKind.Frontal) { End(ChaseState.Lost, "HEAD-ON"); return; }
                if (Health <= 0) { End(ChaseState.Lost, "WRECKED"); return; }
            }
        }

        // Stopped: it was spun out and has skidded to a standstill (D5). All stopped wins.
        for (int i = 0; i < Targets.Count; i++)
            if (wasSpinning[i] && !Targets[i].Car.Spinning) Targets[i].Stopped = true;
        if (Targets.All(t => t.Stopped)) { End(ChaseState.Won, "STOPPED"); return; }
        if (Fleeing.Any(t => t.Runner.Along >= t.BorderAlong)) End(ChaseState.Lost, "IT GOT AWAY");
    }

    /// <summary>
    /// The two cars apart again along the contact line (<paramref name="normal"/>: target → player), half each, but only into
    /// room the car fits in: a car parted into a wall or over an edge would be "caught", and the driving lets a caught car
    /// move freely through everything until it is out (his first drive: the car went through everything). A car with no
    /// room stays put and the other takes the whole gap; with room for neither, the push of the contact parts them.
    /// </summary>
    private void Part(Fugitive target, Vector2 normal, float gapPx)
    {
        bool TryMove(Car car, CarBody body, Vector2 by)
        {
            if (!Driving.Fits(car.Position + by, car.Heading, car.HeightPx, _world, body, out float h, out _)) return false;
            car.MoveTo(car.Position + by);
            car.HeightPx = h;
            return true;
        }
        bool playerMoved = TryMove(Player, PlayerBody, normal * (gapPx / 2));
        bool targetMoved = TryMove(target.Car, target.Body, -normal * (gapPx / 2));
        if (!playerMoved && targetMoved) TryMove(target.Car, target.Body, -normal * (gapPx / 2));
        else if (playerMoved && !targetMoved) TryMove(Player, PlayerBody, normal * (gapPx / 2));
    }

    private void End(ChaseState state, string outcome)
    {
        State = state;
        Outcome = outcome;
    }
}

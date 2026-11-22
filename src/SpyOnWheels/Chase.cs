using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace SpyOnWheels;

public enum ChaseState { Running, Won, Lost }

/// <summary>A car the player must stop, as a chase is given it: its name (the HUD's), its route, whether the route loops (no
/// end to get away to), where it starts, its box, and its handling (WI-022; by its name when not given).</summary>
public sealed record ChaseTarget(string Name, Route Route, bool Loop, (Vector2 At, float Heading, float HeightPx) Start, CarBody Body, Handling? Handling = null);

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
    /// <summary>WI-026: its spin now was the player's doing. A spin from traffic does not stop it: it recovers and flees on.</summary>
    public bool SpunByPlayer { get; internal set; }
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
///
/// <para>**WI-026: traffic** (<see cref="UseTraffic"/>): innocent cars drive lane trips in both directions. Every car touches
/// every car. The player hitting traffic costs health as any push does, and a head-on with it ends the chase as with a
/// target. Contacts without the player cost no health. A target spun out by traffic is not stopped: it recovers and flees.</para>
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
        Func<Vector2, float> heightAt, float levelPx, Handling? playerHandling = null)
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
        Player = new Car(PlayerStart.At, PlayerStart.Heading, pxPerMetre, playerHandling) { HeightPx = PlayerStart.HeightPx };
        Targets = new List<Fugitive> { Make(new ChaseTarget(SpyGame.TargetAssetName, route, false, (tat, route.HeadingAt(targetAlong), heightAt(tat)), targetBody), targetAlong) };
    }

    /// <summary>FEAT-009 S4: the player at <paramref name="playerStart"/>, each target at its own start on its own route.</summary>
    public Chase(IDriveWorld world, double pxPerMetre, (Vector2 At, float Heading, float HeightPx) playerStart, CarBody playerBody,
        IReadOnlyList<ChaseTarget> targets, float levelPx, Handling? playerHandling = null)
    {
        if (targets.Count == 0) throw new ArgumentException("a chase has at least one target", nameof(targets));
        _world = world;
        _ppm = pxPerMetre;
        PlayerBody = playerBody;
        _levelTolerancePx = DriveWorld.StepLevels * levelPx;
        PlayerStart = playerStart;
        Player = new Car(playerStart.At, playerStart.Heading, pxPerMetre, playerHandling) { HeightPx = playerStart.HeightPx };
        Targets = targets.Select(t => Make(t, t.Route.Project(t.Start.At))).ToList();
    }

    private Fugitive Make(ChaseTarget t, float along)
    {
        var car = new Car(t.Start.At, t.Start.Heading, _ppm, t.Handling ?? Handling.For(t.Name)) { HeightPx = t.Start.HeightPx };
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

    // ---- WI-026 traffic ----

    private ITrafficSource? _traffic;
    private Random _rng = new(0);

    /// <summary>The traffic on the road now.</summary>
    public List<Traveller> Traffic { get; } = new();

    /// <summary>Contacts between cars the player was not in (traffic with traffic, a target with traffic): for the report.</summary>
    public int OtherContacts { get; private set; }

    /// <summary>Traffic cars taken away and put back elsewhere, out of sight.</summary>
    public int Replaced { get; private set; }

    /// <summary>Fill the roads with <paramref name="cars"/> traffic cars from <paramref name="source"/>, away from the player
    /// and the targets; the same seed gives the same traffic.</summary>
    public void UseTraffic(ITrafficSource source, int cars, int seed)
    {
        _traffic = source;
        _rng = new Random(seed);
        Traffic.Clear();
        for (int i = 0; i < cars; i++)
            if (source.Next(_rng, Player.Position, Taken(), initial: true) is { } trip) Traffic.Add(new Traveller(trip, _ppm));
    }

    private List<Vector2> Taken()
    {
        var taken = new List<Vector2> { Player.Position };
        taken.AddRange(Targets.Select(t => t.Car.Position));
        taken.AddRange(Traffic.Select(t => t.Car.Position));
        return taken;
    }

    private IEnumerable<Car> AllCars() => new[] { Player }.Concat(Targets.Select(t => t.Car)).Concat(Traffic.Select(t => t.Car));

    public void Step(Drive drive, double dt)
    {
        if (State != ChaseState.Running || dt <= 0) return;
        Clock += dt;
        _cooldown = Math.Max(0, _cooldown - dt);

        var wasSpinning = Targets.Select(t => t.Car.Spinning).ToArray();
        var cars = AllCars().ToList();
        var playerEvent = Driving.Step(Player, drive, dt, _world, PlayerBody, _ppm, PlayerStart);
        foreach (var t in Targets)
        {
            var intent = t.Stopped ? new Drive(false, true, 0f)
                : Traffic.Count == 0 ? t.Runner.DriveFor(t.Car)
                : Pursued(t, cars, dt);
            Driving.Step(t.Car, intent, dt, _world, t.Body, _ppm, t.Start);
        }
        foreach (var v in Traffic.ToList())
        {
            double cap = Math.Min(v.EndCapMs, SpyOnWheels.Traffic.Ahead(v.Car, cars, _ppm) is { } a ? a.CapMs : double.MaxValue);   // stops at its trip's end
            var ev = Driving.Step(v.Car, v.Runner.DriveFor(v.Car, cap, 0, dt), dt, _world, v.Body, _ppm, v.Trip.Start);
            v.StillFor = Math.Abs(v.Car.SpeedMs) < 1 ? v.StillFor + dt : 0;
            if (ev == DriveEvent.Sank) Replace(v, force: true);
        }

        if (playerEvent == DriveEvent.Sank) { End(ChaseState.Lost, "SANK"); return; }

        // Car meets car: only at the same height (one on a deck, one under it, never touch). The player first.
        foreach (var (car, body, target) in Others())
        {
            if (!Touch(Player, PlayerBody, car, body, out var normal, out float depth)) continue;
            Part(Player, PlayerBody, car, body, normal, depth + 2);
            if (_cooldown <= 0 && Contact.Resolve(Player, car, normal) is { } hit)
            {
                _cooldown = ContactCooldownS;
                Contacts.Add((Clock, hit));
                if (target is not null && hit.SpunOut) target.SpunByPlayer = true;
                Health = Math.Max(0, Health - hit.Damage);
                if (hit.Kind == ContactKind.Frontal) { End(ChaseState.Lost, "HEAD-ON"); return; }
                if (Health <= 0) { End(ChaseState.Lost, "WRECKED"); return; }
            }
        }
        // Then every other pair: pushed and turned as any contact, no health to lose.
        var others = Others().ToList();
        for (int i = 0; i < others.Count; i++)
            for (int j = i + 1; j < others.Count; j++)
            {
                var (a, ab, ta) = others[i];
                var (b, bb, tb) = others[j];
                if (ta is not null && tb is not null) continue;   // targets do not touch each other (S4)
                if (!Touch(a, ab, b, bb, out var normal, out float depth)) continue;   // normal: b to a
                Part(a, ab, b, bb, normal, depth + 2);
                if (Contact.Resolve(a, b, normal) is not null) OtherContacts++;
            }

        // Stopped: spun out BY THE PLAYER and skidded to a standstill (D5, WI-026). All stopped wins.
        for (int i = 0; i < Targets.Count; i++)
            if (wasSpinning[i] && !Targets[i].Car.Spinning)
            {
                if (Targets[i].SpunByPlayer) Targets[i].Stopped = true;
                Targets[i].SpunByPlayer = false;
            }
        if (Targets.All(t => t.Stopped)) { End(ChaseState.Won, "STOPPED"); return; }
        if (Fleeing.Any(t => t.Runner.Along >= t.BorderAlong)) { End(ChaseState.Lost, "IT GOT AWAY"); return; }

        // Traffic at its trip's end, or stuck, is taken away once out of sight and a new car comes in elsewhere.
        foreach (var v in Traffic.ToList())
        {
            if (v.Arrived && !v.Trip.ToExit && _traffic?.Onward(_rng, v.Car) is { } way) { v.DriveOn(way.Route, way.ToExit, _ppm); continue; }
            if (v.Arrived || v.StillFor > SpyOnWheels.Traffic.StuckSeconds) Replace(v, force: v.Arrived && v.Trip.ToExit);   // off the map: gone at once
        }
    }

    /// <summary>The fleeing target's intent among traffic: out to pass a slower car, and braking only when about to hit one
    /// in the lane it is driving.</summary>
    private Drive Pursued(Fugitive t, List<Car> cars, double dt)
    {
        var others = cars.Where(c => !ReferenceEquals(c, Player)).ToList();
        double aside = SpyOnWheels.Traffic.PassAside(t.Car, t.Runner, others, _ppm);
        double? cap = SpyOnWheels.Traffic.Ahead(t.Car, others, _ppm) is { GapM: < 10 } a ? a.CapMs : null;
        return t.Runner.DriveFor(t.Car, cap, (float)(aside * _ppm), dt);
    }

    private void Replace(Traveller v, bool force)
    {
        if (!force && Vector2.Distance(v.Car.Position, Player.Position) < SpyOnWheels.Traffic.OutOfSightMetres * _ppm) return;
        Traffic.Remove(v);
        if (_traffic?.Next(_rng, Player.Position, Taken(), initial: false) is { } trip) Traffic.Add(new Traveller(trip, _ppm));
        Replaced++;
    }

    /// <summary>Every car but the player's: the targets (with their fugitive), then the traffic.</summary>
    private IEnumerable<(Car Car, CarBody Body, Fugitive? Target)> Others() =>
        Targets.Select(t => (t.Car, t.Body, (Fugitive?)t)).Concat(Traffic.Select(v => (v.Car, v.Body, (Fugitive?)null)));

    private bool Touch(Car a, CarBody ab, Car b, CarBody bb, out Vector2 normal, out float depth)
    {
        normal = Vector2.Zero; depth = 0;
        if (Math.Abs(a.HeightPx - b.HeightPx) > _levelTolerancePx) return false;
        // Far apart: no need to test the boxes.
        float reach = Math.Max(ab.LengthPx, ab.WidthPx) + Math.Max(bb.LengthPx, bb.WidthPx);
        if (Vector2.DistanceSquared(a.Position, b.Position) > reach * reach) return false;
        return Geometry.Overlap(ab.Shape(a.Position, a.Heading), bb.Shape(b.Position, b.Heading), out normal, out depth);
    }

    /// <summary>
    /// The two cars apart again along the contact line (<paramref name="normal"/>: b to a), half each, but only into room the
    /// car fits in: a car parted into a wall or over an edge would be "caught", and the driving lets a caught car move freely
    /// through everything until it is out (his first drive: the car went through everything). A car with no room stays put
    /// and the other takes the whole gap; with room for neither, the push of the contact parts them.
    /// </summary>
    private void Part(Car a, CarBody ab, Car b, CarBody bb, Vector2 normal, float gapPx)
    {
        bool TryMove(Car car, CarBody body, Vector2 by)
        {
            if (!Driving.Fits(car.Position + by, car.Heading, car.HeightPx, _world, body, out float h, out _)) return false;
            car.MoveTo(car.Position + by);
            car.HeightPx = h;
            return true;
        }
        bool aMoved = TryMove(a, ab, normal * (gapPx / 2));
        bool bMoved = TryMove(b, bb, -normal * (gapPx / 2));
        if (!aMoved && bMoved) TryMove(b, bb, -normal * (gapPx / 2));
        else if (aMoved && !bMoved) TryMove(a, ab, normal * (gapPx / 2));
    }

    private void End(ChaseState state, string outcome)
    {
        State = state;
        Outcome = outcome;
    }
}

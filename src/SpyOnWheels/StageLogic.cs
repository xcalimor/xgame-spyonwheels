using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using XGame.WorldData;
using XGame.WorldScene;

namespace SpyOnWheels;

/// <summary>A placed vehicle the game drives: its id, its name (the theme item's), its picture as the scene found it, its box,
/// and its handling by its theme item (WI-022).</summary>
public sealed record StageCar(string Id, string Name, PlacedPicture Picture, CarBody Body, Handling Handling)
{
    /// <summary>Where it was placed and which way it faces (the picture's nose is the car's: 0 north, clockwise).</summary>
    public (Vector2 At, float Heading) Pose => (Picture.At, Picture.Turn);
}

/// <summary>A Runner as the stage gives it: its car, and its route (null: none, so it stays parked).</summary>
public sealed record StageRunner(StageCar Car, Route? Route, bool Loop);

/// <summary>
/// **FEAT-009 S4: the stage's game logic, as Spy On Wheels plays it.** The world's layer for the Spy On Wheels profile, read
/// through the shared module (<see cref="GameLogic"/>, <see cref="RouteWorld"/>) against the profile in the asset store, beside
/// the themes: the Hero's placed car is the player's, each Runner's is a target driving its route (its legs over the roads,
/// as Xorpheus draws them). Everything wrong is said, never a crash: the layer's problems, a Hero that is no vehicle, a
/// Runner with no route (it stays parked), a Mission not played yet. A world with no layer for the game has no logic, and
/// plays as before (NFR-11).
/// </summary>
public sealed class StageLogic
{
    public StageCar? Hero { get; private init; }
    public List<StageRunner> Runners { get; } = new();
    public string? Mission { get; private init; }

    /// <summary>The placed things the game moves: neither drawn nor solid as parked cars.</summary>
    public HashSet<string> Moving { get; } = new(StringComparer.Ordinal);

    /// <summary>The Runners that flee: those with a route.</summary>
    public IEnumerable<StageRunner> Fleeing => Runners.Where(r => r.Route is not null);

    public static StageLogic? Read(WorldFile world, ThemeLibrary themes, string themesRoot, RoadSceneBuilder roads,
        IReadOnlyList<PlacedPicture> pictures, List<string> problems)
    {
        if (world.Logic is null || !world.Logic.TryGetValue(SpyOnWheelsProfile.Id, out var layer)) return null;
        var library = new ProfileLibrary();
        foreach (var note in library.Open(ProfileLibrary.BesideThemes(themesRoot))) problems.Add($"profiles: {note}");
        if (library.Find(SpyOnWheelsProfile.Id) is not { } profile)
        {
            problems.Add($"logic: the world is marked for '{SpyOnWheelsProfile.Id}', and no such profile is beside the themes at {themesRoot}; played without it");
            return null;
        }
        foreach (var p in GameLogic.Problems(world, library.Find)) problems.Add($"logic: {p.Message}");
        var routes = new RouteWorld(world, roads.TrafficSideOf, r => roads.StandardOf(r).LanePx);
        foreach (var p in routes.Problems(profile, layer)) problems.Add($"logic: {p.Message}");

        var things = (world.PlacedThings ?? new List<PlacedThing>()).ToDictionary(t => t.Id, StringComparer.Ordinal);
        StageCar? Car(string id)
        {
            if (!things.TryGetValue(id, out var thing)) return null;
            foreach (var pic in pictures)
                if (pic.Id == id)
                    return new StageCar(id, themes.Find(thing.Theme)?.Items.FirstOrDefault(i => i.ItemId == thing.Item)?.Name ?? thing.Item, pic, BodyOf(pic), Handling.For(thing.Item));
            return null;
        }

        var holders = GameLogic.Holders(world, profile, layer);
        StageCar? hero = null;
        if (holders.GetValueOrDefault("hero") is [var heroId, ..])
        {
            hero = Car(heroId);
            if (hero is null) problems.Add($"logic: the Hero '{heroId}' is not a placed vehicle picture; the player starts on the oldest road");
        }
        var mission = GameLogic.StageValues(profile, layer)["mission"] is JsonValue m && m.TryGetValue(out string? s) ? s : null;
        if (mission is not null and not "chase") problems.Add($"logic: the Mission is '{mission}'; only a chase is played yet (S4), so it is played as one");

        var logic = new StageLogic { Hero = hero, Mission = mission };
        if (hero is not null) logic.Moving.Add(hero.Id);
        foreach (var id in holders.GetValueOrDefault("runner") ?? new List<string>())
        {
            if (Car(id) is not { } car) { problems.Add($"logic: the Runner '{id}' is not a placed vehicle picture; left out"); continue; }
            var resolved = GameLogic.RoleOf(profile, layer, things[id]);
            var routeId = resolved.Values["route"] is JsonValue v && v.TryGetValue(out string? r) ? r : null;
            var route = routeId is null ? null : layer.ObjectById(routeId);
            if (route is null)
            {
                problems.Add($"logic: the Runner {car.Name} '{id}' has no route; it stays parked");
                logic.Runners.Add(new StageRunner(car, null, false));
                continue;
            }
            var path = Path(routes.Legs(route));
            if (path is null) { problems.Add($"logic: the Runner {car.Name}'s route has fewer than two places; it stays parked"); logic.Runners.Add(new StageRunner(car, null, false)); continue; }
            if (route.Loop) problems.Add($"logic: the Runner {car.Name}'s route loops, so it has no end to get away to: it drives round and never crosses a border");
            logic.Runners.Add(new StageRunner(car, path, route.Loop));
            logic.Moving.Add(id);
        }
        return logic;
    }

    /// <summary>A route's legs as one line to drive: their points in order, a point repeated where two legs meet left out.</summary>
    public static Route? Path(IEnumerable<RouteLeg> legs)
    {
        var points = new List<Vector2>();
        foreach (var leg in legs)
            foreach (var q in leg.Points)
            {
                var p = new Vector2((float)q.X, (float)q.Y);
                if (points.Count == 0 || Vector2.DistanceSquared(points[^1], p) > 1f) points.Add(p);
            }
        return points.Count >= 2 ? new Route(points) : null;
    }

    /// <summary>The car's box from its picture's solid part (turned back to face north), else most of its picture (as the
    /// sprite car's fallback: 94 % of its length, 78 % of its width).</summary>
    public static CarBody BodyOf(PlacedPicture picture)
    {
        if (picture.Solid is { Length: >= 3 } solid)
        {
            float c = MathF.Cos(-picture.Turn), s = MathF.Sin(-picture.Turn);
            var local = solid.Select(p => { var d = p - picture.At; return new Vector2(d.X * c - d.Y * s, d.X * s + d.Y * c); }).ToList();
            return new CarBody(local.Max(p => p.Y) - local.Min(p => p.Y), local.Max(p => p.X) - local.Min(p => p.X));
        }
        return new CarBody(picture.SizePx.Y * 0.94f, picture.SizePx.X * 0.78f);
    }
}

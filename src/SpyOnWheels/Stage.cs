using System.Collections.Generic;
using System.IO;
using System.Linq;
using XGame.WorldData;

namespace SpyOnWheels;

/// <summary>
/// **A stage as the game reads it (WI-015 S1):** the Xorpheus world file, read in place through the shared
/// <c>XGame.WorldData</c> package (D1, not the export), and the theme library it names, read from the asset store
/// (D2). What cannot be found is a problem said out loud, never a crash (assessment §4: unbound addresses are loud).
/// </summary>
public sealed class Stage
{
    public string WorldPath { get; }
    public WorldFile World { get; }
    public ThemeLibrary Themes { get; } = new();
    public List<string> Problems { get; } = new();

    private Stage(string worldPath, WorldFile world)
    {
        WorldPath = worldPath;
        World = world;
    }

    public static Stage Open(string worldPath, string themesRoot)
    {
        var stage = new Stage(Path.GetFullPath(worldPath), WorldIo.Load(worldPath));
        foreach (var note in stage.Themes.Open(themesRoot)) stage.Problems.Add($"theme library: {note}");
        foreach (var t in stage.World.Themes ?? new List<ThemeRef>())
            if (stage.Themes.Find(t.ThemeId) is null) stage.Problems.Add($"the world imports theme '{t.ThemeId}', which the library at {themesRoot} does not hold");
        foreach (var p in PlacedThings.Problems(stage.World, stage.Themes.Find))
            if (p.Kind is PlacedThings.ProblemKind.MissingTheme or PlacedThings.ProblemKind.MissingItem or PlacedThings.ProblemKind.NotDrawable)
                stage.Problems.Add(p.Text);
        return stage;
    }

    /// <summary>What was read, one fact a line: the report the cold check writes.</summary>
    public IEnumerable<string> Describe()
    {
        var w = World;
        var p = w.Project;
        yield return Problems.Count == 0 ? "OK" : "PROBLEMS";
        yield return $"world: {WorldPath}";
        yield return $"name: {p.Name}";
        yield return $"cell: {p.CellSize} px, level {p.EffectiveCellHeight} px, {p.EffectivePxPerMetre} px per metre";
        yield return $"ground: {w.Ground.Width} x {w.Ground.Height} cells, {1 + (w.Ground.Levels?.Count ?? 0)} levels";
        yield return $"pictures: {w.Pictures.Count}, assets: {w.Assets.Count}, sprite placements: {w.Placements.Count}";
        yield return $"lane roads: {w.LaneRoads?.Count ?? 0}, bridges: {w.LaneBridges?.Count ?? 0}";
        yield return $"themes: {string.Join(", ", (w.Themes ?? new List<ThemeRef>()).Select(t => t.ThemeId + (Themes.Find(t.ThemeId) is null ? " (MISSING)" : "")))}";
        foreach (var g in (w.PlacedThings ?? new List<PlacedThing>()).GroupBy(t => t.Theme + "/" + t.Item).OrderBy(g => g.Key))
            yield return $"placed: {g.Key} x{g.Count()}";
        foreach (var problem in Problems) yield return $"problem: {problem}";
    }
}

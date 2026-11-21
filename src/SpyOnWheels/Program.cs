using System;
using System.IO;
using System.Linq;
using SpyOnWheels;

// Usage:
//   SpyOnWheels [--world <path.world.json>] [--themes <themes root>] [--report <file>] [--shots <dir>]
// --shots: a scripted drive with no input; saves pictures and skeleton-report.txt into the folder, then exits.
// --report: read the world, write what was read to the file and exit (the cold check: no window needed).
var opts = Options.From(args);
if (opts.Report is { } report)
{
    var stage = Stage.Open(opts.World, opts.Themes);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
    File.WriteAllLines(report, stage.Describe());
    return stage.Problems.Count == 0 ? 0 : 1;
}

using var game = new SpyGame(opts);
game.Run();
return 0;

namespace SpyOnWheels
{
    /// <summary>Where the game reads its world and themes. Defaults: the stage the skeleton was built for, read in
    /// place from this repo's <c>Stages</c> (WI-015 D1), and the studio's asset store for themes (D2).</summary>
    public sealed record Options(string World, string Themes, string? Report, string? Shots)
    {
        public const string DefaultThemes = @"D:\dev\local\AI\OutputLocalFolder\xgame-studio-assets\assets\themes";

        public static Options From(string[] args)
        {
            string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
            return new Options(
                Opt("--world") ?? Path.Combine(RepoRoot(), "Stages", "theisland.world.world.json"),   // WI-020: his island is the stage
                Opt("--themes") ?? DefaultThemes,
                Opt("--report"),
                Opt("--shots"));
        }

        /// <summary>The repo root: the folder above the build output that holds <c>Stages</c>.</summary>
        private static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "Stages"))) return dir.FullName;
            return Directory.GetCurrentDirectory();
        }
    }
}

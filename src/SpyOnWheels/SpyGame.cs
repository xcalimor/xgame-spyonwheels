using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XGame.TerrainRendering;
using XGame.WorldData;
using XGame.WorldScene;

namespace SpyOnWheels;

/// <summary>
/// **The walking skeleton (WI-015 S3).** The stage drawn through the shared world scene, the Agent Car on the oldest
/// lane road's first point (D6), driven by the arrows (D4), the camera following it and never turning (D5).
/// <c>--shots &lt;dir&gt;</c> runs a scripted drive with no input and saves pictures and a report: the cold check.
/// </summary>
public sealed class SpyGame : Game
{
    public const string HeroAssetName = "Agent Car";
    public const float CameraZoom = 0.5f;

    private readonly GraphicsDeviceManager _graphics;
    private readonly Options _options;
    private SpriteBatch _spriteBatch = null!;
    private WorldView _view = null!;
    private Car _car = null!;
    private (string SourceFile, Rectangle Rect)? _carSprite;
    private double _clock;
    private int _frame;
    private readonly List<string> _report = new();
    private Vector2 _start;
    private readonly System.Diagnostics.Stopwatch _wall = System.Diagnostics.Stopwatch.StartNew();

    public SpyGame(Options options)
    {
        _options = options;
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
            GraphicsProfile = GraphicsProfile.HiDef,                    // the renderer's ps_4_0 shader needs it
            PreferredDepthStencilFormat = DepthFormat.Depth24Stencil8,  // placed things draw with depth, shadows with stencil
        };
        IsMouseVisible = true;
        IsFixedTimeStep = true;
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        var stage = Stage.Open(_options.World, _options.Themes);
        _view = new WorldView(GraphicsDevice, _spriteBatch, stage, _options.Themes);
        _carSprite = HeroSprite(stage.World);
        var (at, heading) = StartOf(stage.World, _view.RoadScene);
        _start = at;
        _car = new Car(at, heading, stage.World.Project.EffectivePxPerMetre);
        Window.Title = $"Spy On Wheels - {stage.World.Project.Name}" + (stage.Problems.Count == 0 ? "" : $" ({stage.Problems.Count} problems)");
        _report.AddRange(stage.Describe());
        _report.Add($"hero: {(_carSprite is { } s ? $"{HeroAssetName} {s.Rect.Width}x{s.Rect.Height} px from {s.SourceFile}" : "MISSING - drawn as a box")}");
        _report.Add(FormattableString.Invariant($"start: ({at.X:0}, {at.Y:0}) px, heading {MathHelper.ToDegrees(heading):0} deg"));
    }

    /// <summary>D6: the oldest lane road (the first painted), its first point, facing along it.</summary>
    public static (Vector2 At, float Heading) StartOf(WorldFile world, RoadSceneBuilder roads)
    {
        int c = world.Project.CellSize;
        var road = (world.LaneRoads ?? new List<LaneRoad>()).Where(r => r.Points.Count >= 2 && !roads.IsTrack(r))
            .OrderBy(r => r.Born ?? int.MaxValue).FirstOrDefault();
        if (road is null) return (new Vector2(world.Ground.Width * c / 2f, world.Ground.Height * c / 2f), 0f);
        var centre = LaneGeometry.Centreline(road.Points, c, road.RadiusCells * c);
        var s = centre[0];
        var p0 = s.PointAt(0);
        var p1 = s.PointAt(Math.Min(s.Length, c / 4.0));
        var forward = new Vector2((float)(p1.X - p0.X), (float)(p1.Y - p0.Y));
        float heading = MathF.Atan2(forward.X, -forward.Y);
        // Stand a little way in, in the outermost lane on the traffic's side (the lanes along the line lie on that side), so
        // the car starts on its road, in its own direction's lane: the road's band is its line moved AcrossLanes lanes right.
        var std = roads.StandardOf(road);
        var n = s.RightNormalAt(0);
        var right = new Vector2((float)n.X, (float)n.Y);
        double half = Math.Max(1, road.Lanes) * std.LanePx / 2;
        double side = roads.TrafficSideOf(road) == TrafficSide.Left ? -1 : 1;
        double across = road.AcrossLanes * std.LanePx + side * (half - std.LanePx / 2);
        var at = new Vector2((float)p0.X, (float)p0.Y) + right * (float)across + Vector2.Normalize(forward) * (c / 8f);
        return (at, heading);
    }

    /// <summary>The hero's picture: the world's sprite named <see cref="HeroAssetName"/>, its poster frame.</summary>
    private static (string SourceFile, Rectangle Rect)? HeroSprite(WorldFile world)
    {
        var asset = world.Assets.FirstOrDefault(a => a.Name == HeroAssetName && a.Class == AssetClass.Sprite);
        if (asset is null || world.PrimaryPicture(asset) is not { } picture || SpriteScene.PosterSeries(asset) is not { Frames.Count: > 0 } series) return null;
        if (asset.FrameById(series.Frames[0].Frame) is not { } frame) return null;
        var (x, y, w, h) = FrameUvMath.FrameRect(frame);
        return (picture.SourceFile, new Rectangle(x, y, w, h));
    }

    protected override void Update(GameTime gameTime)
    {
        var keys = Keyboard.GetState();
        if (keys.IsKeyDown(Keys.Escape)) Exit();
        double dt = gameTime.ElapsedGameTime.TotalSeconds;
        var drive = _options.Shots is not null
            ? new Drive(Throttle: _clock < 2.5, Brake: false, Steer: _clock is > 1.5 and < 2.5 ? 0.6f : 0f)   // the scripted drive
            : new Drive(keys.IsKeyDown(Keys.Up), keys.IsKeyDown(Keys.Down),
                (keys.IsKeyDown(Keys.Right) ? 1f : 0f) - (keys.IsKeyDown(Keys.Left) ? 1f : 0f));
        _car.Step(drive, dt);
        _clock += dt;
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);
        var camera = _overview ? Overview() : new WorldCamera(_car.Position, CameraZoom, 0f);
        float carHeight = _view.HeightPxAt(_car.Position);
        var viewport = GraphicsDevice.Viewport;
        _view.Draw(camera, _clock, band =>
        {
            if (carHeight >= band.From && carHeight < band.Below) DrawCar(camera, viewport, carHeight);
        });
        base.Draw(gameTime);
        _frame++;
        if (_options.Shots is { } dir) Script(dir);
    }

    private Texture2D? _pixel;

    /// <summary>The car about its centre, at its own size (the world's 64 px per metre is the sprite's), by the 2D camera of
    /// the ground's height under it.</summary>
    private void DrawCar(in WorldCamera camera, Viewport viewport, float heightPx)
    {
        _spriteBatch.Begin(blendState: BlendState.NonPremultiplied, samplerState: SamplerState.LinearClamp,
            transformMatrix: camera.AtHeight(heightPx, viewport).ViewMatrix(viewport));
        if (_carSprite is { } s)
        {
            var texture = _view.Picture(s.SourceFile);
            _spriteBatch.Draw(texture, _car.Position, s.Rect, Color.White, _car.Heading, new Vector2(s.Rect.Width / 2f, s.Rect.Height / 2f), 1f, SpriteEffects.None, 0f);
        }
        else
        {
            _pixel ??= MakePixel();
            _spriteBatch.Draw(_pixel, _car.Position, null, Color.Silver, _car.Heading, new Vector2(0.5f), new Vector2(150, 300), SpriteEffects.None, 0f);
        }
        _spriteBatch.End();
    }

    private Texture2D MakePixel()
    {
        var t = new Texture2D(GraphicsDevice, 1, 1);
        t.SetData(new[] { Color.White });
        return t;
    }

    /// <summary>The scripted drive's moments, in simulated seconds: a picture at each, the report at the last.</summary>
    private static readonly double[] ShotAt = { 0.1, 1.5, 3.0 };
    private int _shot;
    private bool _overview;

    /// <summary>The cold check's last picture: the whole painted ground in the window.</summary>
    private WorldCamera Overview()
    {
        var g = _view.Stage.World.Ground;
        int c = _view.Stage.World.Project.CellSize;
        float ox = g.Origin.Length > 0 ? g.Origin[0] : 0, oy = g.Origin.Length > 1 ? g.Origin[1] : 0;
        var centre = new Vector2((ox + g.Width / 2f) * c, (oy + g.Height / 2f) * c);
        float zoom = MathF.Min(1280f / (g.Width * c), 720f / (g.Height * c));
        return new WorldCamera(centre, zoom, 0f);
    }

    /// <summary>The cold check, on simulated time (so a throttled window changes nothing): full throttle for 2.5 s,
    /// straight along the road, a right turn from 1.5 s; a picture at the start, mid-drive and at 3 s; the report; exit.</summary>
    private void Script(string dir)
    {
        if (_overview) { Save(Path.Combine(dir, "skeleton-overview.png")); Exit(); return; }
        if (_shot >= ShotAt.Length || _clock < ShotAt[_shot]) return;
        Save(Path.Combine(dir, FormattableString.Invariant($"skeleton-{ShotAt[_shot]:0.0}s.png")));
        if (++_shot < ShotAt.Length) return;
        float metres = Vector2.Distance(_start, _car.Position) / (float)_view.Stage.World.Project.EffectivePxPerMetre;
        _report.Add(FormattableString.Invariant($"after {_clock:0.00} s simulated ({_frame} frames drawn, {_wall.Elapsed.TotalSeconds:0.0} s wall): ({_car.Position.X:0}, {_car.Position.Y:0}) px, {metres:0.0} m from the start, {_car.SpeedMs * 3.6:0} km/h, heading {MathHelper.ToDegrees(_car.Heading):0} deg"));
        _report.Insert(0, metres > 10 && _view.Stage.Problems.Count == 0 && _carSprite is not null ? "PASS" : "FAIL");
        File.WriteAllLines(Path.Combine(dir, "skeleton-report.txt"), _report);
        _overview = true;   // one more frame: the whole stage from high up, then exit
    }

    private void Save(string path)
    {
        int w = GraphicsDevice.PresentationParameters.BackBufferWidth, h = GraphicsDevice.PresentationParameters.BackBufferHeight;
        var data = new Color[w * h];
        GraphicsDevice.GetBackBufferData(data);
        using var texture = new Texture2D(GraphicsDevice, w, h);
        texture.SetData(data);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        texture.SaveAsPng(stream, w, h);
    }

    protected override void UnloadContent()
    {
        _view?.Dispose();
        _pixel?.Dispose();
        base.UnloadContent();
    }
}

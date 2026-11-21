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
    /// <summary>WI-020 D3: the car the player must stop.</summary>
    public const string TargetAssetName = "Pickup";
    private (string SourceFile, Rectangle Rect)? _targetSprite;
    private Route? _route;
    private Chase? _chase;
    private bool _againHeld;
    /// <summary>WI-017: the camera zooms out with speed.</summary>
    private readonly SpeedCamera _camera = new(Car.TopSpeedMs);

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
    private DriveWorld _drive = null!;
    private CarBody _body;
    private (Vector2 At, float Heading, float HeightPx) _startPose;
    private readonly Dictionary<DriveEvent, int> _events = new();
    private double _onDeckSeconds;
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
        _carSprite = SpriteOf(stage.World, HeroAssetName);
        _targetSprite = SpriteOf(stage.World, TargetAssetName);
        var (at, heading) = StartOf(stage.World, _view.RoadScene);
        _start = at;
        _car = new Car(at, heading, stage.World.Project.EffectivePxPerMetre);
        // WI-016: the world as the car feels it, the car's box, and the start it sinks back to.
        _drive = new DriveWorld(stage.World, _view.RoadScene, _view.Placed, _view.PictureSize);
        _body = BodyOf(stage.World, HeroAssetName, _carSprite?.Rect);
        _car.HeightPx = _view.HeightPxAt(at);
        _startPose = (at, heading, _car.HeightPx);
        // WI-020: the chase, when the stage has a road for the target to flee along.
        _route = Route.Of(stage.World, _view.RoadScene);
        if (_route is not null) NewChase();
        Window.Title = $"Spy On Wheels - {stage.World.Project.Name}" + (stage.Problems.Count == 0 ? "" : $" ({stage.Problems.Count} problems)");
        _report.AddRange(stage.Describe());
        _report.Add($"hero: {(_carSprite is { } s ? $"{HeroAssetName} {s.Rect.Width}x{s.Rect.Height} px from {s.SourceFile}" : "MISSING - drawn as a box")}");
        _report.Add(FormattableString.Invariant($"start: ({at.X:0}, {at.Y:0}) px, heading {MathHelper.ToDegrees(heading):0} deg"));
        _report.Add(FormattableString.Invariant($"world as the car feels it: {_drive.SolidCount} solids, {_drive.DeckCount} bridge decks; car box {_body.LengthPx / stage.World.Project.EffectivePxPerMetre:0.00} x {_body.WidthPx / stage.World.Project.EffectivePxPerMetre:0.00} m"));
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

    /// <summary>WI-020: a fresh chase on the stage's route: the player behind, the target ahead, full health.</summary>
    private void NewChase()
    {
        var world = _view.Stage.World;
        _chase = new Chase(_route!, _drive, world.Project.EffectivePxPerMetre, _body, BodyOf(world, TargetAssetName, _targetSprite?.Rect),
            _view.HeightPxAt, world.Project.EffectiveCellHeight);
        _car = _chase.Player;
        _startPose = _chase.PlayerStart;
        _start = _chase.PlayerStart.At;
    }

    /// <summary>A car's picture: the world's sprite named <paramref name="name"/>, its poster frame.</summary>
    private static (string SourceFile, Rectangle Rect)? SpriteOf(WorldFile world, string name)
    {
        var asset = world.Assets.FirstOrDefault(a => a.Name == name && a.Class == AssetClass.Sprite);
        if (asset is null || world.PrimaryPicture(asset) is not { } picture || SpriteScene.PosterSeries(asset) is not { Frames.Count: > 0 } series) return null;
        if (asset.FrameById(series.Frames[0].Frame) is not { } frame) return null;
        var (x, y, w, h) = FrameUvMath.FrameRect(frame);
        return (picture.SourceFile, new Rectangle(x, y, w, h));
    }

    /// <summary>D3: the car's box, its collision box if the hero sprite has one, otherwise 94% of its frame's length and
    /// 78% of its width (the rig's car is 4.72 m long in a 5 m frame).</summary>
    private static CarBody BodyOf(WorldFile world, string name, Rectangle? frameRect)
    {
        var asset = world.Assets.FirstOrDefault(a => a.Name == name && a.Class == AssetClass.Sprite);
        var series = asset is null ? null : SpriteScene.PosterSeries(asset);
        var frame = asset is not null && series is { Frames.Count: > 0 } ? asset.FrameById(series.Frames[0].Frame) : null;
        if (asset is not null && Collision.For(asset, frame) is { Count: > 0 } boxes)
            return new CarBody(boxes.Max(b => b.Y + b.Height) - boxes.Min(b => b.Y), boxes.Max(b => b.X + b.Width) - boxes.Min(b => b.X));
        var r = frameRect ?? new Rectangle(0, 0, 150, 300);
        return new CarBody(r.Height * 0.94f, r.Width * 0.78f);
    }

    protected override void Update(GameTime gameTime)
    {
        var keys = Keyboard.GetState();
        if (keys.IsKeyDown(Keys.Escape)) Exit();
        double dt = gameTime.ElapsedGameTime.TotalSeconds;
        var drive = _options.Shots is not null
            ? new Drive(Throttle: true, Brake: false, Steer: 0f)   // the scripted drive: flat out along the first road
            : new Drive(keys.IsKeyDown(Keys.Up), keys.IsKeyDown(Keys.Down),
                (keys.IsKeyDown(Keys.Right) ? 1f : 0f) - (keys.IsKeyDown(Keys.Left) ? 1f : 0f));
        if (_chase is not null)
        {
            // R (or Enter once it is over) starts the chase again: failure is fast to retry (vision §7).
            bool again = keys.IsKeyDown(Keys.R) || (_chase.State != ChaseState.Running && keys.IsKeyDown(Keys.Enter));
            if (again && !_againHeld) NewChase();
            _againHeld = again;
            _chase.Step(drive, dt);
        }
        else
        {
            var happened = Driving.Step(_car, drive, dt, _drive, _body, _view.Stage.World.Project.EffectivePxPerMetre, _startPose);
            _events[happened] = _events.GetValueOrDefault(happened) + 1;
        }
        _camera.Update(_car.SpeedMs, dt, _car.Forward, new Vector2(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height));
        if (_drive.SurfaceNear(_car.Position, _car.HeightPx) is { Kind: SurfaceKind.Deck }) _onDeckSeconds += dt;
        _clock += dt;
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);
        // WI-020 cold check: the 10 s picture looks at the target, the 20 s one at the border (the scripted car is elsewhere).
        var focus = _options.Shots is not null && _chase is not null
            ? _shot == 1 ? _chase.Target.Position : _shot == 2 ? _chase.Route.PointAt(_chase.BorderAlong) : _car.Position + _camera.Lead
            : _car.Position + _camera.Lead;
        var camera = _overview ? Overview() : new WorldCamera(focus, _camera.Zoom, 0f);
        float carHeight = _car.HeightPx;
        var viewport = GraphicsDevice.Viewport;
        _view.Draw(camera, _clock, band =>
        {
            bool In(float h) => h >= band.From && h < band.Below;
            if (_chase is not null)
            {
                var b = _chase.Route.PointAt(_chase.BorderAlong);
                float bh = _view.HeightPxAt(b);
                if (In(bh)) DrawBorder(camera, viewport, bh);
                if (In(_chase.Target.HeightPx)) DrawCar(camera, viewport, _chase.Target, _targetSprite, Color.IndianRed);
            }
            if (In(carHeight)) DrawCar(camera, viewport, _car, _carSprite, Color.Silver);
        });
        if (_chase is not null) DrawHud(viewport);
        base.Draw(gameTime);
        _frame++;
        if (_options.Shots is { } dir) Script(dir);
    }

    private Texture2D? _pixel;

    /// <summary>The car about its centre, at its own size (the world's 64 px per metre is the sprite's), by the 2D camera of
    /// the ground's height under it.</summary>
    private void DrawCar(in WorldCamera camera, Viewport viewport, Car car, (string SourceFile, Rectangle Rect)? sprite, Color fallback)
    {
        _spriteBatch.Begin(blendState: BlendState.NonPremultiplied, samplerState: SamplerState.LinearClamp,
            transformMatrix: camera.AtHeight(car.HeightPx, viewport).ViewMatrix(viewport));
        if (sprite is { } s)
        {
            var texture = _view.Picture(s.SourceFile);
            _spriteBatch.Draw(texture, car.Position, s.Rect, Color.White, car.Heading, new Vector2(s.Rect.Width / 2f, s.Rect.Height / 2f), 1f, SpriteEffects.None, 0f);
        }
        else
        {
            _pixel ??= MakePixel();
            _spriteBatch.Draw(_pixel, car.Position, null, fallback, car.Heading, new Vector2(0.5f), new Vector2(150, 300), SpriteEffects.None, 0f);
        }
        _spriteBatch.End();
    }

    /// <summary>WI-020 D4: the border, a red and white chequered band across the route.</summary>
    private void DrawBorder(in WorldCamera camera, Viewport viewport, float heightPx)
    {
        _pixel ??= MakePixel();
        var c = _chase!;
        var at = c.Route.PointAt(c.BorderAlong);
        float heading = c.Route.HeadingAt(c.BorderAlong);
        var right = new Vector2(MathF.Cos(heading), MathF.Sin(heading));
        var fwd = new Vector2(MathF.Sin(heading), -MathF.Cos(heading));
        float ppm = (float)_view.Stage.World.Project.EffectivePxPerMetre, sq = ppm;   // 1 m squares
        _spriteBatch.Begin(blendState: BlendState.NonPremultiplied, transformMatrix: camera.AtHeight(heightPx, viewport).ViewMatrix(viewport));
        for (int i = -7; i < 7; i++)
            for (int j = 0; j < 2; j++)
            {
                var p = at + right * (i * sq) + fwd * (j * sq);
                _spriteBatch.Draw(_pixel, p, null, (i + j) % 2 == 0 ? Color.Red : Color.White, heading, Vector2.Zero, new Vector2(sq, sq), SpriteEffects.None, 0f);
            }
        _spriteBatch.End();
    }

    /// <summary>WI-020: health, the gap to the target, the last contact, and the outcome.</summary>
    private void DrawHud(Viewport viewport)
    {
        _pixel ??= MakePixel();
        var c = _chase!;
        _spriteBatch.Begin(blendState: BlendState.NonPremultiplied, samplerState: SamplerState.PointClamp);
        // Health: a bar, top left.
        var bar = new Rectangle(20, 20, 300, 18);
        _spriteBatch.Draw(_pixel, new Rectangle(bar.X - 2, bar.Y - 2, bar.Width + 4, bar.Height + 4), Color.Black * 0.7f);
        float share = (float)(c.Health / Chase.MaxHealth);
        _spriteBatch.Draw(_pixel, new Rectangle(bar.X, bar.Y, (int)(bar.Width * share), bar.Height), share > 0.5f ? Color.LimeGreen : share > 0.25f ? Color.Orange : Color.Red);
        PixelFont.Draw(_spriteBatch, _pixel, $"HEALTH {c.Health:0}", new Vector2(bar.Right + 12, bar.Y + 1), 3, Color.White);
        float toBorder = (c.BorderAlong - c.Runner.Along) / (float)_view.Stage.World.Project.EffectivePxPerMetre;
        PixelFont.Draw(_spriteBatch, _pixel, $"TARGET {c.GapMetres:0} M   BORDER {Math.Max(0, toBorder):0} M   {c.Player.SpeedMs * 3.6:0} KM/H", new Vector2(20, 50), 3, Color.White);
        if (c.Contacts.Count > 0)
        {
            var (at, hit) = c.Contacts[^1];
            if (c.Clock - at < 2.5)
            {
                string what = hit.Kind switch { ContactKind.Rear => "REAR", ContactKind.Side => "SIDE", ContactKind.Angled => "ANGLED", _ => "HEAD-ON" };
                string did = hit.Kind == ContactKind.Frontal ? "" : hit.SpunOut ? " - SPUN OUT!" : hit.Kind == ContactKind.Side ? " - THROWN" : " - WOBBLE";
                PixelFont.Draw(_spriteBatch, _pixel, $"{what} {hit.ClosingMs * 3.6:0} KM/H{did}  -{hit.Damage:0}", new Vector2(20, 76), 3, Color.Yellow);
            }
        }
        if (c.State == ChaseState.Running && c.Clock < 3)
            Centre("STOP THE PICKUP BEFORE THE BORDER", viewport.Height / 3f, 5, Color.White);
        if (c.State != ChaseState.Running)
        {
            Centre(c.Outcome ?? "", viewport.Height / 2f - 40, 10, c.State == ChaseState.Won ? Color.LimeGreen : Color.Red);
            Centre("ENTER OR R TO GO AGAIN", viewport.Height / 2f + 30, 4, Color.White);
        }
        _spriteBatch.End();

        void Centre(string text, float y, float scale, Color color)
        {
            float w = PixelFont.Width(text, scale);
            var at = new Vector2((viewport.Width - w) / 2f, y);
            _spriteBatch.Draw(_pixel, new Rectangle((int)at.X - 12, (int)y - 10, (int)w + 24, (int)(5 * scale) + 20), Color.Black * 0.6f);
            PixelFont.Draw(_spriteBatch, _pixel, text, at, scale, color);
        }
    }

    private Texture2D MakePixel()
    {
        var t = new Texture2D(GraphicsDevice, 1, 1);
        t.SetData(new[] { Color.White });
        return t;
    }

    /// <summary>The scripted drive's moments, in simulated seconds: a picture at each, the report at the last.</summary>
    private static readonly double[] ShotAt = { 3, 10, 20, 30 };
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
        double cppm = _view.Stage.World.Project.EffectivePxPerMetre;
        _report.Add(FormattableString.Invariant($"camera at {ShotAt[_shot]:0} s: {_car.SpeedMs * 3.6:0} km/h, zoom {_camera.Zoom:0.000} (target {_camera.TargetAt(_car.SpeedMs):0.000}), view {1280 / _camera.Zoom / cppm:0} x {720 / _camera.Zoom / cppm:0} m, lead {_camera.Lead.Length() / cppm:0.0} m"));
        if (++_shot < ShotAt.Length) return;
        float metres = Vector2.Distance(_start, _car.Position) / (float)_view.Stage.World.Project.EffectivePxPerMetre;
        _report.Add(FormattableString.Invariant($"after {_clock:0.00} s simulated ({_frame} frames drawn, {_wall.Elapsed.TotalSeconds:0.0} s wall): ({_car.Position.X:0}, {_car.Position.Y:0}) px, {metres:0.0} m from the start, {_car.SpeedMs * 3.6:0} km/h, heading {MathHelper.ToDegrees(_car.Heading):0} deg"));
        _report.Add(FormattableString.Invariant($"events (steps): {string.Join(", ", _events.OrderBy(e => e.Key).Select(e => $"{e.Key} {e.Value}"))}; on a bridge deck {_onDeckSeconds:0.0} s; height now {_car.HeightPx / _view.Stage.World.Project.EffectiveCellHeight:0.00} levels"));
        for (int m = -4; m <= 16; m += 2)
            _report.Add(FormattableString.Invariant($"  ahead {m,3} m: {_drive.Describe(_car.Position + _car.Forward * (float)(m * _view.Stage.World.Project.EffectivePxPerMetre))}"));
        // WI-019: the surface across the road at the start, every metre from its middle out onto the ground.
        {
            var right = new Vector2(MathF.Cos(_startPose.Heading), MathF.Sin(_startPose.Heading));
            double sppm = _view.Stage.World.Project.EffectivePxPerMetre;
            _report.Add("traction across the road at the start (m from the middle): " + string.Join(", ", Enumerable.Range(-8, 17).Select(m =>
                FormattableString.Invariant($"{m} {(_drive.TractionAt(_startPose.At + right * (float)(m * sppm), _startPose.HeightPx) == Traction.Asphalt ? "A" : "g")}"))));
        }
        // Every deck driven, headless: from 3 m before its start along its run for 6 s; where the car ends up.
        double ppm = _view.Stage.World.Project.EffectivePxPerMetre;
        foreach (var (roadId, deckStart, unit, length, deckHeight) in _drive.Decks)
        {
            var from = deckStart - unit * (float)(3 * ppm);
            float heading = MathF.Atan2(unit.X, -unit.Y);
            var probe = new Car(from, heading, ppm);
            probe.HeightPx = _drive.SurfaceNear(from, deckHeight)?.HeightPx ?? deckHeight;
            var pose = (from, heading, probe.HeightPx);
            var seen = new Dictionary<DriveEvent, int>();
            float furthest = 0;
            for (int i = 0; i < 360; i++)
            {
                var ev = Driving.Step(probe, new Drive(true, false, 0), 1 / 60.0, _drive, _body, ppm, pose);
                seen[ev] = seen.GetValueOrDefault(ev) + 1;
                furthest = MathF.Max(furthest, Vector2.Dot(probe.Position - deckStart, unit));
            }
            _report.Add(FormattableString.Invariant($"deck run {roadId[..Math.Min(8, roadId.Length)]} at level {deckHeight / _view.Stage.World.Project.EffectiveCellHeight:0}, {length / ppm:0} m long: reached {furthest / ppm:0.0} m along ({(furthest > length ? "CROSSED" : "did not cross")}), {string.Join(", ", seen.OrderBy(e => e.Key).Select(e => $"{e.Key} {e.Value}"))}; ends on: {_drive.Describe(probe.Position)}"));
            // The same deck again: straight on until on it, then steering hard (WI-018: drifting) into its side: does it fall off?
            foreach (float steer in new[] { 1f, -1f })
            {
                var side = new Car(from, heading, ppm) { HeightPx = probe.HeightPx };
                side.Reset(from, heading, _drive.SurfaceNear(from, deckHeight)?.HeightPx ?? deckHeight);
                // WI-018: a drifting car may leave the road before the deck and go down beside it (a real route); what must never
                // happen is a car standing on the deck one step and below it the next.
                bool slipped = false, reached = false;
                float level = _view.Stage.World.Project.EffectiveCellHeight;
                for (int i = 0; i < 360; i++)
                {
                    bool onDeck = Math.Abs(side.HeightPx - deckHeight) < 1 && _drive.SurfaceNear(side.Position, side.HeightPx) is { Kind: SurfaceKind.Deck };
                    reached |= onDeck;
                    Driving.Step(side, new Drive(true, false, reached ? steer : 0), 1 / 60.0, _drive, _body, ppm, (from, heading, side.HeightPx));
                    if (onDeck && side.HeightPx < deckHeight - 0.5f * level) slipped = true;
                }
                _report.Add($"  steering {(steer > 0 ? "right" : "left")} on it: {(slipped ? "FELL OFF the deck" : reached ? "stayed on the deck while on it" : "drifted off before reaching it")}");
            }
        }
        // WI-020: the chase, headless. The route, then the target alone (the player parked): does it reach the border, how
        // fast, how far off its line it ever strays, how long off the road.
        bool runnerOk = true;
        if (_route is not null)
        {
            var world = _view.Stage.World;
            _report.Add(FormattableString.Invariant($"route: {_route.Length / ppm:0} m, {_route.Points.Count} points, from ({_route.Points[0].X / world.Project.CellSize:0.0}, {_route.Points[0].Y / world.Project.CellSize:0.0}) to ({_route.Points[^1].X / world.Project.CellSize:0.0}, {_route.Points[^1].Y / world.Project.CellSize:0.0}) cells"));
            var alone = new Chase(_route, _drive, ppm, _body, BodyOf(world, TargetAssetName, _targetSprite?.Rect), _view.HeightPxAt, world.Project.EffectiveCellHeight);
            _report.Add(FormattableString.Invariant($"border at {alone.BorderAlong / ppm:0} m along; target starts {(alone.Runner.Along) / ppm:0} m along; target {(_targetSprite is null ? "MISSING - a box" : TargetAssetName)}"));
            float worstOff = 0; double offRoad = 0, topKmh = 0, lastOff = 0;
            var offAt = new List<string>();
            var parked = new Drive(false, false, 0);
            while (alone.State == ChaseState.Running && alone.Clock < 120)
            {
                alone.Step(parked, 1 / 60.0);
                worstOff = MathF.Max(worstOff, Vector2.Distance(alone.Target.Position, _route.PointAt(alone.Runner.Along)));
                if (_drive.TractionAt(alone.Target.Position, alone.Target.HeightPx) != Traction.Asphalt)
                {
                    if (offRoad == 0 || alone.Clock - lastOff > 0.5) offAt.Add(FormattableString.Invariant($"{alone.Runner.Along / ppm:0} m ({alone.Target.SpeedMs * 3.6:0} km/h)"));
                    lastOff = alone.Clock;
                    offRoad += 1 / 60.0;
                }
                topKmh = Math.Max(topKmh, alone.Target.SpeedMs * 3.6);
            }
            runnerOk = alone.Outcome == "IT GOT AWAY" && worstOff / ppm < 4;
            _report.Add(FormattableString.Invariant($"target alone: {alone.Outcome ?? "still running"} after {alone.Clock:0.0} s, top {topKmh:0} km/h, furthest off its line {worstOff / ppm:0.0} m, off the road {offRoad:0.0} s => {(runnerOk ? "ok" : "BAD")}"));
            if (offAt.Count > 0) _report.Add("  left the road at: " + string.Join(", ", offAt));
            // His first drive: after a restart the car went through everything and the pickup could be stopped only once.
            // Three chases in a row on the island, a rammer at the wheel: how each ends, and frames either car sat "caught"
            // (its box not fitting where it is, so the driving lets it pass through everything).
            var tbody = BodyOf(world, TargetAssetName, _targetSprite?.Rect);
            for (int run = 0; run <= 3; run++)   // run 0: straight at the target from the start, into the first cliff (where the scrape caught it)
            {
                var c = new Chase(_route, _drive, ppm, _body, tbody, _view.HeightPxAt, world.Project.EffectiveCellHeight);
                bool startFits = Driving.Fits(c.Player.Position, c.Player.Heading, c.Player.HeightPx, _drive, _body, out _, out _)
                    && Driving.Fits(c.Target.Position, c.Target.Heading, c.Target.HeightPx, _drive, tbody, out _, out _);
                int caughtP = 0, caughtT = 0;
                // The rammer: the target's own route steering, flat out where that would not brake, and straight at the
                // target once within 12 m of it.
                // Started 10 m behind the target (the stops are what is checked here; the catching is his drive to judge).
                float behind = run == 0 ? _route.Project(c.Player.Position) : c.Runner.Along - (float)(10 * ppm);
                if (run > 0) c.Player.Reset(_route.PointAt(behind), _route.HeadingAt(behind), _view.HeightPxAt(_route.PointAt(behind)));
                var pilot = new Runner(_route, ppm, behind);
                while (c.State == ChaseState.Running && c.Clock < 90)
                {
                    var follow = pilot.DriveFor(c.Player);
                    var to = c.Target.Position - c.Player.Position;
                    float off = MathHelper.WrapAngle(MathF.Atan2(to.X, -to.Y) - c.Player.Heading);
                    bool close = run == 0 || to.Length() < 12 * ppm;
                    c.Step(close ? new Drive(true, false, Math.Clamp(off * 3, -1, 1)) : new Drive(true, follow.Brake && c.Player.SpeedMs > Runner.CruiseMs * (1 - Runner.BendSlowdown) + 4, follow.Steer), 1 / 60.0);
                    if (!Driving.Fits(c.Player.Position, c.Player.Heading, c.Player.HeightPx, _drive, _body, out _, out _))
                    {
                        if (caughtP++ == 0)
                            _report.Add(FormattableString.Invariant($"  first caught at {c.Clock:0.00} s: ({c.Player.Position.X / world.Project.CellSize:0.00}, {c.Player.Position.Y / world.Project.CellSize:0.00}) cells, heading {MathHelper.ToDegrees(c.Player.Heading):0}, {c.Player.SpeedMs * 3.6:0} km/h slip {c.Player.SlipMs:0.0}, height {c.Player.HeightPx:0}, here {_drive.Describe(c.Player.Position)}"));
                    }
                    if (!Driving.Fits(c.Target.Position, c.Target.Heading, c.Target.HeightPx, _drive, tbody, out _, out _)) caughtT++;
                }
                bool clean = startFits && caughtP == 0 && caughtT == 0;
                runnerOk &= clean;
                _report.Add(FormattableString.Invariant($"rammer run {run}: {c.Outcome ?? "running"} at {c.Clock:0.0} s, {c.Contacts.Count} contacts, health {c.Health:0}, you {pilot.Along / ppm:0} m along at {c.Player.SpeedMs * 3.6:0} km/h, gap {c.GapMetres:0} m; starts fit {startFits}; caught frames you {caughtP}, target {caughtT} => {(clean ? "ok" : "BAD")}"));
            }
            _report.Add(FormattableString.Invariant($"the scripted drive's chase:{_chase?.Outcome ?? "running"} at {_chase?.Clock:0.0} s; contacts: {string.Join("; ", _chase?.Contacts.Select(h => FormattableString.Invariant($"{h.At:0.0} s {h.Result.Kind} {h.Result.ClosingMs * 3.6:0} km/h spin {h.Result.SpunOut} -{h.Result.Damage:0}")) ?? Array.Empty<string>())}"));
        }
        _report.Insert(0, metres > 10 && _view.Stage.Problems.Count == 0 && _carSprite is not null && runnerOk ? "PASS" : "FAIL");
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

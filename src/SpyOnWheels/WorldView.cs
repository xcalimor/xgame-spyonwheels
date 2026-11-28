using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XGame.TerrainRendering;
using XGame.WorldData;
using XGame.WorldScene;

namespace SpyOnWheels;

/// <summary>
/// **The stage drawn as Xorpheus draws it (WI-015 S3)**, entirely through the shared <c>XGame.WorldScene</c> package:
/// the ground's light, atlas and levels; the road scene (lanes with their skins, bridges, low bridges); the placed
/// things; the sprite placements; one draw order. The game adds only what moves: the cars, in their height band.
/// </summary>
public sealed class WorldView : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly SpriteBatch _spriteBatch;
    private readonly PictureCache _pictures;
    private readonly PictureCache _themePictures;
    private readonly Texture2D _placeholder;
    private readonly GroundAtlas? _atlas;

    public Stage Stage { get; }
    public TerrainRenderer Terrain { get; }
    public RoadRenderer Roads { get; }
    public RoadSceneBuilder RoadScene { get; }
    public PlacedSceneBuilder Placed { get; }
    public GroundSurface Surface { get; }

    public WorldView(GraphicsDevice device, SpriteBatch spriteBatch, Stage stage, string themesRoot)
    {
        _device = device;
        _spriteBatch = spriteBatch;
        Stage = stage;
        var world = stage.World;

        _pictures = new PictureCache(device);
        _pictures.SetPicturesDir(PictureCache.DirOf(stage.WorldPath));
        _themePictures = new PictureCache(device);
        _themePictures.SetPicturesDir(themesRoot);
        _placeholder = Checker(device);

        Terrain = new TerrainRenderer(device);
        _atlas = GroundScene.Apply(Terrain, device, spriteBatch, world, _pictures);
        Roads = new RoadRenderer(device);
        RoadScene = new RoadSceneBuilder(Roads, stage.Themes, _themePictures, _placeholder, () => world, () => Terrain.Lock);
        RoadScene.Build(perspective: false);
        Placed = new PlacedSceneBuilder(device, RoadScene);
        Placed.Build();
        Surface = new GroundSurface(world);
    }

    /// <summary>The height of the top surface at a world point, in px (0 over the void).</summary>
    public float HeightPxAt(Vector2 at) =>
        Surface.HeightAt(at.X, at.Y) is double h ? (float)(h * Stage.World.Project.EffectiveCellHeight) : 0f;

    public Texture2D Picture(string sourceFile) => _pictures.Get(sourceFile);

    /// <summary>FEAT-009 S4: a theme picture (a vehicle's), by the absolute file the placed scene names.</summary>
    public Texture2D ThemePicture(string file) => _themePictures.Get(file);

    /// <summary>FEAT-009 S4: placed vehicles the game moves itself (the Hero, the Runners): not drawn as parked.</summary>
    public HashSet<string> Moving { get; } = new(StringComparer.Ordinal);

    public (int Width, int Height)? PictureSize(string sourceFile) => _pictures.TryGetSize(sourceFile);

    /// <summary>One frame of the world; <paramref name="movers"/> draws the game's moving things in each height band, and
    /// <paramref name="onGround"/> what lies flat on the ground (WI-021 tyre marks), under the band's sprites and cars.</summary>
    public void Draw(in WorldCamera camera, double clock, Action<(double From, double Below)> movers, Action<(double From, double Below)>? onGround = null)
    {
        var viewport = _device.Viewport;
        var world = Stage.World;
        if (_atlas is not null) Terrain.Draw(_spriteBatch, camera, clock);
        var order = SpriteScene.DrawOrder(world);
        var eye = camera;
        WorldDrawOrder.Draw(camera, viewport, Roads, Terrain, Placed.Renderer, world.Project.EffectiveCellHeight, band =>
        {
            onGround?.Invoke(band);
            SpriteScene.Draw(_spriteBatch, eye, viewport, order, band, _ => Color.White,
                p => SpriteScene.Resolve(world, p, _pictures.TryGetSize, clock), _pictures.Get);
            // FEAT-009 S4: parked vehicles, drawn as Xorpheus draws them.
            PlacedPictures.Draw(_spriteBatch, eye, viewport, Placed.Pictures.Where(p => !Moving.Contains(p.Id)), band, _themePictures.Get);
            movers(band);
        });
    }

    /// <summary>The placeholder for a skin the library cannot supply: the editor's grey checker, so it reads as missing.</summary>
    private static Texture2D Checker(GraphicsDevice device)
    {
        const int n = 16;
        var texture = new Texture2D(device, n, n);
        texture.SetData(Enumerable.Range(0, n * n).Select(i => ((i % n) / 4 + (i / n) / 4) % 2 == 0 ? new Color(200, 0, 200) : new Color(60, 60, 60)).ToArray());
        return texture;
    }

    public void Dispose()
    {
        _atlas?.Atlas.Dispose();
        _pictures.Dispose();
        _themePictures.Dispose();
        _placeholder.Dispose();
    }
}

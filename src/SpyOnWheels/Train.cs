using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using XGame.WorldData;

namespace SpyOnWheels;

/// <summary>
/// **WI-027: a train on the stage's railway (his ask: "add a train that goes between the end points").** A locomotive and
/// its wagons shuttle along one track line end to end: they pull away, cruise, slow to a stop at the far end, wait, and go
/// back (push-pull: the locomotive leads one way and pushes the other, as Xorpheus' preview runs it). The line is the
/// editor's own <see cref="TrackWalk"/> (over bridges and on along touching tracks), and the vehicles are placed by
/// <see cref="TrainPreview.Place"/>, so the game's train runs where the editor's preview does.
///
/// <para>Nothing stops a train. Each vehicle is a <see cref="Car"/> moved by the timetable (never by the driving), so the
/// cars' drivers see it like any car ahead, and a contact reads it with the train's mass.</para>
/// </summary>
public sealed class Train
{
    public const double CruiseMs = 16;           // ~58 km/h
    public const double AccelMs2 = 1.2;          // pulls away over ~13 s
    public const double WaitSeconds = 4;         // at each end
    public const double LocoMetres = 17, WagonMetres = 14, WidthMetres = 3.0;
    public const int Wagons = 3;
    public const double MassKg = 60000;          // per vehicle: a car against it is a car against a wall that moves

    public static readonly Handling Rail = new("train", CruiseMs, AccelMs2, AccelMs2, 0, 0, 0, 0, 1, MassKg);

    private readonly TrackWalk _walk;
    private readonly double _ppm;
    private readonly Func<Vector2, float> _heightAt;
    private readonly List<double> _lengths;
    private readonly double _gap, _trainPx;
    private double _head, _speed, _wait;
    private int _dir = 1;

    public Train(TrackWalk walk, double pxPerMetre, Func<Vector2, float> heightAt, float levelPx)
    {
        _walk = walk;
        _ppm = pxPerMetre;
        _heightAt = heightAt;
        LevelPx = levelPx;
        _lengths = new[] { LocoMetres }.Concat(Enumerable.Repeat(WagonMetres, Wagons)).Select(m => m * pxPerMetre).ToList();
        _gap = TrainPreview.CouplingMeters * pxPerMetre;
        _trainPx = TrainPreview.TrainLength(_lengths, _gap);
        _head = _trainPx;   // the whole train on the line's first stretch
        Vehicles = _lengths.Select(l => (new Car(Vector2.Zero, 0, pxPerMetre, Rail), new CarBody((float)l, (float)(WidthMetres * pxPerMetre)))).ToList();
        Place();
    }

    private float LevelPx { get; }

    /// <summary>The line is long enough to hold the train with room to run.</summary>
    public static bool Fits(TrackWalk walk, double pxPerMetre) =>
        walk.Length > TrainPreview.TrainLength(new[] { LocoMetres }.Concat(Enumerable.Repeat(WagonMetres, Wagons)).Select(m => m * pxPerMetre).ToList(),
            TrainPreview.CouplingMeters * pxPerMetre) + 20 * pxPerMetre;

    /// <summary>The locomotive first, then each wagon, with its box.</summary>
    public List<(Car Car, CarBody Body)> Vehicles { get; }

    /// <summary>Metres per second along the line, signed: positive toward the line's far end.</summary>
    public double SpeedMs => _speed * _dir;

    /// <summary>Waiting at an end.</summary>
    public bool Waiting => _wait > 0;

    /// <summary>How far the locomotive's nose is along the line, px.</summary>
    public double Head => _head;

    public double LengthPx => _walk.Length;

    public void Step(double dt)
    {
        if (_wait > 0)
        {
            _wait = Math.Max(0, _wait - dt);
            Place();
            return;
        }
        double remaining = (_dir > 0 ? _walk.Length - _head : _head - _trainPx) / _ppm;
        double canStop = Math.Sqrt(2 * AccelMs2 * Math.Max(0, remaining));
        _speed = Math.Min(Math.Min(CruiseMs, _speed + AccelMs2 * dt), canStop);
        _head += _dir * _speed * _ppm * dt;
        if (remaining <= 0.05 || _speed < 0.05 && remaining < 0.5)
        {
            _head = _dir > 0 ? _walk.Length : _trainPx;
            _speed = 0;
            _dir = -_dir;
            _wait = WaitSeconds;
        }
        Place();
    }

    private void Place()
    {
        var poses = TrainPreview.Place(_walk, _lengths, _gap, _head);
        for (int i = 0; i < poses.Count; i++)
        {
            var p = poses[i];
            var at = new Vector2((float)p.Centre.X, (float)p.Centre.Y);
            var heading = new Vector2((float)p.Heading.X, (float)p.Heading.Y);
            var car = Vehicles[i].Car;
            float height = _walk.DeckAt(_head - _lengths[i] / 2) is { } z ? z * LevelPx : _heightAt(at);
            car.Carry(at, (float)p.Rotation, heading * (float)(_speed * _dir), height);
        }
    }

    /// <summary>One train per railway line on the stage (a line over several tracks is one train), where it fits.</summary>
    public static List<Train> On(WorldFile world, XGame.WorldScene.RoadSceneBuilder roads, Func<Vector2, float> heightAt)
    {
        double ppm = world.Project.EffectivePxPerMetre;
        float level = world.Project.EffectiveCellHeight;
        var trains = new List<Train>();
        var lines = new List<TrackWalk>();
        foreach (var road in (world.LaneRoads ?? new List<LaneRoad>()).Where(r => r.Points.Count >= 2 && roads.IsTrack(r)))
        {
            if (TrackWalk.Of(world, road, roads.StandardOf(road).LanePx) is not { } walk) continue;
            var (mid, _) = walk.PoseAt(walk.Length / 2);
            if (lines.Any(l => Math.Abs(l.Length - walk.Length) < 1 && Near(l, mid, ppm))) continue;   // the same line, from another of its tracks
            lines.Add(walk);
            if (Fits(walk, ppm)) trains.Add(new Train(walk, ppm, heightAt, level));
        }
        return trains;
    }

    private static bool Near(TrackWalk walk, RoadPoint p, double ppm)
    {
        for (double a = 0; a <= walk.Length; a += ppm)
        {
            var (q, _) = walk.PoseAt(a);
            if ((q - p).Length < ppm) return true;
        }
        return false;
    }
}

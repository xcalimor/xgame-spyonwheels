using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace SpyOnWheels;

/// <summary>Why a car's tyres are leaving rubber this step.</summary>
public enum TyreWork { Launch, Brake, Slide }

/// <summary>One short dark stroke of rubber on the ground, world px, at the surface height it lies on.</summary>
public readonly record struct TyreMark(Vector2 From, Vector2 To, float HeightPx, double Born, float Strength);

/// <summary>
/// **Tyre marks (WI-021, his ask: "better details like tire tracks when accelerating hard or hit the breaks").** Dark
/// strokes left where the tyres work hard: full throttle from low speed (the two rear wheels), the brakes at speed (all
/// four), and a slide or a spin-out (all four, a sideways smear). Only on paved ground (asphalt and decks): grass takes none.
/// The marks fade slowly, the oldest first, and their number never passes the cap. Plain simulation: the game says which
/// cars to watch and where the ground is paved, and draws <see cref="Marks"/> on the ground under the cars.
/// </summary>
public sealed class TyreMarks
{
    // First guesses, to be found in play (vision §11).
    public const int DefaultCap = 6000;            // strokes kept (~25 s of four wheels sliding); past it the oldest go
    public const double FadeSeconds = 40;          // a mark is gone this long after it was laid...
    public const double HoldShare = 0.5;           // ...and stays full dark for this share of it, then fades out
    public const double BrakeFromMs = 8;           // the brakes held faster than ~29 km/h
    public const double SlideFromMs = 2.5;         // sliding sideways faster than this (a drift, a shove)
    public const double SpinFromMs = 1;            // a spinning car marks while it still moves this fast
    public const double WidthMetres = 0.22;        // a tyre's print
    public const double MinStrokeMetres = 0.15;    // a stroke is laid once the wheel has moved this far
    public const double BreakMetres = 3;           // further in one step (a reset, a sink) starts a new line
    public const float WheelAlong = 0.34f;         // wheels from the centre: this share of the body's length forward and back
    public const float WheelAcross = 0.40f;        // and this share of its width out to each side

    /// <summary>How dark each kind lays down (1 = black).</summary>
    public static float StrengthOf(TyreWork work) => work switch { TyreWork.Launch => 0.55f, TyreWork.Brake => 0.45f, _ => 0.6f };

    private readonly TyreMark[] _ring;
    private int _first, _count;
    private readonly Dictionary<Car, Vector2?[]> _wheels = new();
    private readonly HashSet<Car> _seen = new();

    public TyreMarks(int cap = DefaultCap) => _ring = new TyreMark[Math.Max(1, cap)];

    public int Cap => _ring.Length;
    public int Count => _count;
    public double Clock { get; private set; }

    /// <summary>Every mark, the oldest first.</summary>
    public IEnumerable<TyreMark> Marks
    {
        get { for (int i = 0; i < _count; i++) yield return _ring[(_first + i) % _ring.Length]; }
    }

    /// <summary>How visible a mark is now: full for the first <see cref="HoldShare"/> of its life, then down to nothing.</summary>
    public float Alpha(in TyreMark m)
    {
        double age = Clock - m.Born;
        double fadeFrom = FadeSeconds * HoldShare;
        return (float)Math.Clamp(1 - (age - fadeFrom) / (FadeSeconds - fadeFrom), 0, 1);
    }

    /// <summary>What the car's tyres are doing hard right now, or null when they just roll (or it is in the air).
    /// <paramref name="calm"/>: a car that never launches or stands on its brakes (traffic); it still slides when shoved.</summary>
    public static TyreWork? Working(Car car, bool calm = false)
    {
        if (car.Airborne) return null;
        if (car.Spinning) return car.Velocity.Length() > SpinFromMs ? TyreWork.Slide : null;
        if (Math.Abs(car.SlipMs) > SlideFromMs) return TyreWork.Slide;
        if (calm) return null;
        var d = car.LastDrive;
        if (d.Brake && car.SpeedMs > BrakeFromMs) return TyreWork.Brake;
        if (car.WheelspinShare > 0 && car.SpeedMs > 0) return TyreWork.Launch;   // WI-030: the rear tyres spinning (Handling.WheelspinBelowShare)
        return null;
    }

    /// <summary>Start a frame: the clock moves on and marks past their life go (the oldest are always first).</summary>
    public void Tick(double dt)
    {
        Clock += dt;
        while (_count > 0 && Clock - _ring[_first].Born >= FadeSeconds) { _first = (_first + 1) % _ring.Length; _count--; }
    }

    /// <summary>Watch one car after its step: each working wheel on paved ground extends its line; a wheel that stops
    /// working, leaves the paving or jumps far ends its line. <paramref name="paved"/> answers for a point and a height.</summary>
    public void Observe(Car car, CarBody body, double pxPerMetre, Func<Vector2, float, bool> paved, bool calm = false)
    {
        _seen.Add(car);
        if (!_wheels.TryGetValue(car, out var last)) _wheels[car] = last = new Vector2?[4];
        var work = Working(car, calm);
        var f = car.Forward;
        var r = car.Right;
        float a = body.LengthPx * WheelAlong, w = body.WidthPx * WheelAcross;
        // Front left, front right, rear right, rear left.
        Span<Vector2> at = stackalloc Vector2[] { car.Position + f * a - r * w, car.Position + f * a + r * w, car.Position - f * a + r * w, car.Position - f * a - r * w };
        float minPx = (float)(MinStrokeMetres * pxPerMetre), breakPx = (float)(BreakMetres * pxPerMetre);
        for (int i = 0; i < 4; i++)
        {
            bool wheelWorks = work is { } k && (k != TyreWork.Launch || i >= 2) && paved(at[i], car.HeightPx);
            if (!wheelWorks) { last[i] = null; continue; }
            if (last[i] is not { } from) { last[i] = at[i]; continue; }
            float moved = Vector2.Distance(from, at[i]);
            if (moved > breakPx) { last[i] = at[i]; continue; }
            if (moved < minPx) continue;
            Add(new TyreMark(from, at[i], car.HeightPx, Clock, StrengthOf(work!.Value)));
            last[i] = at[i];
        }
    }

    /// <summary>End a frame: forget the wheels of cars no longer watched (traffic replaced out of sight).</summary>
    public void Forget()
    {
        if (_wheels.Count > _seen.Count)
            foreach (var car in new List<Car>(_wheels.Keys))
                if (!_seen.Contains(car)) _wheels.Remove(car);
        _seen.Clear();
    }

    /// <summary>Every mark gone (a fresh chase).</summary>
    public void Clear() { _first = 0; _count = 0; _wheels.Clear(); _seen.Clear(); }

    private void Add(in TyreMark m)
    {
        if (_count == _ring.Length) { _ring[_first] = m; _first = (_first + 1) % _ring.Length; return; }
        _ring[(_first + _count) % _ring.Length] = m;
        _count++;
    }
}

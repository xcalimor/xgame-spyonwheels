using System;
using System.Linq;
using Microsoft.Xna.Framework;

namespace SpyOnWheels;

/// <summary>What happened to the car in one step.</summary>
public enum DriveEvent { None, Scraped, Stopped, Sank }

/// <summary>
/// **The car in the world (WI-016).** The car says where it wants to go; the world says how much of it happens, in small
/// steps: where every point of the car's footprint has a surface near its height (D1) and the car's box overlaps no
/// solid at that height (D2), it goes. Otherwise it slides along what stopped it, keeping the part of its motion along
/// the wall and that share of its speed (Q1: a glancing hit costs little, a head-on one stops it); a sheer edge stops it
/// like a solid (Q2). If it then stands over water, it sinks: back to its start, stopped (Q3, D4).
/// </summary>
public static class Driving
{
    /// <summary>The longest single move tested, in metres: shorter than any wall is thick, so nothing is passed through.</summary>
    public const double SubstepMetres = 0.25;

    /// <summary>How much of the way the car turns toward the wall's line on each scraping move (Q1's slide).</summary>
    public const float AlignShare = 0.35f;

    public static DriveEvent Step(Car car, Drive drive, double dt, IDriveWorld world, CarBody body, double pxPerMetre,
        (Vector2 At, float Heading, float HeightPx) start)
    {
        float headingBefore = car.Heading;
        var wanted = car.Advance(drive, dt);
        // A turn that would push the car's box into something is not taken (turning on the spot against a wall).
        if (!Fits(car.Position, car.Heading, car.HeightPx, world, body, out _, out _) && Fits(car.Position, headingBefore, car.HeightPx, world, body, out _, out _))
            car.TurnTo(headingBefore);

        var result = DriveEvent.None;
        double keep = 1;   // the share of speed kept: the worst scrape of this step, applied once
        float stepPx = (float)(SubstepMetres * pxPerMetre);
        int steps = Math.Max(1, (int)Math.Ceiling(wanted.Length() / stepPx));
        var d = wanted / steps;
        // A car already caught (it started inside something) moves freely until it is out.
        bool caught = !Fits(car.Position, car.Heading, car.HeightPx, world, body, out _, out _);
        for (int i = 0; i < steps; i++)
        {
            if (caught) { car.MoveTo(car.Position + d); caught = !Fits(car.Position, car.Heading, car.HeightPx, world, body, out float ch, out _); if (!caught) car.HeightPx = ch; continue; }
            if (Fits(car.Position + d, car.Heading, car.HeightPx, world, body, out float h, out var normal))
            {
                car.MoveTo(car.Position + d);
                car.HeightPx = h;
                continue;
            }
            // Slide: along the solid's face, or along a cell edge (one axis at a time), whichever goes furthest.
            var tries = new[]
            {
                normal != Vector2.Zero && Vector2.Dot(d, normal) < 0 ? d - normal * Vector2.Dot(d, normal) : Vector2.Zero,
                new Vector2(d.X, 0), new Vector2(0, d.Y),
            }.Where(t => t.LengthSquared() > 1e-6f).OrderByDescending(t => t.LengthSquared());
            bool moved = false;
            foreach (var t in tries)
            {
                if (!Fits(car.Position + t, car.Heading, car.HeightPx, world, body, out float th, out _)) continue;
                car.MoveTo(car.Position + t);
                car.HeightPx = th;
                keep = Math.Min(keep, t.Length() / d.Length());
                // The car turns to run along what it slides on (it stops pressing into it, so a glancing hit costs little).
                var along = Vector2.Normalize(t) * Math.Sign(car.SpeedMs == 0 ? 1 : car.SpeedMs);
                float target = MathF.Atan2(along.X, -along.Y);
                float diff = MathF.IEEERemainder(target - car.Heading, MathF.Tau);
                if (MathF.Abs(diff) < MathHelper.PiOver2 * 0.9f)
                    car.TurnTo(car.Heading + diff * AlignShare);
                result = DriveEvent.Scraped;
                moved = true;
                break;
            }
            if (!moved)
            {
                keep = 0;
                result = DriveEvent.Stopped;
                break;
            }
        }
        car.Slow(keep);

        if (world.SurfaceNear(car.Position, car.HeightPx) is { Kind: SurfaceKind.Water })
        {
            car.Reset(start.At, start.Heading, start.HeightPx);
            return DriveEvent.Sank;
        }
        return result;
    }

    /// <summary>Whether the car's box fits at <paramref name="at"/>: every footprint point on a surface near its height, no
    /// solid overlapped. <paramref name="heightPx"/> is the surface under its centre; <paramref name="normal"/> the solid's
    /// push-out direction when one is hit (zero for a sheer edge or the void).</summary>
    public static bool Fits(Vector2 at, float heading, float carHeightPx, IDriveWorld world, CarBody body, out float heightPx, out Vector2 normal)
    {
        heightPx = carHeightPx;
        normal = Vector2.Zero;
        var shape = body.Shape(at, heading);
        if (world.SurfaceNear(at, carHeightPx) is not { } centre || world.CeilingOver(at, centre.HeightPx)) return false;
        // Each footprint point is reached by walking out from the centre in short steps, each surface near the last: a ramp
        // climbs a little per step and passes; a sheer edge is one whole level at once and stops the walk (D1).
        float walkPx = body.FeelStepPx;
        foreach (var p in body.Feel(at, heading))
        {
            float h = centre.HeightPx;
            int n = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(at, p) / walkPx));
            for (int k = 1; k <= n; k++)
            {
                if (world.SurfaceNear(Vector2.Lerp(at, p, k / (float)n), h) is not { } s) return false;
                h = s.HeightPx;
            }
            // A deck too low over the point is a ceiling: the car stops against it, never rises through it.
            if (world.CeilingOver(p, h)) return false;
        }
        heightPx = centre.HeightPx;
        return !world.HitsSolid(shape, heightPx, out normal);
    }
}

/// <summary>The car's box (D3), world px: its length along its heading and its width across.</summary>
public readonly record struct CarBody(float LengthPx, float WidthPx, float FeelStepPx = 16f)
{
    /// <summary>The box's corners, clockwise from the front left.</summary>
    public Vector2[] Shape(Vector2 at, float heading)
    {
        var f = new Vector2(MathF.Sin(heading), -MathF.Cos(heading));
        var r = new Vector2(-f.Y, f.X);
        float hl = LengthPx / 2, hw = WidthPx / 2;
        return new[] { at + f * hl - r * hw, at + f * hl + r * hw, at - f * hl + r * hw, at - f * hl - r * hw };
    }

    /// <summary>Where the car feels the ground: its corners and the middles of its four sides.</summary>
    public Vector2[] Feel(Vector2 at, float heading)
    {
        var c = Shape(at, heading);
        return new[] { c[0], c[1], c[2], c[3], (c[0] + c[1]) / 2, (c[1] + c[2]) / 2, (c[2] + c[3]) / 2, (c[3] + c[0]) / 2 };
    }
}

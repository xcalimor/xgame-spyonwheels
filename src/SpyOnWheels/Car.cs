using System;
using Microsoft.Xna.Framework;

namespace SpyOnWheels;

/// <summary>What the driver asks of the car this step (the vision's four arrows).</summary>
public readonly record struct Drive(bool Throttle, bool Brake, float Steer);

/// <summary>
/// **The car as plain simulation (WI-015 D4)**, no window, no world: Up accelerates to a top speed, Down brakes (and,
/// once stopped, backs up slowly), Left/Right turn the car, quickly when slow and less as speed grows, as a real car
/// does; a car standing still does not turn. World pixels and seconds; the tuning is in metres and read at the world's
/// scale, so a car is the same car in any world.
/// </summary>
/// <summary>
/// **What a surface does to the tyres (WI-019, his ask: "turning gets harder because tyres get better grip on asphalt than
/// on grass").** <see cref="Grip"/> scales the grip (1 = asphalt); <see cref="DragPerS"/> holds the car back in proportion to
/// its speed, so flat out it settles where the engine and the drag balance. <see cref="MaxDriftDeg"/> is how far the slide
/// may swing from the nose: under power at speed a turn always reaches it, so it is what makes a power turn on asphalt
/// differ from one on grass (his drive: "too small a difference between grass and asphalt").
/// </summary>
public readonly record struct Traction(double Grip, double DragPerS, double MaxDriftDeg)
{
    /// <summary>The road and the bridge decks: the tuning as WI-018 left it.</summary>
    public static readonly Traction Asphalt = new(1.6, 0, 18);   // his drive: "make it feel like the tyres suck onto the road" (grip 1 → 1.6, drift 30° → 18°)

    /// <summary>Everything off the road (D1, D2): 60% of the grip, and flat out it holds ~130 km/h.</summary>
    public static readonly Traction OffRoad = new(0.6, Car.AccelerationMs2 / OffRoadTopSpeedMs, 35);   // loose: the slide swings out to 35°

    /// <summary>Flat out off the road, m/s (~130 km/h).</summary>
    public const double OffRoadTopSpeedMs = 36;
}

public sealed class Car
{
    // The tuning: a first guess for the skeleton's feel, to be felt and changed. Since WI-022 these are the sports car's
    // (Handling.SportsCar); a car drives by its own Handling.
    public const double TopSpeedMs = 46.0;        // ~165 km/h (WI-017 his drive: "the speed could be a bit higher"; was 38)
    public const double ReverseSpeedMs = 6.0;
    public const double AccelerationMs2 = 16.0;    // WI-018 D3 "an aggressive sports car": 0-165 km/h in ~3 s (was 9)
    public const double BrakeMs2 = 50.0;           // WI-018 D3 "more aggressive braking" (22 → 30), his drive "much harder brakes" (→ 50)
    public const double RollDragMs2 = 2.5;         // coasting slows the car
    public const double TurnSlowRad = 2.6;         // turn rate at low speed, rad/s
    public const double TurnFastRad = 0.9;         // turn rate at top speed
    public const double PowerTurnBoost = 1.33;     // WI-018 his drive "drift circle a bit too wide": under power the rear steps out and the nose turns this much faster (in a drift the nose sets the circle)
    public const double TurnFullFromMs = 4.0;      // below this the turn rate fades to nothing at standstill

    // WI-018 grip (his ask: "if I turn while accelerating the car gets a bit harder to steer"): the car keeps its momentum
    // when the nose turns, and the tyres pull the motion back in line with the nose up to this much sideways, m/s2.
    public const double GripMs2 = 40.0;            // coasting or braking: holds nearly every turn the steering asks for
    public const double GripPowerMs2 = 20.0;       // under throttle while turning: half, so it runs wide and slides (D2)
    public const double GripKeepsSpeed = 0.85;     // the share of the sideways speed the tyres turn into forward speed; the rest is scrubbed

    /// <summary>What the ground under the car does to it (WI-019, set by <see cref="Driving"/> from the world each step).</summary>
    public Traction Ground { get; set; } = Traction.Asphalt;

    private readonly double _pxPerMetre;

    /// <summary>The car type's numbers (WI-022); the sports car unless given.</summary>
    public Handling Handling { get; }

    public Car(Vector2 position, float heading, double pxPerMetre, Handling? handling = null)
    {
        Position = position;
        Heading = heading;
        _pxPerMetre = pxPerMetre;
        Handling = handling ?? Handling.SportsCar;
    }

    /// <summary>World px.</summary>
    public Vector2 Position { get; private set; }

    /// <summary>Radians, 0 facing north (up the screen), growing clockwise.</summary>
    public float Heading { get; private set; }

    /// <summary>Metres per second along the heading; negative backing up.</summary>
    public double SpeedMs { get; private set; }

    /// <summary>Metres per second sideways, to the right of the heading (WI-018): the slide. Zero when the tyres hold.</summary>
    public double SlipMs { get; private set; }

    // ---- WI-030 a lively car (his ask: an aggressive V8 start that spins the tyres, and full brakes that make the car hard
    // to hold at high speed; "an arcadish feeling comes before realism") ----

    public const double PowerOversteerRadS2 = 16;  // steering while the rear spins swings the tail (and it keeps swinging a moment after)
    public const double BrakeOversteerRadS2 = 4;   // steering while braking hard at top speed: up to ~0.8 rad/s more
    public const double WheelspinGripLoss = 0.9;   // a full wheelspin takes the grip this far toward a shoved car's loose grip
    public const double BrakeGripLoss = 0.5;       // and full brakes at the top speed this far

    /// <summary>The player's car: its wheelspin and its light rear under braking change how it handles. Computer drivers
    /// cannot catch a slide, so their cars are spared it (they still leave the marks).</summary>
    public bool Lively { get; set; }

    /// <summary>How hard the rear tyres spin this step, 0-1 (full throttle from a standstill: 1).</summary>
    public double WheelspinShare { get; private set; }

    /// <summary>How light the rear is under braking this step, 0-1 (full brakes at the top speed: 1).</summary>
    public double BrakeShare { get; private set; }

    /// <summary>What the driver asked of the car on its last step (WI-021).</summary>
    public Drive LastDrive { get; private set; }

    /// <summary>The unit vector the car faces, in world px axes (y down).</summary>
    public Vector2 Forward => new(MathF.Sin(Heading), -MathF.Cos(Heading));

    /// <summary>Drive freely, no world: the intent applied and the car moved by it.</summary>
    public void Step(Drive drive, double dt) => Position += Advance(drive, dt);

    /// <summary>The intent applied to speed and heading; returns where the car WANTS to move this step (world px). The
    /// world decides how much of it happens (<see cref="Driving"/>).</summary>
    public Vector2 Advance(Drive drive, double dt)
    {
        if (dt <= 0) return Vector2.Zero;
        LastDrive = drive;   // WI-021: what the driver asked, so the tyre marks can see the tyres working
        WheelspinShare = 0;
        BrakeShare = 0;
        if (Spinning) return AdvanceSpinning(dt);
        _lastSteer = drive.Steer;
        if (Airborne) return Velocity * (float)(_pxPerMetre * dt);   // WI-029: in the air nothing the driver does takes
        // WI-020: a wobble turns the nose by itself for a moment; the driver keeps the wheel.
        if (YawRadS != 0)
        {
            TurnTo(WrapAngle(Heading + (float)(YawRadS * dt)));
            YawRadS *= Math.Exp(-WobbleDecayPerS * dt);
            if (Math.Abs(YawRadS) < 0.05) YawRadS = 0;
        }
        _looseFor = Math.Max(0, _looseFor - dt);
        // WI-028: past its top only on speed a slope already gave it (on the level a slide must never carry it past its top).
        double most = Math.Max(Handling.TopSpeedMs, Math.Min(Velocity.Length(), Handling.TopSpeedMs * DownhillOverTop));
        double v = SpeedMs;
        // WI-030: the rear tyres spin under full throttle from low speed (less the faster it goes); the brakes at speed
        // lighten the rear (more the faster it goes).
        double spinBelow = Handling.TopSpeedMs * Handling.WheelspinBelowShare;
        if (drive.Throttle && v >= 0 && v < spinBelow) WheelspinShare = 1 - v / spinBelow;
        if (drive.Brake && v > Handling.BrakeLooseFromMs)
            BrakeShare = Math.Clamp((v - Handling.BrakeLooseFromMs) / Math.Max(1, Handling.TopSpeedMs - Handling.BrakeLooseFromMs), 0, 1);
        if (drive.Throttle) v = v < 0 ? Math.Min(0, v + Handling.BrakeMs2 * dt) : v >= Handling.TopSpeedMs ? v : Math.Min(Handling.TopSpeedMs, v + Handling.AccelerationMs2 * dt);
        else if (drive.Brake) v = v > 0 ? Math.Max(0, v - Handling.BrakeMs2 * dt) : Math.Max(-ReverseSpeedMs, v - Handling.AccelerationMs2 * 0.5 * dt);
        else v = v > 0 ? Math.Max(0, v - RollDragMs2 * dt) : Math.Min(0, v + RollDragMs2 * dt);
        v -= v * Ground.DragPerS * Handling.OffRoadDragScale * dt;   // off the road the ground holds the car back (WI-019 D2)
        if (v > Handling.TopSpeedMs) v = Math.Max(Handling.TopSpeedMs, v - OverTopDragMs2 * dt);   // WI-028: past its top (downhill) the air takes it back
        SpeedMs = v;

        double speed = Math.Abs(v);
        double share = Math.Min(1.0, speed / Handling.TopSpeedMs);
        double rate = Handling.TurnSlowRad + (Handling.TurnFastRad - Handling.TurnSlowRad) * share;
        rate *= Math.Min(1.0, speed / TurnFullFromMs);
        bool steering = Math.Abs(drive.Steer) > 0.01f && speed > 0;
        if (drive.Throttle && steering) rate *= PowerTurnBoost;
        TurnTo(WrapAngle(Heading + (float)(Math.Clamp(drive.Steer, -1f, 1f) * rate * dt * Math.Sign(v))));

        // The tyres: pull the slide back toward the nose, up to the grip; most of what they take becomes forward speed.
        double grip = (drive.Throttle && steering ? Handling.GripPowerMs2 : Handling.GripMs2) * Ground.Grip * dt;
        if (Lively)
        {
            // WI-030: a spinning or light rear lets go: steering then swings the tail out (the nose turns further than
            // asked) and the tyres hold the slide less. Steering the other way catches it.
            double toLoose = 1 - (1 - WheelspinGripLoss * WheelspinShare) * (1 - BrakeGripLoss * BrakeShare);
            grip += (LooseGripMs2 * dt - grip) * toLoose;
            if (steering)
                YawRadS += Math.Clamp(drive.Steer, -1f, 1f) * Math.Sign(v) * dt
                    * (WheelspinShare * PowerOversteerRadS2 * Math.Min(1.0, speed / TurnFullFromMs) + BrakeShare * BrakeOversteerRadS2);
        }
        if (Loose) grip = LooseGripMs2 * dt;
        double take = Math.Min(Math.Abs(SlipMs), grip);
        SlipMs -= Math.Sign(SlipMs) * take;
        // A drift, not a spin: past the largest angle the tyres take the rest too (D2). A shoved car (WI-020) is thrown
        // sideways instead: no cap, and what the tyres take is scrubbed, never turned into forward speed.
        double maxSlip = Math.Tan(MathHelper.ToRadians((float)Ground.MaxDriftDeg)) * Math.Abs(SpeedMs);
        if (!Loose && Math.Abs(SlipMs) > maxSlip) { take += Math.Abs(SlipMs) - maxSlip; SlipMs = Math.Sign(SlipMs) * maxSlip; }
        if (SpeedMs != 0 && !Loose) SpeedMs = Math.Clamp(SpeedMs + Math.Sign(SpeedMs) * take * GripKeepsSpeed, -ReverseSpeedMs, most);
        // Nose and slide together never pass the top speed.
        double total = Math.Sqrt(SpeedMs * SpeedMs + SlipMs * SlipMs);
        if (total > most) { SpeedMs *= most / total; SlipMs *= most / total; }
        return Velocity * (float)(_pxPerMetre * dt);
    }

    /// <summary>The height of the surface the car stands on, world px (WI-016).</summary>
    public float HeightPx { get; set; }

    // ---- WI-028 slopes (his ask: "slow down slightly when going upwards and accelerate downwards") ----

    public const double GravityMs2 = 9.81;
    public const double SlopeShare = 0.5;          // gravity along the slope at half strength: felt, never a wall
    public const double DownhillOverTop = 1.15;    // downhill may carry a car this far past its top speed
    public const double OverTopDragMs2 = 6;        // and on the level it bleeds back to its top this fast

    /// <summary>The car rose <paramref name="riseMetres"/> (negative: fell) while moving: what that costs or gives in speed,
    /// by energy (v² changes by 2 g h, at <see cref="SlopeShare"/>), so a gentle slope and a step of the same height do the
    /// same. The motion keeps its direction.</summary>
    public void Climb(double riseMetres)
    {
        if (riseMetres == 0) return;
        var v = Velocity;
        double speed = v.Length();
        if (speed < 1e-6) return;
        double after = Math.Sqrt(Math.Max(0, speed * speed - 2 * GravityMs2 * SlopeShare * riseMetres));
        SpeedMs *= after / speed;
        SlipMs *= after / speed;
    }

    public void MoveTo(Vector2 at) => Position = at;

    /// <summary>WI-027: put the car where something else moves it (a train vehicle by its timetable): place, heading, motion
    /// (m/s, world axes) and height, with no driving.</summary>
    public void Carry(Vector2 at, float heading, Vector2 velocityMs, float heightPx)
    {
        Position = at;
        Heading = heading;
        SpeedMs = Vector2.Dot(velocityMs, Forward);
        SlipMs = Vector2.Dot(velocityMs, Right);
        HeightPx = heightPx;
    }
    /// <summary>The car's motion in the world, m/s (y down): along the nose plus the slide.</summary>
    public Vector2 Velocity => Forward * (float)SpeedMs + Right * (float)SlipMs;

    /// <summary>The unit vector to the car's right.</summary>
    public Vector2 Right => new(MathF.Cos(Heading), MathF.Sin(Heading));

    /// <summary>Turn the nose; the motion keeps its direction in the world (WI-018 D1), so it is split anew along the new nose.</summary>
    public void TurnTo(float heading)
    {
        var velocity = Velocity;
        Heading = heading;
        SpeedMs = Vector2.Dot(velocity, Forward);
        SlipMs = Vector2.Dot(velocity, Right);
    }

    /// <summary>Lose speed: <paramref name="keep"/> is the share kept (0 stops the car).</summary>
    public void Slow(double keep) { SpeedMs *= Math.Clamp(keep, 0, 1); SlipMs *= Math.Clamp(keep, 0, 1); }

    /// <summary>Back to a place, stopped (the water's answer, Q3).</summary>
    public void Reset(Vector2 at, float heading, float heightPx)
    {
        Position = at;
        Heading = heading;
        HeightPx = heightPx;
        SpeedMs = 0;
        SlipMs = 0;
        YawRadS = 0;
        Spinning = false;
        _looseFor = 0;
        Airborne = false; AirMetres = 0; VzMs = 0; _groundVzMs = 0;
    }

    // ---- WI-029 crests and jumps (his ask: "a small jump when going max speed up a slope and the slope ends ... to make
    // it harder to control the car. This will force the driver to slow down not to jump") ----

    public const double JumpFromMs = 30;           // ~108 km/h: slower than this a car keeps to the ground over any crest
    public const double MaxLaunchMs = 5;           // straight up, at its top speed: ~0.6 m high, ~0.5 s, ~20 m long
    public const double CrestFromMs = 2;           // the ground was rising at least this fast under it (a climb)...
    public const double CrestDropShare = 0.5;      // ...and now rises at less than this share of that: the slope ended
    public const double AirGravityMs2 = 2 * GravityMs2;   // arcade: a short hop, not a float
    public const double LandLooseSeconds = 0.35;   // after landing the tyres skid this long (a full-height landing)
    public const double LandKeep = 0.96;           // and the landing costs this share of the speed
    public const double LandSteerKickRadS = 1.2;   // steering held in the air turns the car this hard as it lands

    private double _groundVzMs;
    private float _lastSteer;

    /// <summary>Off the ground (a jump over a crest): no throttle, brakes or steering until it lands.</summary>
    public bool Airborne { get; private set; }

    /// <summary>How high above the ground under it, metres (0 on the ground).</summary>
    public double AirMetres { get; private set; }

    /// <summary>Its own vertical speed while in the air, m/s (up positive).</summary>
    public double VzMs { get; private set; }

    /// <summary>
    /// The ground under the car rose <paramref name="riseMetres"/> this step (the driving says so): on the ground the car
    /// follows it, unless it was climbing fast and the climb just ended, at more than <see cref="JumpFromMs"/>, where it
    /// leaves the ground in proportion to how much faster it went. In the air it flies (its own vertical speed, under
    /// <see cref="AirGravityMs2"/>) until it meets the ground again.
    /// </summary>
    public void FollowGround(double riseMetres, double dt)
    {
        if (dt <= 0) return;
        double groundVz = riseMetres / dt;
        if (Airborne)
        {
            AirMetres += VzMs * dt - riseMetres;
            VzMs -= AirGravityMs2 * dt;
            if (AirMetres <= 0) Land();
        }
        else
        {
            double speed = Velocity.Length();
            bool crest = _groundVzMs > CrestFromMs && groundVz < _groundVzMs * CrestDropShare;
            if (crest && speed > JumpFromMs)
            {
                double share = Math.Clamp((speed - JumpFromMs) / Math.Max(1, Handling.TopSpeedMs - JumpFromMs), 0, 1);
                VzMs = MaxLaunchMs * share;
                AirMetres = VzMs * dt;
                Airborne = AirMetres > 0;
            }
        }
        _groundVzMs = Airborne ? 0 : groundVz;
    }

    private void Land()
    {
        double impact = Math.Clamp(-VzMs / MaxLaunchMs, 0, 1.5);
        Airborne = false;
        AirMetres = 0;
        VzMs = 0;
        SpeedMs *= LandKeep;
        SlipMs *= LandKeep;
        _looseFor = Math.Max(_looseFor, LandLooseSeconds * impact);
        YawRadS += _lastSteer * LandSteerKickRadS * impact;
    }

    // ---- WI-020 contact: pushed, wobbling, spun out ----

    public const double WobbleDecayPerS = 5.0;     // a wobble's own turning dies away in well under a second
    public const double SpinFrictionMs2 = 14.0;    // a spinning car skids to a stop (the vision: "regained by braking to a stop")
    public const double SpinDecayPerS = 0.7;       // and its spin slows as it goes
    public const double LooseSeconds = 0.5;        // after a shove the tyres let the car be thrown, this long
    public const double LooseGripMs2 = 8.0;        // a shoved car skids sideways as tyres really do (~0.8 g), not with the arcade grip that holds a drift

    private double _looseFor;

    /// <summary>The nose turning by itself, rad/s, clockwise positive: a wobble (the driver still steers) or a spin.</summary>
    public double YawRadS { get; private set; }

    /// <summary>Out of control (the vision's spin-out): no throttle, brake or steering until the car has skidded to a stop.</summary>
    public bool Spinning { get; private set; }

    /// <summary>Just shoved: thrown with the push, not held by the tyres (<see cref="LooseSeconds"/>).</summary>
    public bool Loose => _looseFor > 0;

    /// <summary>A change of motion from a contact, m/s in world axes (y down).</summary>
    public void Push(Vector2 deltaMs)
    {
        var v = Velocity + deltaMs;
        SpeedMs = Vector2.Dot(v, Forward);
        SlipMs = Vector2.Dot(v, Right);
        _looseFor = LooseSeconds;
    }

    /// <summary>A contact's turn on the car: <paramref name="spinOut"/> takes the wheel away until it stops; otherwise a wobble.</summary>
    public void Kick(double yawRadS, bool spinOut)
    {
        YawRadS += yawRadS;
        if (spinOut) Spinning = true;
    }

    /// <summary>Spinning: the motion keeps its world direction and skids down, the nose turns by itself; once stopped the
    /// driver has the car again.</summary>
    private Vector2 AdvanceSpinning(double dt)
    {
        var v = Velocity;
        double speed = v.Length();
        double slower = Math.Max(0, speed - SpinFrictionMs2 * Ground.Grip * dt);
        v = speed > 0 ? v * (float)(slower / speed) : Vector2.Zero;
        Heading = WrapAngle(Heading + (float)(YawRadS * dt));
        YawRadS *= Math.Exp(-SpinDecayPerS * dt);
        SpeedMs = Vector2.Dot(v, Forward);
        SlipMs = Vector2.Dot(v, Right);
        if (slower < 0.3)
        {
            SpeedMs = 0; SlipMs = 0; YawRadS = 0;
            Spinning = false;
            return Vector2.Zero;
        }
        return v * (float)(_pxPerMetre * dt);
    }

    private static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }
}

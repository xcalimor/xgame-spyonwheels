using System;
using Microsoft.Xna.Framework;

namespace SpyOnWheels;

/// <summary>The vision's four contacts (§6 Collision Resolution), seen from the attacker.</summary>
public enum ContactKind { Rear, Angled, Side, Frontal }

/// <summary>What one contact did: its kind, how fast the cars closed (m/s), the turn it gave the struck car, whether that
/// car spun out, and the health it cost the attacker.</summary>
public readonly record struct ContactResult(ContactKind Kind, double ClosingMs, double YawRadS, bool SpunOut, double Damage);

/// <summary>
/// **Car meets car (WI-020 step 2).** Two boxes overlap; the contact is read from the struck car's side of it and from how
/// far apart the two cars face (the vision, §6):
/// <list type="bullet">
/// <item>**Frontal**: nose to nose, facing at least <see cref="FrontalDeg"/> apart: the attacker dies. The vision calls this
/// angle the single most important tuning number in the game.</item>
/// <item>**Rear**: from straight behind: the target is shoved on and destabilised most (a hard one spins it), the attacker
/// barely feels it.</item>
/// <item>**Angled**: into the target at an angle: the wider the angle, the harder it turns, and past
/// <see cref="SpinOutYawRadS"/> it spins out instead of wobbling.</item>
/// <item>**Side**: drawn alongside, then steered in: the target is thrown sideways, not turned.</item>
/// </list>
/// The push itself is a near-plastic exchange of momentum along the contact's line, weighed by the two cars' masses (WI-022). Every number here is
/// a first guess, to be found in play (vision §11).
/// </summary>
public static class Contact
{
    public const double FrontalDeg = 150;        // facing this far apart, nose to nose: death
    public const double ParallelDeg = 30;        // within this, a rear or side hit; beyond, angled
    public const double MinClosingMs = 0.5;      // slower than this the cars just lean on each other
    public const double Restitution = 0.2;       // how much the cars bounce apart
    public const double SideThrow = 1.6;         // a side hit throws harder than the momentum alone (the attacker steers in)
    public const double RearYawPerMs = 0.06;     // rad/s of turn per m/s closing, from behind (more off-centre, more turn)
    public const double AngledYawPerMs = 0.16;   // rad/s per m/s closing, at a right angle
    public const double SideYawPerMs = 0.02;
    public const double SpinOutYawRadS = 2.2;    // a kick past this takes the wheel away
    public const double RearSpinClosingMs = 14;  // from behind, closing this fast spins the target whatever the offset
    public const double PushDamage = 6;          // health per push, attacker
    public const double DamagePerMs = 0.5;       // plus this per m/s the struck car was faster (the slower car takes more)

    /// <summary>WI-022: how much harder a struck car turns for the masses: 1 between equals, more when the attacker is the
    /// heavier (a light car is spun more easily), less when it is the lighter. Softened (the square root of the mass share): the
    /// full share made the pickup unstoppable from behind on a straight (WI-022 measure: the first hit lands at 64 km/h closing,
    /// the full share asks 67), which would have turned his chase into one nobody wins.</summary>
    public static double MassTurn(double attackerKg, double struckKg) => Math.Sqrt(2 * attackerKg / (attackerKg + struckKg));

    /// <summary>Where on <paramref name="struck"/> the contact lands and how far apart the cars face, read into a kind.
    /// <paramref name="normal"/> points from the struck car toward the attacker.</summary>
    public static ContactKind Classify(Car attacker, Car struck, Vector2 normal)
    {
        double apart = Math.Abs(MathHelper.ToDegrees(MathHelper.WrapAngle(attacker.Heading - struck.Heading)));
        float onNose = Vector2.Dot(normal, struck.Forward);
        // Nose to nose: the attacker's front meets the struck car's front.
        if (apart >= FrontalDeg && onNose > 0.3f) return ContactKind.Frontal;
        if (apart <= ParallelDeg)
        {
            if (onNose < -0.7f) return ContactKind.Rear;
            if (Math.Abs(Vector2.Dot(normal, struck.Right)) > 0.7f) return ContactKind.Side;
        }
        return ContactKind.Angled;
    }

    /// <summary>The contact resolved on both cars: momentum exchanged along <paramref name="normal"/> (struck → attacker), the
    /// struck car turned by the kind's rule, the attacker's damage. Returns null when the cars are not closing.</summary>
    public static ContactResult? Resolve(Car attacker, Car struck, Vector2 normal)
    {
        // How fast the attacker comes at the struck car along the line between them.
        double closing = Vector2.Dot(attacker.Velocity - struck.Velocity, -normal);
        if (closing < MinClosingMs) return null;
        var kind = Classify(attacker, struck, normal);
        double faster = Math.Max(0, struck.Velocity.Length() - attacker.Velocity.Length());   // before the push changes them
        // WI-022 mass: the impulse of two masses meeting; each car's change of speed is the impulse over its own mass, so
        // the heavier car shoves the lighter further and is shoved less itself (equal masses: half the closing each, as before).
        double ma = attacker.Handling.MassKg, ms = struck.Handling.MassKg;
        double j = closing * (1 + Restitution) * ma * ms / (ma + ms);
        double throwShare = kind == ContactKind.Side ? SideThrow : 1;
        struck.Push(-normal * (float)(j / ms * throwShare));
        attacker.Push(normal * (float)(j / ma));

        // Which way the struck car turns: the push on it, acting where the attacker's centre lies off its own.
        var lever = (attacker.Position - struck.Position);
        var force = -normal;
        double torque = lever.X * force.Y - lever.Y * force.X;
        double sign = torque >= 0 ? 1 : -1;
        double apart = Math.Abs(MathHelper.WrapAngle(attacker.Heading - struck.Heading));
        double yaw = kind switch
        {
            // Off-centre from behind turns it more: the attacker's centre across the struck car's line, as a share of a car's width.
            ContactKind.Rear => RearYawPerMs * closing * (0.5 + Math.Min(1.5, Math.Abs(Vector2.Dot(lever, struck.Right)) / Math.Max(1f, lever.Length()) * 4)),
            ContactKind.Angled => AngledYawPerMs * closing * Math.Max(0.25, Math.Sin(apart)),
            ContactKind.Side => SideYawPerMs * closing,
            _ => 0,
        } * sign * MassTurn(ma, ms);
        bool spin = kind != ContactKind.Frontal && (Math.Abs(yaw) > SpinOutYawRadS || (kind == ContactKind.Rear && closing * MassTurn(ma, ms) >= RearSpinClosingMs));   // WI-022: a heavier target takes a harder hit to spin
        if (spin && Math.Abs(yaw) < SpinOutYawRadS) yaw = SpinOutYawRadS * sign;
        struck.Kick(yaw, spin);

        // The slower car takes more (vision §3): the attacker pays more when the struck car was the faster.
        double damage = kind == ContactKind.Frontal ? double.PositiveInfinity : PushDamage + DamagePerMs * faster;
        return new ContactResult(kind, closing, yaw, spin, damage);
    }
}

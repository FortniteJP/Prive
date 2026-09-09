using AFortOnlineBeacon.Core.Math;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     What a thrown item does when it goes off, beyond the damage.
///
///     For most of the throwables that IS the item. A shockwave grenade does 5 damage and throws you
///     2500 units; a boogie bomb does none at all and makes you dance for five seconds. Until now
///     every one of them exploded for its damage and nothing else, so a boogie bomb was a very weak
///     frag and a shockwave grenade was a firework.
///
///     The numbers are the game's own - see the generator for why AthenaGameData is the right source
///     rather than the projectile Blueprints' properties.
/// </summary>
internal static partial class FortGrenadeEffects {
    /// <summary>The effect for a thrown item, or null when it just explodes.</summary>
    public static (EGrenadeEffect Kind, float Radius, float LaunchVelocity, float AddToZ,
                   float Duration, float Period, float HitDelay, bool FriendlyFire,
                   bool Damages, bool FallDamage)? For(string? itemName) {
        if (itemName == null) return null;

        foreach (var row in Rows) {
            if (!row.Item.Equals(itemName, StringComparison.OrdinalIgnoreCase)) continue;
            return (row.Kind, row.Radius, row.LaunchVelocity, row.AddToZ,
                    row.Duration, row.Period, row.HitDelay, row.FriendlyFire,
                    row.Damages, row.FallDamage);
        }

        return null;
    }

    /// <summary>
    ///     The velocity a shockwave or impulse grenade throws a pawn at - the projectile Blueprint's
    ///     own formula, read out of its bytecode rather than reconstructed from the row names:
    ///
    ///         dir    = Normal(victim - blast)
    ///         v      = dir * LaunchVelocity
    ///         z      = FMax(v.Z + AddToZBeforeLaunch, 500)
    ///         launch = (v.X, v.Y, z)
    ///
    ///     TWO THINGS THAT LOOK LIKE DETAILS AND ARE NOT. AddToZBeforeLaunch is added to the SCALED
    ///     vector, in velocity units, not to the direction before normalising - the name reads the
    ///     other way and the first version of this did it the other way. And the Z component has a
    ///     FLOOR OF 500, a plain constant in the graph, which is what guarantees you always go UP:
    ///     without it a grenade landing at your feet throws you almost flat, which is exactly the
    ///     "it does not launch upward" this was reported as.
    ///
    ///     Distance does not scale it. The row is a single value and there is no falloff row - a
    ///     shockwave throws everything inside its 500-unit radius equally hard, which is how it
    ///     plays.
    /// </summary>
    public static FVector LaunchVelocityFor(FVector blast, FVector victim, float launchVelocity, float addToZ) {
        var dx = victim.X - blast.X;
        var dy = victim.Y - blast.Y;
        var dz = victim.Z - blast.Z;

        var length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        // Dead centre: straight up, rather than a division by zero or a random direction. The
        // graph's own Normal() has a 0.0001 tolerance and returns zero below it, which with the
        // floor below comes out as straight up too.
        if (length < 1e-3f) return new FVector { X = 0f, Y = 0f, Z = MathF.Max(addToZ, MinimumLaunchZ) };

        return new FVector {
            X = dx / length * launchVelocity,
            Y = dy / length * launchVelocity,
            Z = MathF.Max(dz / length * launchVelocity + addToZ, MinimumLaunchZ)
        };
    }

    /// <summary>
    ///     The FMax the projectile graph applies to the launch's Z: a literal 500 in the bytecode,
    ///     not a curve row, so it is the same for every launcher.
    /// </summary>
    private const float MinimumLaunchZ = 500f;

    /// <summary>
    ///     The two gameplay cues a SHOCKWAVE puts on whoever it throws - and the answer to "the low
    ///     gravity is more than just no fall damage".
    ///
    ///     `GE_Athena_ShockGrenade_FX` grants `GA_Athena_ShockGrenade_RemoveFX`, whose whole content
    ///     is these two tags: a LOOPING cue for the flight and a LANDING one for the arrival. The
    ///     names give the game away - it reuses the Low Gravity Rock's effects, which is what a
    ///     shockwave throw looks and sounds like.
    /// </summary>
    public const string LowGravLoopingCue = "GameplayCue.Athena.ForagedItem.LowGravRock.Looping";

    /// <summary>See <see cref="LowGravLoopingCue" />.</summary>
    public const string LowGravLandingCue = "GameplayCue.Athena.ForagedItem.LowGravRock.Land";

    /// <summary>
    ///     The LAUNCH burst, and the one that can actually be sent today.
    ///
    ///     WHICH RPC A CUE NEEDS IS DECIDED BY ITS NOTIFY'S CLASS, which is the thing the first
    ///     attempt got wrong: `GCN_Athena_LowGravity_C` is a **FortGameplayCueNotify_Looping** and
    ///     answers to Added/WhileActive, so sending its tag as EXECUTED does nothing at all - which
    ///     is exactly what "there is still no low gravity effect" was. Its two siblings,
    ///     `GCN_Athena_LowGravity_Liftoff_C` and `_Land_C`, are **FortGameplayCueNotify_Simple** and
    ///     do answer to Executed.
    ///
    ///     So the burst at the launch and the thump on landing are both reachable with the RPC this
    ///     server already has; the continuous aura between them needs Added, and needs a way to
    ///     REMOVE it afterwards that does not exist here yet (cue removal rides the ASC's replicated
    ///     cue list rather than an RPC).
    /// </summary>
    public const string LowGravLiftoffCue = "GameplayCue.Athena.ForagedItem.LowGravRock.Liftoff";

    /// <summary>
    ///     The ability a BOOGIE BOMB runs on whoever it catches, by path - its CDO, like every other
    ///     ability this server grants.
    ///
    ///     `GA_DanceGrenade_Stun_C` is a gift: its NetExecutionPolicy is **ServerInitiated**, which is
    ///     the exact property that lets this server start an ability on a client at all (the emote
    ///     recipe rests on the same thing - see FortEmoteSystem). It carries its own AnimMontage,
    ///     `Emote_DG_Disco_M`, so onlookers can be given the dance the same way an emote's is.
    /// </summary>
    public const string DanceStunAbilityPath =
        "/Game/Athena/Items/Consumables/DanceGrenade/GA_DanceGrenade_Stun.Default__GA_DanceGrenade_Stun_C";

    /// <summary>The montage that ability plays - what everyone else sees the victim doing.</summary>
    public const string DanceStunMontagePath =
        "/Game/Animation/Game/MainPlayer/Emotes/Emote_DG_Disco_M.Emote_DG_Disco_M";
}

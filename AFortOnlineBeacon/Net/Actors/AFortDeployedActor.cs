namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     A thrown item that ends its life as a PLACED ACTOR rather than as an explosion - a sneaky
///     snowman, a shield bubble, a firework mortar's emplacement.
///
///     WHY A SEPARATE TYPE FROM ABuildingActor, AND IT IS NOT TIDINESS. Fortnite's building classes
///     fork, and the fork is above almost everything this server sends:
///
///         ABuildingActor
///           +- ABuildingSMActor         - a wall, a floor, a tree, a prop; handles 38-67
///           +- ABuildingGameplayActor   - a llama, a shield bubble, a mortar emplacement
///
///     The two are SIBLINGS. They share ABuildingActor's handles 16-37 exactly, and then diverge: a
///     building piece has TextureData[0] at 38 where a gameplay actor has something else entirely.
///     So sending a gameplay actor the layout this project uses for a wall - MinimalReplicationProxy,
///     BuildingAnimation, the damage cue - names handles its class does not have, and a real client
///     answers that by CLOSING THE CONNECTION:
///
///         LogRep: Error: ReceiveProperties: Invalid property terminator handle - Handle=44
///         LogNet: Error: UActorChannel::ProcessBunch: Replicator.ReceivedBunch failed. Closing connection.
///
///     The supply llama hit exactly this and is why <see cref="NativeRepLayouts.HandlePrefix" />
///     exists. This type is the same shape of answer for the deployables.
///
///     WHICH SIDE OF THE FORK EACH ITEM IS ON WAS CHECKED, not assumed (`pakreader supers`):
///
///         Athena_Prop_SneakySnowman_C   -> BuildingProp -> ... -> ABuildingSMActor   (a piece)
///         BGA_Athena_SilverBlazerCore_C -> BGA_Athena_WithGravity_Parent_C -> ABuildingGameplayActor
///         B_BGA_FireworksMortar_Holder_C-> ABuildingGameplayActor
///
///     so the SNOWMAN is spawned as an ordinary ABuildingActor and only the other two need this.
///
///     THE CLASS COMES FROM THE PATH, THE C# TYPE DECIDES THE REP LAYOUT, AND BOTH HAVE TO BE ASKED
///     FOR TOGETHER. `GUClassArray.StaticClassForPath` is keyed by (type, path) and
///     `UClass.CreateDefaultObject` does `Activator.CreateInstance(Type)`, so the UClass a deployable
///     is spawned from must be built with THIS type - `StaticClassForPath<AFortDeployedActor>(path)`.
///     `ABuildingActor.ClassForPath` bakes `typeof(ABuildingActor)` instead, and since
///     `UWorld.SpawnActor<T>` is only a cast, using it here produced an InvalidCastException that the
///     deploy path caught and logged as one line. Every gameplay-actor deployable was silently never
///     spawned; the snowman is the only row with GameplayActor false and the only one that worked.
/// </summary>
public class AFortDeployedActor : ABuildingActor {
    /// <summary>
    ///     The class path this was deployed as - kept for the log, since the C# type is the same for
    ///     every deployable and says nothing about which item produced it.
    ///
    ///     Settable rather than init-only because the actor is built by Activator.CreateInstance
    ///     from its UClass, which takes no arguments; the deploy path fills this in afterwards.
    /// </summary>
    public string DeployedClassPath { get; set; } = string.Empty;

    /// <summary>
    ///     Actors that go when this one does - the shield bubble's DOME, bound to its CORE.
    ///
    ///     This is the core's own Blueprint, not a choice: its `Destroyed` event (bound to its own
    ///     OnDied at BeginPlay) spawns the break emitter, hides itself, disables collision, calls
    ///     `SpawnedBGA->End()` and then `K2_DestroyActor`. So breaking the device ends the shield.
    ///     The dome's own `End` is a plain BlueprintCallable with no Net flag, so nothing a server
    ///     can send would run it on a client; taking the actor off the wire is what is left, and the
    ///     client removes it with its channel.
    ///
    ///     Done from <see cref="Destroyed" /> because that is the one point every exit passes -
    ///     shot to death (BuildingStructuralSupportSystem tears it down 0.35 s after it breaks, after
    ///     its break animation has gone out), the 30-second lifespan, or anything added later.
    /// </summary>
    public List<AActor> GoesDownWith { get; } = new();

    /// <summary>
    ///     Takes no damage at all. The dome is `StaticGameplayTags = BuildingActor.NonDestructable`
    ///     with the `TestNearInfinite` attribute category, and until this existed it had the
    ///     default 200 HP of any building here - so a grenade that landed next to a shield bubble
    ///     destroyed the SHIELD, which is precisely the thing a shield bubble exists not to allow.
    ///     The core is not flagged: breaking it is the intended counter, and it is what ends the dome.
    /// </summary>
    public bool Indestructible { get; set; }

    /// <summary>Send bHidden - for a class whose CDO is hidden and whose instance must not be.</summary>
    public bool bReplicateVisibility { get; set; }

    /// <summary>Send Instigator and Owner - for a class whose client-side Blueprint reads them.</summary>
    public bool bReplicateInstigator { get; set; }

    protected override void Destroyed() {
        base.Destroyed();

        foreach (var bound in GoesDownWith) {
            if (bound.IsPendingKillPending()) continue;

            Console.WriteLine($"AFortDeployedActor: '{GetFName()}' is gone, so '{bound.GetFName()}' goes " +
                              "with it.");
            bound.Destroy();
        }

        GoesDownWith.Clear();
    }
}

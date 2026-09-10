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
///     The CLASS comes from the path, not from this C# type - see ABuildingActor.ClassForPath and
///     GUClassArray.StaticClassForPath. This type only decides which RepLayout the server uses.
/// </summary>
public class AFortDeployedActor : ABuildingActor {
    /// <summary>
    ///     The class path this was deployed as - kept for the log, since the C# type is the same for
    ///     every deployable and says nothing about which item produced it.
    /// </summary>
    public string DeployedClassPath { get; init; } = string.Empty;
}

using AFortOnlineBeacon.Core.Objects;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     UFortControllerComponent_Interaction - the PlayerController's `InteractionComp`, and the reason
///     chests, ammo boxes and doors did nothing for so long.
///
///     `ServerAttemptInteract` IS NOT A PLAYERCONTROLLER RPC. Every interaction in the game arrives as
///     a SUB-OBJECT content block naming this component, which the live capture states outright:
///
///         Sent RPC: FortControllerComponent_Interaction
///           ...Athena_PlayerController_C_2147479011.InteractionComp::ServerAttemptInteract
///
///     and which this server's own log then showed from the other side:
///
///         SUB-OBJECT content block ... path='PersistentLevel.Athena_PlayerController_C.InteractionComp'
///           resolved=NULL
///         skipping 745 bits from unresolved sub-object
///
///     Registering the handler on the controller could therefore never work, however correct the
///     parameter decoding was - the field index is looked up in the COMPONENT's ClassNetCache, and the
///     block was being skipped wholesale before it got that far.
///
///     This object exists purely so that path resolves. It holds no state: the client owns the whole
///     interaction decision (what is in range, what is interactable, how long the hold was) and this
///     server only has to receive the result. The component is never replicated outwards either -
///     nothing here has a Net flag - so it needs no RepLayout and no channel of its own.
/// </summary>
public class UFortControllerComponent_Interaction : UObject {
    /// <summary>
    ///     The name the client exports, and therefore the only name this can have. It is a default
    ///     subobject of the controller in the real game, which is what lets the client name it by
    ///     PATH rather than needing a server-assigned id - a stably-named object.
    /// </summary>
    public const string SubObjectName = "InteractionComp";

    /// <summary>The controller this hangs off - the actor an interaction is credited to.</summary>
    public APlayerController? Owner { get; set; }
}

using AFortOnlineBeacon.Core;
using AFortOnlineBeacon.Net.Actors;

namespace AFortOnlineBeacon.Net.Replication;

/// <summary>
///     Ground-truth FRepLayout property tables for the native engine classes AActor/AController/
///     APlayerController/APawn map to - counterpart to NativeClassNetCache, but for ordinary
///     UPROPERTY replication (FRepLayout) rather than RPC/legacy-field dispatch (ClassNetCache).
///
///     Property order and struct-recursion shape are ground-truthed the same way
///     NativeClassNetCache's field membership was: a live offset dump of the real Fortnite 10.40
///     client (PriveDev/UEDumper - see DumpNetFields/DumpStructFields), plus vanilla UE 4.23
///     RepLayout.cpp/EngineTypes.h source for which structs opt into a native NetSerialize
///     (FRepMovement: yes, atomic; FRepAttachment: no, recurses into its 6 members). Computing
///     handles from this table reproduces RemoteRole=5, which is confirmed correct (a real client
///     applies it and shows the right value), and Role=14, which is NOT confirmed - three separate
///     handle values (9, 14, 22) have each been tried live and each broke the connection the same
///     way ("Invalid property terminator handle"), including 14 despite it being what every
///     available check (live offset dump, live struct-member dump, live StructFlags atomicity
///     read, and a full-bunch hex dump proving our own wire bytes match a hand computation exactly)
///     agrees is correct. An attempt to hook the real client's FRepLayout::ReceiveProperties
///     directly and dump its actual Cmds table (PriveDev/UEDumper, replayout_hook.cpp) also didn't
///     pan out - the hook never fired, most likely because the compiler inlined that function into
///     its caller. So "Role" stays declared here (for correct downstream handle numbering) but out
///     of UActorChannel.InitialReplicatedProperties until this gets resolved some other way. Most
///     of these properties don't have a real C# field on the corresponding class yet either, so
///     they're declared with no value getter - they still reserve their correct handle, they just
///     can't be named in a changed set.
/// </summary>
internal static class NativeRepLayouts {
    private static FRepPropertyDef Reserved(string name, ERepPropertyKind kind = ERepPropertyKind.Bool) => new() {
        Name = name,
        Kind = kind
    };

    private static readonly FRepPropertyDef[] ActorProps = {
        Reserved("bHidden"),
        Reserved("bReplicateMovement"),
        Reserved("bTearOff"),
        Reserved("bCanBeDamaged"),
        new() {
            // NOT swapped on send: SendProperties_r (RepLayout.cpp) reads RepState->SavedRole /
            // SavedRemoteRole directly for these two cmds, and CompareRoleProperties populates
            // those Saved* fields straight from the same-named actual property (SavedRemoteRole =
            // Actor.RemoteRole, no cross-field substitution). The swap only happens on the
            // receiving end (ReceivePropertyHelper redirects the WRITE destination via
            // Parent.RoleSwapIndex) - a prior pass here mistakenly added a send-side swap too,
            // which was wrong per a direct re-read of RepLayout.cpp's SendProperties_r.
            Name = "RemoteRole",
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = (int) ENetRole.ROLE_MAX,
            GetByteValue = obj => (byte) ((AActor) obj).RemoteRole
        },
        Reserved("ReplicatedMovement", ERepPropertyKind.StructAtomic),
        new() {
            Name = "AttachmentReplication",
            Kind = ERepPropertyKind.StructRecurse,
            Children = new[] {
                Reserved("AttachParent"),
                Reserved("LocationOffset"),
                Reserved("RelativeScale3D"),
                Reserved("RotationOffset"),
                Reserved("AttachSocket"),
                Reserved("AttachComponent")
            }
        },
        Reserved("Owner"),
        new() {
            // Not swapped on send either - see RemoteRole above.
            Name = "Role",
            Kind = ERepPropertyKind.ByteEnum,
            EnumMaxValue = (int) ENetRole.ROLE_MAX,
            GetByteValue = obj => (byte) ((AActor) obj).Role
        },
        Reserved("Instigator")
    };

    private static readonly FRepPropertyDef[] ControllerProps = ActorProps.Concat(new[] {
        Reserved("PlayerState"),
        Reserved("Pawn")
    }).ToArray();

    private static readonly FRepPropertyDef[] PlayerControllerProps = ControllerProps.Concat(new[] {
        Reserved("TargetViewRotation"),
        Reserved("SpawnLocation")
    }).ToArray();

    private static readonly FRepPropertyDef[] PawnProps = ActorProps.Concat(new[] {
        Reserved("RemoteViewPitch"),
        Reserved("PlayerState"),
        Reserved("Controller")
    }).ToArray();

    public static readonly FRepLayout Actor = new(ActorProps);
    public static readonly FRepLayout Controller = new(ControllerProps);
    public static readonly FRepLayout PlayerController = new(PlayerControllerProps);
    public static readonly FRepLayout Pawn = new(PawnProps);

    public static FRepLayout Get(AActor actor) => actor switch {
        APlayerController => PlayerController,
        AController => Controller,
        APawn => Pawn,
        _ => Actor
    };
}

using AFortOnlineBeacon.Core.Objects;

namespace AFortOnlineBeacon.Net.Actors;

/// <summary>
///     AFortSprayDecalInstance - the actor that IS a spray on a wall, spawned from
///     `/Game/Athena/Cosmetics/Sprays/BP_SprayDecal.BP_SprayDecal_C`.
///
///     WHY THE SERVER HAS TO SPAWN IT. `GAB_Spray_Generic`'s entire body sits behind
///     `EX_JumpIfNot IsServer` (read out of the Blueprint with Tools/BlueprintDump): the sprayer's own
///     client plays the montage and does nothing else, and the decal is a REPLICATED ACTOR the
///     authority creates. So a spray that "works" - ability granted, montage played, ServerEndAbility
///     received - still paints nothing until this exists. See [[emote-wire-flow]].
///
///     It derives from ABuildingSMActor, which is why this class derives from
///     <see cref="ABuildingActor"/>: every handle up to 67 is inherited unchanged and already
///     replicated by this project, and the decal adds exactly one replicated property of its own
///     (`NumReplicatedProperties = 1` on the Blueprint) - `SprayInfo`, an FFortSprayDecalRepPayload
///     that RepLayout flattens into handles 69-72. Only 69 carries anything.
///
///     Not damageable and not part of the structural grid: the CDO sets bCanBeDamaged false and
///     bSurpressHealthBar true, and a decal supports nothing.
/// </summary>
public class AFortSprayDecalInstance : ABuildingActor {
    /// <summary>
    ///     The class the client resolves this actor as. A single shared UClass, so every decal in a
    ///     match exports one path and then travels as a NetGUID.
    /// </summary>
    public const string ClassPath = "/Game/Athena/Cosmetics/Sprays/BP_SprayDecal.BP_SprayDecal_C";

    public static UClass Class => GUClassArray.StaticClassForPath<AFortSprayDecalInstance>(ClassPath);

    /// <summary>
    ///     SprayInfo.SprayAsset - wire handle 69, the SPID_* item definition. Everything visible about
    ///     a spray comes from here: the client reads DecalMaterial/DecalTexture off the asset itself,
    ///     which is why the server never has to know what the picture is.
    /// </summary>
    public UObject? SprayAsset { get; set; }

    /// <summary>
    ///     The ability sets this by name on the deferred spawn (`SetFloatPropertyByName "DecalSize"`)
    ///     and it is 96 there, overriding the CDO's own 128. NOT replicated - it is a Blueprint
    ///     variable, not a Net property - so it is kept only to record what the real value is; the
    ///     client's own CDO default is what a replicated decal will actually use.
    /// </summary>
    public const float AbilityDecalSize = 96f;
}

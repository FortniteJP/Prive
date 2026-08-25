namespace AFortOnlineBeacon.Core;

/// <summary>
///     /Script/FortniteGame.EFortCustomPartType - the slot index into
///     FCustomCharacterData::Parts[6] (and the bit index into its WasPartReplicatedFlags mask).
///     Values from a Dumper-7 dump of the real 10.40 client.
/// </summary>
public enum EFortCustomPartType {
    Head,
    Body,
    Hat,
    Backpack,
    Charm,
    Face,
    NumTypes
}

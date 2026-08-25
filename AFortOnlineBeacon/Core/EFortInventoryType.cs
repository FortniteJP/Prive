namespace AFortOnlineBeacon.Core;

/// <summary>
///     /Script/FortniteGame.EFortInventoryType, uint8-backed. Values from a Dumper-7 dump of the
///     real 10.40 client. MAX=3 matters on the wire: UByteProperty::NetSerializeItem writes
///     CeilLogTwo(MaxEnumValue) bits, so this serializes as 2 bits.
/// </summary>
public enum EFortInventoryType : byte {
    World,
    Account,
    Outpost,
    MAX
}

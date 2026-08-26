namespace AFortOnlineBeacon.Net.Replication;

/// <summary>
///     One entry of AFortGameStateAthena::GameMemberInfoArray - the client's team/squad roster.
///     See FFastArraySerializerWriter.WriteGameMemberInfoArrayDelta for the wire format and for why
///     this exists at all.
/// </summary>
public sealed class FGameMemberInfo {
    /// <summary>FFastArraySerializerItem::ReplicationID - unique and non-negative, handed out by MarkItemDirty in real UE.</summary>
    public required int ReplicationId { get; init; }

    public required byte SquadId { get; init; }
    public required byte TeamIndex { get; init; }
    public required FUniqueNetIdRepl? MemberUniqueId { get; init; }
}

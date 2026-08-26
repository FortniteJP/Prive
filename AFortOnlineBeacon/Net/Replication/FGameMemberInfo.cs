namespace AFortOnlineBeacon.Net.Replication;

/// <summary>
///     One entry of AFortGameStateAthena::GameMemberInfoArray - the client's team/squad roster.
///     See FFastArraySerializerWriter.WriteGameMemberInfoArrayDelta for the wire format and for why
///     this exists at all.
/// </summary>
public sealed class FGameMemberInfo : IFastArrayItem {
    /// <summary>FFastArraySerializerItem::ReplicationID - INDEX_NONE until FFastArraySerializer.MarkItemDirty hands one out.</summary>
    public int ReplicationId { get; set; } = UnrealConstants.IndexNone;

    /// <summary>FFastArraySerializerItem::ReplicationKey - server-side only; bumped every time this entry changes.</summary>
    public int ReplicationKey { get; set; }

    public required byte SquadId { get; init; }
    public required byte TeamIndex { get; init; }
    public required FUniqueNetIdRepl? MemberUniqueId { get; init; }
}

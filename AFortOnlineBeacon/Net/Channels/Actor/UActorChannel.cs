namespace AFortOnlineBeacon.Net.Channels.Actor;

public class UActorChannel : UChannel {
    public AActor? Actor { get; private set; }
    public FNetworkGUID ActorNetGUID { get; set; } = new FNetworkGUID();

    public UActorChannel() {
        ChType = EChannelType.CHTYPE_Actor;
        ChName = EName.Actor;
        // bClearRecentActorRefs = true;
        // bHoldQueuedExportBunchesAndGUIDs = false;
        // QueuedCloseReason = EChannelCloseReason::Destroyed;
    }

    /// <summary>Binds this channel to the actor it will replicate. Simplified port of UActorChannel::SetChannelActor.</summary>
    public void SetChannelActor(AActor inActor) {
        Actor = inActor;

        var packageMap = (UPackageMapClient) Connection!.PackageMap!;
        ActorNetGUID = packageMap.GuidCache!.GetOrAssignNetGUID(inActor);

        Connection.ActorChannels[inActor] = this;
    }

    protected override void CleanUp(bool bForDestroy, EChannelCloseReason closeReason) {
        base.CleanUp(bForDestroy, closeReason);

        // Leave the map consistent, or ServerReplicateActors would keep believing this connection
        // still has a channel for the actor and never reopen one.
        if (Actor != null) Connection?.ActorChannels.Remove(Actor);
    }

    /// <summary>
    ///     Properties marked dirty for the one-shot initial replication below. Handle numbering
    ///     (NativeRepLayouts) was ground-truthed via live wire probes on 2026-08-24 - see that
    ///     file's doc comment for the full story (AttachmentReplication consumes SIX handles, not
    ///     one, which is what made every downstream handle wrong earlier this session). AGameState
    ///     needs bReplicatedHasBegunPlay, APlayerState needs bHasStartedPlaying, and
    ///     APlayerController needs both bHasServerFinishedLoading and the PlayerState object
    ///     reference itself (so the client can resolve PlayerController->PlayerState) - together,
    ///     per Project-Reboot-3.0's own disabled workaround comment, these are what gate a real
    ///     client's loading-screen dismissal.
    /// </summary>
    /// <summary>
    ///     REP_DISABLE - a comma-separated list of property names to drop from whatever changed-set
    ///     GetInitialReplicatedProperties would otherwise return, so a suspected group can be
    ///     switched off without editing code. Purely a bisecting aid: when adding several properties
    ///     at once changes client behaviour for the worse, this narrows down which group did it in
    ///     one run each instead of by reasoning.
    ///
    ///     An entry is either a bare property name, which disables it on every actor, or
    ///     "ActorTypeName:PropertyName", which disables it only on that actor. The qualified form
    ///     matters because the same name lives at different handles on different classes -
    ///     "PlayerState" is handle 16 on APlayerController (load-bearing: it gates the loading
    ///     screen) but handle 17 on APawn, so only "APawn:PlayerState" can turn the latter off.
    ///     A colon is used rather than a dot because property names themselves contain dots
    ///     (CharacterData.Parts[0]).
    /// </summary>
    private static readonly HashSet<string> DisabledProperties =
        (Environment.GetEnvironmentVariable("REP_DISABLE") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();

    private static HashSet<string> GetInitialReplicatedProperties(AActor actor) {
        var changed = GetInitialReplicatedPropertiesCore(actor);
        if (DisabledProperties.Count == 0) return changed;

        var typeName = actor.GetType().Name;
        var dropped = changed.Where(name =>
            DisabledProperties.Contains(name) || DisabledProperties.Contains($"{typeName}:{name}")).ToArray();

        if (dropped.Length > 0) {
            changed.ExceptWith(dropped);
            Console.WriteLine($"\aGetInitialReplicatedProperties: REP_DISABLE dropped [{string.Join(", ", dropped)}] from {typeName}");
            File.AppendAllText("REP_DISABLE.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {typeName} dropped [{string.Join(", ", dropped)}]{Environment.NewLine}");
        }

        return changed;
    }

    private static HashSet<string> GetInitialReplicatedPropertiesCore(AActor actor) => actor switch {
        // The Athena block (WarmupCountdown*/AircraftStartTime/TotalPlayers/PlayersLeft/
        // CurrentPlaylistId/GamePhase/bGameModeWillSkipAircraft) mirrors what raider3.5 sets in
        // Logic/Game.h::OnReadyToStartMatch on a real injected 10.40 server. Until this landed the
        // client's GameState looked like a match that had never left EAthenaGamePhase::None, with
        // no playlist id and a battle bus it was still expecting to be put on.
        AGameState => new HashSet<string> {
            "RemoteRole", "Role", "bReplicatedHasBegunPlay", "MatchState", "FortTimeOfDayManager",
            "WorldManager", "ReplicatedWorldTimeSeconds",
            "WarmupCountdownStartTime", "WarmupCountdownEndTime", "AircraftStartTime",
            "TotalPlayers", "PlayersLeft", "CurrentPlaylistId", "GamePhase",
            "CurrentPlaylistInfo.BasePlaylist", "bGameModeWillSkipAircraft"
        },
        APlayerState => new HashSet<string> { "RemoteRole", "Role", "UniqueId", "PlayerNamePrivate", "bHasFinishedLoading", "bHasStartedPlaying", "HeroId", "HeroType",
            "CharacterData.WasPartReplicatedFlags", "CharacterData.Parts[0]", "CharacterData.Parts[1]", "CharacterData.Parts[3]",
            "TeamIndex", "SquadId" },
        // AFortInventory's own InventoryType (handle 16). Its other Net property, Inventory
        // (FFortItemList), is a FastArraySerializer / Custom Delta property and cannot go through
        // FRepLayout at all - see NativeRepLayouts.InventoryProps.
        AFortInventory => new HashSet<string> { "RemoteRole", "Role", "Owner", "InventoryType" },
        // AFortPickup: the whole PrimaryPickupItemEntry struct (handles 17-36, one per member) plus
        // the two flags that say what kind of pickup it is and that it is already at rest. Every
        // member has to be listed - RepLayout gave each one its own handle, and the client's
        // OnRep_PrimaryPickupItemEntry sees whatever arrives, so a missing member is a zero.
        // AFortWeapon: what the weapon IS (WeaponData), which inventory row it belongs to
        // (ItemEntryGuid) and how loaded it is. Owner is the pawn - a real server sets it
        // (raider3.5 Inventory.h:294) and the client uses it to decide whose hands to put this in.
        // AFortWeap_BuildingTool::DefaultMetadata is wire handle 36 - but ONLY on that subclass.
        // AFortWeaponPickaxeAthena and AFortWeaponRanged are SIBLINGS of AFortWeap_BuildingTool
        // under plain AFortWeapon, not descendants of it: their real ClassReps stops at handle 33
        // (ReloadAbilitySpecHandle) or wherever their OWN extra properties end, and handle 36
        // simply does not exist for them. This project has one C# AFortWeapon type standing in for
        // that whole hierarchy, so the split has to happen on STATE (does this instance actually
        // have DefaultMetadata set?), not on C# type. Sending handle 36 unconditionally to every
        // weapon (as an earlier version of this code did) broke every weapon, not just building
        // tools: the client's HandleToCmdIndex.IsValidIndex(36) check fails for a class whose own
        // Cmds array ends before 36, ReceiveProperties_r logs "BunchIsError" and returns false, and
        // the connection dies a few ticks later with no server-side exception at all - reproduced
        // twice, disconnecting during the very first few ticks after spawn, well before any
        // building tool was ever equipped (the pickaxe alone was enough to trigger it). Same failure
        // shape as the historical ATOMIC_STRUCTS bug in [[rep_handle_derivation]].
        AFortWeapon w when w.DefaultMetadata != null => new HashSet<string> {
            "RemoteRole", "Role", "Owner", "Instigator", "WeaponData",
            "ItemEntryGuid.A", "ItemEntryGuid.B", "ItemEntryGuid.C", "ItemEntryGuid.D",
            "WeaponLevel", "AmmoCount",
            "PrimaryAbilitySpecHandle", "ReloadAbilitySpecHandle",
            "DefaultMetadata"
        },
        AFortWeapon => new HashSet<string> {
            // Instigator (handle 15) is what the client's own weapon code reads to find the pawn
            // holding this - without it AFortWeaponRanged::OwnerIsMoving errors every frame and the
            // weapon never becomes usable.
            "RemoteRole", "Role", "Owner", "Instigator", "WeaponData",
            "ItemEntryGuid.A", "ItemEntryGuid.B", "ItemEntryGuid.C", "ItemEntryGuid.D",
            "WeaponLevel", "AmmoCount",
            // Which granted abilities this weapon fires and reloads with - handles 31 and 33.
            "PrimaryAbilitySpecHandle", "ReloadAbilitySpecHandle"
        },
        AFortPickup => new HashSet<string> {
            "RemoteRole", "Role",
            "PrimaryPickupItemEntry.Count", "PrimaryPickupItemEntry.ItemDefinition",
            "PrimaryPickupItemEntry.OrderIndex", "PrimaryPickupItemEntry.Durability",
            "PrimaryPickupItemEntry.Level", "PrimaryPickupItemEntry.LoadedAmmo",
            "PrimaryPickupItemEntry.ItemGuid.A", "PrimaryPickupItemEntry.ItemGuid.B",
            "PrimaryPickupItemEntry.ItemGuid.C", "PrimaryPickupItemEntry.ItemGuid.D",
            "PrimaryPickupItemEntry.StateValues", "PrimaryPickupItemEntry.AlterationInstances",
            "PrimaryPickupItemEntry.GenericAttributeValues",
            "PickupLocationData.LootInitialPosition", "PickupLocationData.LootFinalPosition",
            "PickupLocationData.FinalTossRestLocation", "PickupLocationData.TossState",
            "bTossedFromContainer", "bServerStoppedSimulation",
            // Not sent as true at spawn - it is the per-tick diff that carries it, once
            // ServerHandlePickup flips it. That makes this the first property in the project
            // whose whole purpose is to change after the initial burst.
            "bPickedUp"
        },
        // WorldInventory maps to handle 34 as a plain ObjectRef - CONFIRMED live by the truncated
        // name probe, which reported "Property=WorldInventory, Parent=25, Cmd=53, ReadHandle=34"
        // with ReadLen=8, i.e. a packed NetGUID byte, matching PlayerState's own ObjectRef read at
        // handle 16 (and unlike the 1-bit bools that really occupy 49/50/51, where every earlier
        // attempt had wrongly placed this). See NativeRepLayouts.PlayerControllerProps.
        // APawn: everything APawn::PossessedBy sets - without these the client sees an unowned pawn
        // with no controller and no player state.
        APawn => new HashSet<string> {
            "RemoteRole", "Role", "Owner", "PlayerState", "Controller",
            // Null at spawn and set by ServerExecuteInventoryItem. Listed here because
            // ReplicatedProperties doubles as the set the per-tick diff walks - a property absent
            // from it is never compared, so it could never start being sent later either.
            "CurrentWeapon"
        },
        APlayerController => new HashSet<string> {
            "RemoteRole", "Role", "bHasInitiallySpawned", "bHasServerFinishedLoading",
            "PlayerState", "Pawn", "WorldInventory",
            // Without this the client's inventory capacity is zero and it refuses every pickup.
            "OverriddenBackpackSize",
            // Handle 75. False on an untold client, and a dead player may not jump or build.
            "bMarkedAlive",
            // Handle 80. Without this the client has no BroadcastRemoteClientInfo to call
            // ServerSetPlayerBuildableClass on - see AFortBroadcastRemoteClientInfo's doc comment.
            "BroadcastRemoteClientInfo"
        },
        // AFortBroadcastRemoteClientInfo: just enough that the client can resolve who owns this and
        // that it's live. RemoteBuildableClass is never sent from HERE - it starts unset and only
        // becomes non-default once ServerSetPlayerBuildableClass actually sets it (see
        // NativeRpcHandlers), same as AFortPickup.bPickedUp above.
        AFortBroadcastRemoteClientInfo => new HashSet<string> { "RemoteRole", "Role", "Owner", "bActive" },
        _ => new HashSet<string> { "RemoteRole", "Role" }
    };

    /// <summary>
    ///     Default subobjects (CreateDefaultSubobject in the native constructor) to replicate via a
    ///     sub-object content block (see ReplicateSubobject) alongside this actor's own RepLayout
    ///     property push, mirroring real UE's AActor::ReplicateSubobjects override per class.
    ///     Currently unused - AFortPlayerController::WorldInventory was originally modeled this way,
    ///     but a live client rejected it ("Sub-object cannot be actor class"), proving
    ///     /Script/FortniteGame.FortInventory is really an actor class, not a UObject default
    ///     subobject - see AFortInventory/NativeRepLayouts.PlayerControllerProps for where it lives
    ///     now (a plain ObjectRef Cmd, like AController.PlayerState). Left in place - and
    ///     WriteContentBlockHeader/WriteContentBlockPayload/ReplicateSubobject below still real,
    ///     ported directly from UE 4.23's own DataChannel.cpp - for whatever future property turns
    ///     out to actually be a genuine UObject default subobject.
    /// </summary>
    private static IEnumerable<UObject> GetInitialReplicatedSubobjects(AActor actor) {
        yield break;
    }

    /// <summary>
    ///     One-shot version of UActorChannel::ReplicateActor - writes and sends the actor's spawn
    ///     header, followed by a property push via NativeRepLayouts/FRepLayout (this project's
    ///     minimal stand-in for FObjectReplicator::ReplicateProperties/FRepLayout::SendProperties).
    ///
    ///     TEMPORARY diagnostic, replacing the normal layout entirely: if REPLAYOUT_PROBE_HANDLE=N
    ///     is set, sends HandleProbe's truncated name probe for handle N on the actor named by
    ///     REPLAYOUT_PROBE_ACTOR (substring match on the runtime type name, default APlayerController).
    ///     It makes the client's read of that property overflow, so ReceiveProperties_r logs
    ///     "BunchIsError - Property=<real name>, Parent=<idx>, Cmd=<idx>" - the only condition under
    ///     which the client will name a handle at all. See HandleProbe.cs for how to read the result.
    /// </summary>
    public unsafe void ReplicateActor() {
        if (Actor == null || Connection == null) return;

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        var packageMap = (UPackageMapClient) Connection.PackageMap!;

        // DataChannel.cpp:2825. The must-be-mapped list is per-connection and is supposed to be
        // drained by the SendBunch of whichever bunch referenced those objects. Anything still in it
        // here belongs to an earlier bunch that never flushed it, and would be prepended to THIS
        // actor's bunch instead - worse, if it ever leaked onto a control-channel bunch the client
        // would never strip the prefix, since only UActorChannel::ReceivedBunch reads it.
        if (packageMap.GetMustBeMappedGuidsInLastBunch().Count != 0) {
            Console.WriteLine("ReplicateActor: MustBeMappedGuidsInLastBunch is not empty at the start of " +
                              $"replication ({packageMap.GetMustBeMappedGuidsInLastBunch().Count} leftover) - " +
                              "some earlier bunch serialized objects without flushing them.");
        }

        packageMap.SerializeNewActor(bunch, this, Actor);
        Actor.OnSerializeNewActor(bunch);

        var payload = new FNetBitWriter(Connection.PackageMap, 64);
        // REPLAYOUT_PROBE_HANDLE - the truncated name probe (see HandleProbe.WriteTruncatedNameProbe).
        // Any handle is valid, including the already-confirmed 1-22, which are useful as calibration
        // targets; there is deliberately no guard that can throw here, since this runs deep inside
        // packet dispatch where an exception kills the server outright.
        var probeHandleEnv = Environment.GetEnvironmentVariable("REPLAYOUT_PROBE_HANDLE");
        // REPLAYOUT_PROBE_ACTOR picks which actor's channel carries the probe, matched against the
        // runtime type name (case-insensitive substring, e.g. "AFortInventory"). Defaults to
        // APlayerController, which is what every probe so far has targeted. Only one actor should
        // ever match: the probe deliberately corrupts that channel, and the client closes the
        // connection as soon as it fails.
        var probeActor = Environment.GetEnvironmentVariable("REPLAYOUT_PROBE_ACTOR") ?? nameof(APlayerController);
        if (Actor.GetType().Name.Contains(probeActor, StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(probeHandleEnv, out var probeHandle) && probeHandle > 0) {
            Console.WriteLine($"ReplicateActor: REPLAYOUT_PROBE_HANDLE={probeHandle} on {Actor.GetType().Name} - sending TRUNCATED name probe (RemoteRole anchor + bare handle, no value bits, no terminator) instead of the normal layout");
            HandleProbe.WriteTruncatedNameProbe(payload, Actor, probeHandle);
        } else {
            NativeRepLayouts.Get(Actor).WriteChangedProperties(payload, Actor, ReplicatedProperties);
            WriteCustomDeltaProperties(payload);
        }

        bunch.WriteBit(true); // bHasRepLayout
        bunch.WriteBit(true); // bIsActor

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        Console.WriteLine($"ReplicateActor: {Actor.GetFName()} ChIndex={ChIndex} RemoteRole={Actor.RemoteRole} " +
                          $"Role={Actor.Role} numPayloadBits={numPayloadBits} " +
                          $"payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        foreach (var subobject in GetInitialReplicatedSubobjects(Actor)) ReplicateSubobject(subobject, bunch);

        SendBunch(bunch, false);

        // Everything the burst just wrote is now the client's view of this actor, so record it -
        // otherwise the first ReplicateActorUpdate would resend all of it as "changed".
        NativeRepLayouts.Get(Actor).SeedShadowState(Actor, ReplicatedProperties, _shadowState);
    }

    /// <summary>
    ///     The candidate property set for this channel's actor, resolved once. Doubles as the
    ///     initial burst's changed set and as the set the per-tick diff walks - a property the
    ///     server would never send at join is not one it should start sending later either.
    ///
    ///     Cached because GetInitialReplicatedProperties has side effects (the REP_DISABLE report
    ///     writes to the console and to a file), and the diff pass runs every tick.
    /// </summary>
    private HashSet<string>? _replicatedProperties;

    private HashSet<string> ReplicatedProperties =>
        _replicatedProperties ??= GetInitialReplicatedProperties(Actor!);

    /// <summary>
    ///     What this channel has already put on the wire, per property name. Stands in for real UE's
    ///     shadow buffer (FRepState::StaticBuffer) - see FRepLayout.GetComparableValue.
    /// </summary>
    private readonly Dictionary<string, object?> _shadowState = new();

    /// <summary>
    ///     Time, on the driver's clock, at which this channel is next allowed to consider the actor
    ///     for replication. Real UE keeps the equivalent on the actor as
    ///     AActor::NetUpdateTime/NetUpdateFrequency; it lives here because this project has one
    ///     channel per actor per connection and no ServerReplicateActors prioritisation pass.
    /// </summary>
    private float _nextUpdateTime;

    /// <summary>
    ///     The ongoing half of UActorChannel::ReplicateActor - the branch real UE takes when
    ///     OpenPacketId is already set (DataChannel.cpp:227-155): no spawn header, no bNetInitial,
    ///     and an UNRELIABLE bunch. Unreliable matters here more than in real UE: this project's
    ///     reliable queue is 256 entries deep and closes the connection when it overflows, so
    ///     property updates at gameplay rate must never take that path. A dropped update is
    ///     harmless - the property still differs from the shadow next tick, so it is simply sent
    ///     again.
    ///
    ///     Sends nothing at all when nothing changed, which is the normal case.
    /// </summary>
    public unsafe bool ReplicateActorUpdate() {
        if (Actor == null || Connection == null || Closing || Broken) return false;

        // Not yet opened on the wire - the initial burst has not run, and there is nothing to diff
        // against. ReplicateActor is what opens it.
        if (OpenPacketId.First == UnrealConstants.IndexNone) return false;

        // Wait for the open to be ACKED, not merely sent. These updates are unreliable and UDP is
        // unordered, so one could otherwise overtake the reliable open bunch and reach a client that
        // has no channel for it yet - UActorChannel::ProcessBunch drops those silently ("New actor
        // channel received non-open packet"). Silently is the problem: the packet WAS delivered, so
        // no NAK ever arrives to re-dirty the shadow, and the property would stay stale forever.
        if (!OpenAcked) return false;

        var driverTime = Connection.Driver!.GetElapsedTime();
        if (driverTime < _nextUpdateTime) return false;

        var frequency = Actor.NetUpdateFrequency > 0.0f ? Actor.NetUpdateFrequency : 1.0f;
        _nextUpdateTime = driverTime + 1.0f / frequency;

        var layout = NativeRepLayouts.Get(Actor);
        var changed = layout.CompareProperties(Actor, ReplicatedProperties, _shadowState);

        // Custom deltas go in their own bunch, so an actor with no changed RepLayout property can
        // still have a changed fast array. The same is true one level down, for a component's.
        var wroteSomething = ReplicateCustomDeltaUpdate();
        wroteSomething |= ReplicateAbilitySystemComponent();
        wroteSomething |= ReplicateMovementSet();
        wroteSomething |= ReplicatePlayerAttrSet();

        if (changed.Count == 0) return wroteSomething;

        var changedNames = changed.Select(entry => entry.Name).ToHashSet();

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = false;

        using var payload = new FNetBitWriter(Connection.PackageMap, 64);
        layout.WriteChangedProperties(payload, Actor, changedNames);

        bunch.WriteBit(true); // bHasRepLayout
        bunch.WriteBit(true); // bIsActor

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        Console.WriteLine($"ReplicateActorUpdate: {Actor.GetType().Name} ChIndex={ChIndex} " +
                          $"changed=[{string.Join(", ", changedNames)}] numPayloadBits={numPayloadBits} " +
                          $"payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");

        var packetRange = SendBunch(bunch, false);

        // Only now, after the bunch is away - a shadow updated ahead of a send that never happened
        // would make the property look unchanged forever.
        FRepLayout.CommitShadowState(changed, _shadowState);

        // ...but "away" is not "arrived". These bunches are unreliable, so remember what rode in
        // which packet; a NAK has to undo the shadow update or the property is stale forever.
        if (packetRange.First != UnrealConstants.IndexNone) _unackedUpdates[packetRange.First] = changed;

        // Anything at or below the last delivered packet id has been resolved one way or the other -
        // ack and nak are consumed strictly in order, so this watermark only moves forward.
        if (_unackedUpdates.Count > 1) {
            var acked = Connection.OutAckPacketId;
            foreach (var packetId in _unackedUpdates.Keys.Where(id => id <= acked).ToArray()) {
                _unackedUpdates.Remove(packetId);
            }
        }

        return true;
    }

    /// <summary>
    ///     Sends the actor's changed fast arrays as a content block of their own - bHasRepLayout
    ///     false, which real UE also produces whenever RepLayout wrote nothing
    ///     (DataReplication.cpp:1588); the client then skips ReceiveProperties and goes straight to
    ///     the field loop.
    ///
    ///     Deliberately a SEPARATE, RELIABLE bunch rather than riding along in the unreliable
    ///     property update. Real UE puts both in one content block and absorbs loss through
    ///     FObjectReplicator::ReceivedNak rolling the custom delta base state back; this port has no
    ///     such rollback, and a lost fast-array delta is unrecoverable - the base state has already
    ///     advanced, so the change is never reconsidered and the client's inventory is permanently
    ///     wrong. Reliability is affordable here in a way it is not for per-tick properties: fast
    ///     arrays change on events (an item picked up, ammo spent), not every frame.
    /// </summary>
    private unsafe bool ReplicateCustomDeltaUpdate() {
        using var payload = new FNetBitWriter(Connection!.PackageMap, 256);

        if (!WriteCustomDeltaProperties(payload)) return false;

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        bunch.WriteBit(false); // bHasRepLayout - no property stream in this block
        bunch.WriteBit(true);  // bIsActor

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        Console.WriteLine($"ReplicateCustomDeltaUpdate: {Actor!.GetType().Name} ChIndex={ChIndex} " +
                          $"numPayloadBits={numPayloadBits} payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");

        SendBunch(bunch, false);

        return true;
    }

    /// <summary>Property updates sent in an unreliable bunch, keyed by the packet that carried them.</summary>
    private readonly Dictionary<int, List<(string Name, object? Value)>> _unackedUpdates = new();

    /// <summary>
    ///     Real UE re-dirties unreliable properties through FObjectReplicator::ReceivedNak /
    ///     FRepChangedPropertyTracker. This is the same idea with this port's much simpler shadow:
    ///     forget what the lost packet claimed to have delivered, so the next compare sees those
    ///     properties as changed again and resends them.
    ///
    ///     Without this, a single dropped packet leaves a property permanently stale - it matches
    ///     the shadow forever and is never reconsidered. Reliable bunches do not need it; the base
    ///     implementation resends those outright.
    /// </summary>
    public override void ReceivedNak(int nakPacketId) {
        base.ReceivedNak(nakPacketId);

        if (!_unackedUpdates.Remove(nakPacketId, out var lost)) return;

        foreach (var (name, _) in lost) _shadowState.Remove(name);

        Console.WriteLine($"UActorChannel.ReceivedNak: ChIndex={ChIndex} packet {nakPacketId} was lost, " +
                          $"re-dirtying [{string.Join(", ", lost.Select(entry => entry.Name))}]");
    }

    /// <summary>
    ///     Forget what the shadow says about one property, so the next ReplicateActorUpdate sees it
    ///     as changed and sends it again. Same mechanism ReceivedNak uses, exposed for the one case
    ///     that is not a lost packet: a reference that was WRITTEN correctly but could not be
    ///     RESOLVED by the client, because the actor it names had no channel yet.
    ///
    ///     UPackageMap writes such a reference as a bare NetGUID; if the client has never seen that
    ///     GUID it reads null and, in this project's experience, never comes back to it - real UE's
    ///     FObjectReplicator::UpdateUnmappedObjects would retry, but nothing here makes the server
    ///     send it a second time, and a property that matches the shadow is never reconsidered.
    ///     Re-dirtying once, after the referenced actor's channel is open, is the whole fix.
    /// </summary>
    public void MarkPropertyDirty(string propertyName) => _shadowState.Remove(propertyName);

    /// <summary>
    ///     Port of UActorChannel::WriteContentBlockHeader (DataChannel.cpp). Every replicated object
    ///     in a bunch - the actor itself, or one of its subobjects - is preceded by this small header:
    ///     a bHasRepLayout bit, a bIsActor bit (1 lets the reader skip straight to the payload, since
    ///     it already knows which actor this channel is for), and, for anything else, the object's own
    ///     NetGUID reference (the exact same SerializeObject/InternalWriteObject/ExportNetGUID pipeline
    ///     already used for SerializeNewActor's Archetype reference and AController.PlayerState) plus
    ///     a "stably named" bit. Real UE gates the stably-named-vs-class-fallback choice on
    ///     Connection->Driver->IsServer(); that's always true in this codebase (server-only), so it's
    ///     not checked here. NET_CHECKSUM(Bunch) right after the object reference in real UE is a
    ///     complete no-op outside non-shipping/non-test builds (see CoreNet.h's
    ///     `#if !(UE_BUILD_SHIPPING || UE_BUILD_TEST)` gate on the NET_CHECKSUM macro) - this project's
    ///     already-proven-working SerializeNewActor/InternalWriteObject paths likewise never emit
    ///     anything for it, so it's omitted here too for consistency with a real (shipping/test) client.
    /// </summary>
    public void WriteContentBlockHeader(UObject obj, FOutBunch bunch, bool hasRepLayout) {
        bunch.WriteBit(hasRepLayout);

        var isActor = obj == Actor;
        bunch.WriteBit(isActor);
        if (isActor) return;

        var packageMap = (UPackageMapClient) Connection!.PackageMap!;
        packageMap.SerializeObject(bunch, obj);

        if (obj.IsNameStableForNetworking()) {
            bunch.WriteBit(true);
        } else {
            bunch.WriteBit(false);

            // UClass.CreateDefaultObject() is what actually gives a UClass instance its own
            // resolvable (package, name) identity (see its own comment) - it's normally only called
            // lazily via GetDefaultObject()/GetArchetype() when spawning an actor of that class.
            // A class referenced ONLY here (as a subobject's class fallback, never as some actor's
            // archetype) would otherwise still have a blank FName/null Outer at export time -
            // confirmed live 2026-08-24 ("FullNetGUIDPath: [15]EMPTY"). Calling GetDefaultObject()
            // first (result unused) forces that lazy initialization before we export the class itself.
            var classObj = obj.GetClass();
            classObj.GetDefaultObject();
            packageMap.SerializeObject(bunch, classObj);
        }
    }

    /// <summary>
    ///     Port of UActorChannel::WriteContentBlockPayload (DataChannel.cpp): a content block header
    ///     followed by a packed bit count and that many payload bits.
    /// </summary>
    public unsafe void WriteContentBlockPayload(UObject obj, FOutBunch bunch, bool hasRepLayout, FNetBitWriter payload) {
        WriteContentBlockHeader(obj, bunch, hasRepLayout);

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());
    }

    /// <summary>
    ///     Simplified port of UActorChannel::ReplicateSubobject (DataChannel.cpp) - writes a
    ///     sub-object content block for a non-actor UObject owned by this channel's Actor (e.g.
    ///     AFortPlayerController::WorldInventory, a default subobject created via
    ///     CreateDefaultSubobject in the native constructor). Real UE first tries
    ///     FObjectReplicator::ReplicateProperties (the subobject's own FRepLayout push) and only
    ///     falls back to an empty-payload content block if that had nothing to send; this project
    ///     doesn't implement per-subobject FRepLayout property replication, so it always takes that
    ///     fallback (`WriteContentBlockPayload(Obj, Bunch, false, EmptyPayload)`), which is also
    ///     exactly what real UE does the very first time a subobject with no changed properties of
    ///     its own is replicated - the sole purpose is letting the client resolve/create the
    ///     subobject's NetGUID.
    ///
    ///     Real UE's version also has a defensive pre-step
    ///     (`if (!GuidCache->SupportsObject(Obj)) GuidCache->AssignNewNetGUID_Server(Obj)`) before
    ///     calling SerializeObject, needed because FNetGUIDCache::SupportsObject would otherwise
    ///     reject a default subobject whose Outer (the dynamically-spawned actor) isn't itself
    ///     name-stable. That's not needed here: UFortWorldItem already overrides
    ///     IsSupportedForNetworking() to return true (see its own doc comment), so
    ///     FNetGUIDCache.SupportsObject/GetOrAssignNetGUID (called inside SerializeObject, inside
    ///     WriteContentBlockHeader) already assigns a NetGUID correctly on the first reference without
    ///     a separate bootstrap step.
    /// </summary>
    public void ReplicateSubobject(UObject obj, FOutBunch bunch) {
        if (Connection == null) return;

        var emptyPayload = new FNetBitWriter(Connection.PackageMap!, 8);
        Console.WriteLine($"ReplicateSubobject: obj={obj.GetFName()} class={obj.GetClass().GetFName()} owner={Actor?.GetFName()}");
        WriteContentBlockPayload(obj, bunch, false, emptyPayload);
    }

    /// <summary>
    ///     Sends AController::ClientRestart(APawn* NewPawn) on this (already-open) PlayerController
    ///     channel - the server->client RPC real UE fires from Possess()/RestartPlayer() to tell the
    ///     client-side PlayerController it now controls a pawn. A real PR3.0 client capture
    ///     (2026-08-24) showed this is what the client is actually waiting on: without it, the
    ///     client kept calling ServerSetSpectatorLocation forever (still thinks it's a spectator);
    ///     with it, the client immediately replies with ServerAcknowledgePossession and switches to
    ///     ServerMoveNoBase. Only a single object-reference parameter, so this is hand-written rather
    ///     than routed through a generic RPC-writer abstraction (see FRpcReader for the read-side
    ///     equivalent, which this project only needed for client->server calls until now).
    /// </summary>
    /// <summary>
    ///     Stand-in for FObjectReplicator::ReplicateCustomDeltaProperties (DataReplication.cpp:1421),
    ///     called straight after the RepLayout property push so both land in the same content-block
    ///     payload - the same single-Writer arrangement real UE uses in ReplicateProperties.
    ///
    ///     Only AFortInventory::Inventory is sent, and only ever as an empty delta. That property is
    ///     an FFortItemList, which derives from FFastArraySerializer and is therefore a Custom Delta
    ///     property: it is excluded from FRepLayout's handle stream entirely and has to travel as
    ///     its own RepIndex-addressed field. See FFastArraySerializerWriter for the wire format and
    ///     why an empty delta is still a real message rather than a no-op.
    ///
    ///     Why send an empty inventory at all: the client's ClientRestart_Implementation currently
    ///     stops with "Quickbars are invalid, waiting to finish restarting". Quickbars are not
    ///     replicated on this build - AFortQuickBars derives from AFortClientOnlyActor and
    ///     AFortPlayerController::ClientQuickBars carries no Net flag, so the client builds them
    ///     itself - and the inventory is the most plausible thing it is waiting on, since quickbars
    ///     are a view onto it and AFortInventory has both an OnRep on Inventory and its own
    ///     HandleInventoryLocalUpdate. Unconfirmed; if this does not move the client on, the field
    ///     framing here is still the prerequisite for any real item replication later.
    /// </summary>
    /// <summary>
    ///     What this connection was last sent for each of the actor's fast arrays, keyed by
    ///     ClassNetCache field name. See FNetFastTArrayBaseState for why an accurate base key is
    ///     load-bearing rather than cosmetic.
    /// </summary>
    private readonly Dictionary<string, FNetFastTArrayBaseState> _fastArrayBaseStates = new();

    /// <summary>
    ///     Replicates this actor's AbilitySystemComponent as a SUB-OBJECT content block - the first
    ///     time this project sends one, and the only shape a component can travel in (a component is
    ///     not an actor, so it never gets a channel of its own).
    ///
    ///     The framing is the ordinary content block with bIsActor=0, which makes the header carry
    ///     the component's own NetGUID plus - because a component of a runtime-spawned actor is
    ///     never name-stable - its class, so the client can construct it. That matches a real
    ///     Project-Reboot-3.0 capture exactly: the player's ASC there is a dynamic guid with no path
    ///     export, and every one of its 96 blocks rides its owner's channel this way.
    ///
    ///     Sent in its own bunch rather than appended to the actor's, purely so a failure here
    ///     cannot corrupt the actor's own property stream while this path is new.
    /// </summary>
    private unsafe bool ReplicateAbilitySystemComponent() {
        if (Connection == null || Actor is not APlayerState { AbilitySystemComponent: { } asc }) return false;

        using var payload = new FNetBitWriter(Connection.PackageMap, 256);

        // The component's own properties come first, in the same handle stream an actor uses - the
        // ONLY thing that makes this a component rather than an actor is the content block header.
        // OwnerActor/AvatarActor are what let the client run InitAbilityActorInfo and therefore
        // apply movement attributes to the pawn; see NativeRepLayouts.AbilitySystemComponentProps.
        var layout = NativeRepLayouts.AbilitySystemComponent;
        var changed = layout.CompareProperties(asc, AbilitySystemProperties, _ascShadowState);
        var changedNames = changed.Select(entry => entry.Name).ToHashSet();

        if (changedNames.Count > 0) layout.WriteChangedProperties(payload, asc, changedNames);

        var wroteDelta = WriteCustomDeltaField(payload, NativeClassNetCache.FortAbilitySystemComponentCache,
            "ActivatableAbilities", fieldPayload =>
                FFastArraySerializerWriter.WriteDelta(fieldPayload, asc.ActivatableAbilities,
                    BaseStateFor("ActivatableAbilities"), FFastArraySerializerWriter.WriteAbilitySpec));

        if (changedNames.Count == 0 && !wroteDelta) return false;

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        // bHasRepLayout says whether a handle stream leads the payload. WriteContentBlockHeader
        // writes that bit, the bIsActor=0 bit, and the object reference.
        WriteContentBlockHeader(asc, bunch, hasRepLayout: changedNames.Count > 0);

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        var ascGuid = ((UPackageMapClient) Connection.PackageMap!).GuidCache!.GetNetGUID(asc);
        Console.WriteLine($"ReplicateAbilitySystemComponent: ChIndex={ChIndex} Actor={Actor.GetFName()} " +
                          $"ascNetGuid={ascGuid} stablyNamed={asc.IsNameStableForNetworking()} " +
                          $"changed=[{string.Join(", ", changedNames)}] " +
                          $"abilities={asc.ActivatableAbilities.Count} numPayloadBits={numPayloadBits} " +
                          $"payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");

        SendBunch(bunch, false);

        // Only after the bunch is away, for the same reason ReplicateActorUpdate commits late.
        FRepLayout.CommitShadowState(changed, _ascShadowState);

        return true;
    }

    /// <summary>
    ///     Sends the PlayerState's MovementSet attribute values as a sub-object content block - the
    ///     same framing the AbilitySystemComponent uses, one sibling over.
    ///
    ///     Only SpeedMultiplier is sent. Every other attribute already has a healthy value on the
    ///     client (its log prints WalkSpeed 200, RunSpeed 410), so re-sending them would be noise;
    ///     SpeedMultiplier is the one that never appears there at all.
    /// </summary>
    private unsafe bool ReplicateMovementSet() {
        if (Connection == null || Actor is not APlayerState { MovementSet: { } movementSet }) return false;

        var layout = NativeRepLayouts.MovementSet;
        var changed = layout.CompareProperties(movementSet, MovementSetProperties, _movementSetShadowState);
        if (changed.Count == 0) return false;

        var changedNames = changed.Select(entry => entry.Name).ToHashSet();

        using var payload = new FNetBitWriter(Connection.PackageMap, 128);
        layout.WriteChangedProperties(payload, movementSet, changedNames);

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        WriteContentBlockHeader(movementSet, bunch, hasRepLayout: true);

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        Console.WriteLine($"ReplicateMovementSet: ChIndex={ChIndex} RunSpeed={movementSet.RunSpeed} " +
                          $"changed=[{string.Join(", ", changedNames)}] numPayloadBits={numPayloadBits} " +
                          $"payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");

        SendBunch(bunch, false);
        FRepLayout.CommitShadowState(changed, _movementSetShadowState);
        return true;
    }

    /// <summary>
    ///     The movement attributes this server sends. MaxWalkSpeed reads 0 on the client
    ///     (confirmed with `GetAll FortMovementComp_CharacterAthena MaxWalkSpeed`), and Fortnite
    ///     drives it from these - so with nothing feeding them the character has no speed at all.
    ///     Base and Current always travel together.
    /// </summary>
    private static readonly HashSet<string> MovementSetProperties = new() {
        "WalkSpeed.BaseValue", "WalkSpeed.CurrentValue",
        "RunSpeed.BaseValue", "RunSpeed.CurrentValue",
        "SprintSpeed.BaseValue", "SprintSpeed.CurrentValue",
        "CrouchedRunSpeed.BaseValue", "CrouchedRunSpeed.CurrentValue",
        "CrouchedSprintSpeed.BaseValue", "CrouchedSprintSpeed.CurrentValue",
        "BackwardSpeedMultiplier.BaseValue", "BackwardSpeedMultiplier.CurrentValue",
        "SpeedMultiplier.BaseValue", "SpeedMultiplier.CurrentValue"
    };

    private readonly Dictionary<string, object?> _movementSetShadowState = new();

    /// <summary>
    ///     The stamina set, sent exactly like the movement set one sibling over.
    ///
    ///     Stamina is not cosmetic: a Fortnite jump spends it, so a client reading zero refuses
    ///     to jump at all - locally, before any RPC, which is why the server saw a client that
    ///     crouched and fired and harvested but never once set the jump flag in a move.
    /// </summary>
    private unsafe bool ReplicatePlayerAttrSet() {
        if (Connection == null || Actor is not APlayerState { PlayerAttrSet: { } attrSet }) return false;

        var layout = NativeRepLayouts.PlayerAttrSet;
        var changed = layout.CompareProperties(attrSet, PlayerAttrSetProperties, _playerAttrSetShadowState);
        if (changed.Count == 0) return false;

        var changedNames = changed.Select(entry => entry.Name).ToHashSet();

        using var payload = new FNetBitWriter(Connection.PackageMap, 128);
        layout.WriteChangedProperties(payload, attrSet, changedNames);

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = true;

        WriteContentBlockHeader(attrSet, bunch, hasRepLayout: true);

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);

        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        Console.WriteLine($"ReplicatePlayerAttrSet: ChIndex={ChIndex} Stamina={attrSet.Stamina}/{attrSet.MaxStamina} " +
                          $"changed=[{string.Join(", ", changedNames)}] numPayloadBits={numPayloadBits} " +
                          $"payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");

        SendBunch(bunch, false);
        FRepLayout.CommitShadowState(changed, _playerAttrSetShadowState);
        return true;
    }

    /// <summary>Stamina and its regen, plus the cap the client clamps against.</summary>
    private static readonly HashSet<string> PlayerAttrSetProperties = new() {
        "Stamina.BaseValue", "Stamina.CurrentValue",
        "StaminaRegenRate.BaseValue", "StaminaRegenRate.CurrentValue",
        "StaminaRegenDelay.BaseValue", "StaminaRegenDelay.CurrentValue",
        "MaxStamina.BaseValue", "MaxStamina.CurrentValue"
    };

    private readonly Dictionary<string, object?> _playerAttrSetShadowState = new();
    /// <summary>
    ///     The AbilitySystemComponent properties this server sends. Deliberately just the two links
    ///     that make the component usable - everything else on it is either server bookkeeping or a
    ///     subsystem this project does not have.
    /// </summary>
    private static readonly HashSet<string> AbilitySystemProperties = new() {
        "SpawnedAttributes", "OwnerActor", "AvatarActor"
    };

    /// <summary>The component's own shadow buffer, kept apart from the actor's.</summary>
    private readonly Dictionary<string, object?> _ascShadowState = new();

    private FNetFastTArrayBaseState BaseStateFor(string fieldName) {
        if (!_fastArrayBaseStates.TryGetValue(fieldName, out var state)) {
            state = new FNetFastTArrayBaseState();
            _fastArrayBaseStates[fieldName] = state;
        }

        return state;
    }

    /// <summary>
    ///     Writes every custom delta field whose array has moved since this connection last saw it.
    ///     Called both from the initial burst (where every base state is empty, so everything is
    ///     "changed") and from the per-tick pass - the delta computation is identical, which is the
    ///     point: the first send is just a delta against nothing.
    ///
    ///     Returns true when at least one field was written.
    /// </summary>
    private unsafe bool WriteCustomDeltaProperties(FNetBitWriter payload) {
        if (Connection == null) return false;

        var wroteSomething = false;

        switch (Actor) {
            case AFortInventory inventory:
                wroteSomething |= WriteCustomDeltaField(payload, "Inventory", fieldPayload =>
                    FFastArraySerializerWriter.WriteDelta(fieldPayload, inventory.Inventory, BaseStateFor("Inventory"),
                        FFastArraySerializerWriter.WriteItemEntry));
                break;

            // AFortGameStateAthena::GameMemberInfoArray - the team/squad roster the client looks up
            // by unique id. See FFastArraySerializerWriter.WriteGameMemberInfo.
            case AGameState gameState when gameState.GameMemberInfoArray.Count > 0:
                wroteSomething |= WriteCustomDeltaField(payload, "GameMemberInfoArray", fieldPayload =>
                    FFastArraySerializerWriter.WriteDelta(fieldPayload, gameState.GameMemberInfoArray, BaseStateFor("GameMemberInfoArray"),
                        FFastArraySerializerWriter.WriteGameMemberInfo));
                break;

            // NOTE: AFortGameStateAthena::CurrentPlaylistInfo is deliberately NOT sent as a custom
            // delta field. Handle 155 in the ordinary property stream already delivers BasePlaylist
            // (live-confirmed: the client logs OnRep_CurrentPlaylistInfo -> LoadCurrentPlaylistData
            // -> OnPlaylistDataLoadCompleted for Playlist_DefaultSolo), and a bare
            // FFastArraySerializer header on the same property makes the client log
            // "NetDeltaSerialize - Mismatch read" and permanently mark the field bIncompatible for
            // the connection (DataReplication.cpp:1058-1068). Whatever Fortnite's hand-written
            // FPlaylistPropertyArray::NetDeltaSerialize reads, it is not the stock 4-int32 header -
            // three live runs pinned the reader to consuming fewer bits than that. See
            // FFastArraySerializerWriter.WritePlaylistPropertyArrayDelta for the measurements.
        }

        return wroteSomething;
    }

    /// <summary>
    ///     Writes one Custom Delta property as a ClassNetCache-indexed field. Identical framing to
    ///     SendRpc's below, which is the point: real UE routes custom delta properties and RPCs
    ///     through the very same UActorChannel::WriteFieldHeaderAndPayload.
    /// </summary>
    /// <summary>
    ///     Wraps one custom delta payload in its ClassNetCache field header. <paramref name="writeDelta"/>
    ///     returns false when the array has not moved for this connection, in which case NOTHING is
    ///     written - an unchanged fast array must be absent from the bunch, not present as an empty
    ///     header, or the client re-runs its whole PostReceiveCleanup and RepNotify for no reason.
    /// </summary>
    private bool WriteCustomDeltaField(FNetBitWriter payload, string fieldName, Func<FNetBitWriter, bool> writeDelta) =>
        WriteCustomDeltaField(payload, NativeClassNetCache.Get(Actor!), fieldName, writeDelta);

    private unsafe bool WriteCustomDeltaField(FNetBitWriter payload, FClassNetCache classCache, string fieldName, Func<FNetBitWriter, bool> writeDelta) {
        var field = classCache.GetFromName(fieldName);
        if (field == null) {
            Console.WriteLine($"WriteCustomDeltaProperties: '{fieldName}' not found in {Actor!.GetType().Name}'s ClassNetCache, not sending");
            return false;
        }

        using var fieldPayload = new FNetBitWriter(Connection!.PackageMap, 256);
        if (!writeDelta(fieldPayload)) return false;

        var fieldIndex = (uint) field.FieldNetIndex;
        payload.SerializeInt(&fieldIndex, (uint) (classCache.GetMaxIndex() + 1));
        var fieldBits = (uint) fieldPayload.GetNumBits();
        payload.SerializeIntPacked(&fieldBits);
        var fieldData = fieldPayload.GetData();
        fixed (byte* p = fieldData) payload.SerializeBits(p, fieldPayload.GetNumBits());

        Console.WriteLine($"WriteCustomDeltaProperties: {fieldName} fieldIndex={fieldIndex} maxIndex={classCache.GetMaxIndex()} fieldBits={fieldBits}");

        return true;
    }

    public void SendClientRestart(APawn pawn) => SendPawnRpc("ClientRestart", pawn);

    /// <summary>
    ///     APlayerController::ClientRetryClientRestart - the server's half of the possession retry
    ///     handshake, and the answer to a question the client had been asking us over and over with
    ///     no reply.
    ///
    ///     Real UE (PlayerController.cpp:2684-2701, 672-706): when the client's
    ///     ClientRestart_Implementation cannot finish - on this build it bails with
    ///     "ClientRestart_Implementation failed because Quickbars are invalid, waiting to finish
    ///     restarting" - it never acknowledges the pawn, and instead calls
    ///     ServerCheckClientPossessionReliable. The server answers:
    ///
    ///         void APlayerController::ServerCheckClientPossession_Implementation() {
    ///             if (AcknowledgedPawn != GetPawn()) {
    ///                 LastRetryPlayerTime = ForceRetryClientRestartTime;   // bypass the throttle
    ///                 SafeRetryClientRestart();                            // ClientRetryClientRestart(GetPawn())
    ///             }
    ///         }
    ///
    ///     and ClientRetryClientRestart_Implementation does strictly MORE than re-issue the call -
    ///     it relinks pawn and controller client-side first:
    ///
    ///         SetPawn(NewPawn); NewPawn->Controller = this; NewPawn->OnRep_Controller();
    ///         ClientRestart(GetPawn());
    ///
    ///     So this is not merely a retry: OnRep_Controller is client-side state we had no other way
    ///     to trigger. Until now this project sent ClientRestart exactly once and ignored every
    ///     ServerCheckClientPossession(Reliable) that came back, so the loop the engine designed to
    ///     converge here could never run.
    ///
    ///     Same single-APawn-parameter shape as ClientRestart, so it shares SendPawnRpc.
    /// </summary>
    public void SendClientRetryClientRestart(APawn pawn) => SendPawnRpc("ClientRetryClientRestart", pawn);

    /// <summary>
    ///     Server-side half of the possession handshake, driven by name rather than through
    ///     NativeRpcHandlers because every branch needs the channel to reply on. Called for every
    ///     field, whether or not it decoded into a registered RPC - the field's own declared bit
    ///     count resyncs the reader either way, so the parameters do not have to be understood.
    ///
    ///     The important one is ServerSetSpectatorLocation. It looks like pure spectator noise, but
    ///     APlayerController::ServerSetSpectatorLocation_Implementation (PlayerController.cpp:2745)
    ///     is where a real server keeps retrying possession:
    ///
    ///         else if (World->TimeSeconds != LastSpectatorStateSynchTime) {
    ///             if (AcknowledgedPawn != GetPawn()) SafeRetryClientRestart();
    ///             else { ClientGotoState(GetStateName()); ClientSetViewTarget(GetViewTarget()); }
    ///         }
    ///
    ///     That matters because ClientRestart can legitimately fail the first few times and the
    ///     client then goes quiet - it only sends ServerCheckClientPossession while it still thinks
    ///     it has a pawn. Observed here: the client tried twice, both times before its character
    ///     customization had finished loading, and nothing ever retried afterwards even though the
    ///     loader completed milliseconds later. The client keeps sending ServerSetSpectatorLocation
    ///     the whole time, so that is the heartbeat a server is meant to retry on.
    ///
    ///     (The else branch - ClientGotoState/ClientSetViewTarget - is not implemented yet; it is
    ///     what pulls the client out of spectator state once it HAS acknowledged.)
    /// </summary>
    private void HandlePossessionRpc(string fieldName) {
        if (Actor is not APlayerController pc) return;

        switch (fieldName) {
            case "ServerAcknowledgePossession":
                // The APawn* parameter is deliberately not decoded - the only pawn this controller
                // can be acknowledging is its own.
                pc.AcknowledgedPawn = pc.Pawn;
                Console.WriteLine($"HandlePossessionRpc: ServerAcknowledgePossession - client acknowledged {pc.Pawn?.GetFName()}");
                break;

            case "ServerCheckClientPossession":
            case "ServerCheckClientPossessionReliable":
            case "ServerSetSpectatorLocation":
                if (pc.AcknowledgedPawn != pc.Pawn) SafeRetryClientRestart();
                break;
        }
    }

    /// <summary>APlayerController::RetryClientRestartThrottleTime.</summary>
    private const float RetryClientRestartThrottleTime = 0.5f;

    private float _LastRetryPlayerTime = float.NegativeInfinity;

    /// <summary>
    ///     Answers ServerCheckClientPossession / ...Reliable, mirroring
    ///     APlayerController::SafeRetryClientRestart - throttle included.
    ///
    ///     This was first written WITHOUT the throttle, reasoning that real UE deliberately bypasses
    ///     it here (ServerCheckClientPossession_Implementation stamps LastRetryPlayerTime with
    ///     ForceRetryClientRestartTime first, commenting "Client already throttles their call to
    ///     this function, so respond immediately"). That reasoning was wrong in this context and the
    ///     result was a self-amplifying loop: the client's ClientRestart fails, it immediately calls
    ///     ServerCheckClientPossessionReliable from the failure path (not the throttled
    ///     SafeServerCheckClientPossession path), we immediately answer with another RELIABLE
    ///     ClientRetryClientRestart, and round it goes at frame rate. NumOutRec on this channel then
    ///     climbs about 60/second until it hits UNetConnection.ReliableBuffer (256), at which point
    ///     UChannel.SendBunch closes the connection - roughly four seconds in.
    ///
    ///     UE never sees this because on a healthy server the loop converges after one or two
    ///     rounds. The client's own throttle cannot save us, because it is the FAILURE path doing
    ///     the calling. So the brake has to live here; 0.5s is UE's own constant.
    /// </summary>
    public void SafeRetryClientRestart() {
        if (Actor is not APlayerController pc) return;
        if (pc.Pawn == null) {
            Console.WriteLine("SafeRetryClientRestart: PlayerController has no Pawn, not retrying");
            return;
        }

        // UWorld.TimeSeconds only became a real clock when UWorld.Tick started advancing it; if
        // GetWorld() is ever null here the throttle would jam at 0 and this would fire exactly once,
        // so fall back to a source that always moves.
        var now = Actor.GetWorld()?.TimeSeconds ?? (Environment.TickCount64 / 1000f);
        if (now - _LastRetryPlayerTime <= RetryClientRestartThrottleTime) return;
        _LastRetryPlayerTime = now;

        SendClientRetryClientRestart(pc.Pawn);
    }

    /// <summary>UCharacterMovementComponent::NetworkMinTimeBetweenClientAckGoodMoves.</summary>
    private const float NetworkMinTimeBetweenClientAckGoodMoves = 0.10f;

    private bool _LoggedFirstGoodMoveAck;

    /// <summary>
    ///     Port of the bAckGoodMove branch of UCharacterMovementComponent::SendClientAdjustment.
    ///     Real UE drives this once per ServerReplicateActors pass ("we do this here so that we send
    ///     a maximum of one per packet to that client"); this project has no such pass, so it runs
    ///     off the receive path instead - with the same 0.10s throttle, which dominates either way.
    ///
    ///     ACharacter::ClientAckGoodMove is declared UFUNCTION(unreliable, client), hence reliable:
    ///     false. One ack frees every client saved move up to its timestamp, so 10Hz is plenty for a
    ///     client moving at 60Hz.
    /// </summary>
    private void SendClientAckGoodMove() {
        if (Actor is not APawn pawn) return;
        if (pawn.PendingAckGoodMoveTimeStamp <= 0f) return;

        var now = Actor.GetWorld()?.TimeSeconds ?? (Environment.TickCount64 / 1000f);
        if (now - pawn.ServerLastClientGoodMoveAckTime <= NetworkMinTimeBetweenClientAckGoodMoves) return;
        pawn.ServerLastClientGoodMoveAckTime = now;

        var timeStamp = pawn.PendingAckGoodMoveTimeStamp;
        pawn.PendingAckGoodMoveTimeStamp = 0f;

        if (!_LoggedFirstGoodMoveAck) {
            _LoggedFirstGoodMoveAck = true;
            Console.WriteLine($"UActorChannel.SendClientAckGoodMove: ChIndex={ChIndex} acknowledging the client's first move (TimeStamp={timeStamp}). " +
                "Client saved moves should stop piling up now - watch for 'Hit limit of 96 saved moves' to disappear.");
        }

        SendRpc("ClientAckGoodMove", writer => {
            writer.WriteBit(true); // TimeStamp is present (non-bool RPC param protocol - see FRpcReader)
            writer.WriteFloat(timeStamp);
        }, reliable: false);
    }

    private void SendPawnRpc(string fieldName, APawn pawn) => SendObjectRpc(fieldName, pawn);

    /// <summary>
    ///     A server-&gt;client RPC taking exactly one object reference. Covers ClientRestart /
    ///     ClientRetryClientRestart (an APawn*) and ClientSetHUD (a TSubclassOf&lt;AHUD&gt;, which is
    ///     just an object reference to a UClass on the wire, so a path-exported UAssetRegistry entry
    ///     serves as well as a spawned actor does).
    /// </summary>
    public void SendObjectRpc(string fieldName, UObject obj) =>
        SendRpc(fieldName, writer => {
            writer.WriteBit(true); // the parameter is present (non-bool RPC param protocol - see FRpcReader)
            ((UPackageMapClient) writer.PackageMap!).SerializeObject(writer, obj);
        });

    /// <summary>
    ///     A server-&gt;client RPC whose UFunction takes no parameters, so its field payload is simply
    ///     zero bits long. WriteFieldHeaderAndPayload still writes the RepIndex and a packed length
    ///     of 0, which is all the client's ReadFieldHeaderAndPayload needs to dispatch the call.
    /// </summary>
    public void SendParameterlessRpc(string fieldName) => SendRpc(fieldName, static _ => {});

    /// <summary>
    ///     A server-&gt;client RPC taking one int32 - APlayerController::ClientCapBandwidth(int32 Cap),
    ///     which AGameModeBase::PostLogin sends right after GenericPlayerInitialization.
    /// </summary>
    public void SendIntRpc(string fieldName, int value) =>
        SendRpc(fieldName, writer => {
            writer.WriteBit(true); // the parameter is present (non-bool RPC param protocol - see FRpcReader)
            writer.WriteInt32(value);
        });

    /// <summary>
    ///     A server-&gt;client RPC taking one bool. Bools carry NO presence bit - the value bit is the
    ///     whole encoding (FRepLayout::SendPropertiesForRPC, mirrored by FRpcReader on the way in).
    /// </summary>
    public void SendBoolRpc(string fieldName, bool value) =>
        SendRpc(fieldName, writer => writer.WriteBit(value));

    /// <summary>
    ///     A server-&gt;client RPC on a replicated COMPONENT. Identical to SendRpc below except the
    ///     content block names the sub-object instead of the actor, and the field index is bounded by
    ///     the COMPONENT's ClassNetCache rather than the actor's - a component has its own index
    ///     space entirely (the ASC's GetMaxIndex is 53, so 6 bits).
    /// </summary>
    private unsafe void SendSubObjectRpc(UObject subObject, FClassNetCache classCache, string fieldName,
                                         Action<FNetBitWriter> writeParams, bool reliable = true) {
        if (Actor == null || Connection == null) return;

        var field = classCache.GetFromName(fieldName);
        if (field == null) {
            Console.WriteLine($"SendSubObjectRpc: '{fieldName}' not found in the sub-object's ClassNetCache, not sending");
            return;
        }

        using var payload = new FNetBitWriter(Connection.PackageMap!, 64);
        payload.WriteBit(false); // bDoChecksum
        uint terminator = 0;
        payload.SerializeIntPacked(&terminator); // empty RepLayout property section

        using var fieldPayload = new FNetBitWriter(Connection.PackageMap!, 64);
        writeParams(fieldPayload);

        var fieldIndex = (uint) field.FieldNetIndex;
        payload.SerializeInt(&fieldIndex, (uint) (classCache.GetMaxIndex() + 1));
        var fieldBits = (uint) fieldPayload.GetNumBits();
        payload.SerializeIntPacked(&fieldBits);
        var fieldData = fieldPayload.GetData();
        fixed (byte* p = fieldData) payload.SerializeBits(p, fieldPayload.GetNumBits());

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = reliable;

        WriteContentBlockHeader(subObject, bunch, hasRepLayout: true);

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);
        var payloadData = payload.GetData();
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        Console.WriteLine($"SendSubObjectRpc: {fieldName} on {subObject.GetFName()} fieldIndex={fieldIndex} " +
                          $"numPayloadBits={numPayloadBits} payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");

        SendBunch(bunch, false);
    }

    /// <summary>
    ///     UAbilitySystemComponent::ClientActivateAbilitySucceed (field index 9) - the server's
    ///     "yes, that activation stands" for a client that has already predicted it.
    ///
    ///     Without this the client is left holding an unresolved prediction key forever, which is
    ///     why only the FIRST ServerTryActivateAbility ever arrived: it predicts, waits, and does
    ///     not ask again.
    /// </summary>
    public void SendClientActivateAbilitySucceed(UObject abilitySystem, int abilityHandle, FPredictionKey predictionKey) =>
        SendSubObjectRpc(abilitySystem, NativeClassNetCache.FortAbilitySystemComponentCache,
            "ClientActivateAbilitySucceed", writer => {
                writer.WriteBit(true);              // AbilityToActivate is present (non-bool param)
                writer.WriteInt32(abilityHandle);   // FGameplayAbilitySpecHandle's single int32

                writer.WriteBit(true);              // PredictionKey is present
                FPredictionKey.Write(writer, predictionKey);
            });

    /// <param name="reliable">
    ///     Must match the UFUNCTION's own declaration. Getting this wrong is not cosmetic: an
    ///     unreliable-in-UE RPC sent reliably at gameplay frequency (ClientAckGoodMove fires at the
    ///     client's move rate) fills the 256-entry reliable buffer and kills the connection.
    /// </param>
    private unsafe void SendRpc(string fieldName, Action<FNetBitWriter> writeParams, bool reliable = true) {
        if (Actor == null || Connection == null) return;

        var classCache = NativeClassNetCache.Get(Actor);
        var field = classCache.GetFromName(fieldName);
        if (field == null) {
            Console.WriteLine($"SendRpc: '{fieldName}' not found in {Actor.GetFName()}'s ClassNetCache, not sending");
            return;
        }

        using var bunch = new FOutBunch(this, false);
        bunch.bReliable = reliable;

        var payload = new FNetBitWriter(Connection.PackageMap, 64);
        payload.WriteBit(false); // bDoChecksum
        uint terminator = 0;
        payload.SerializeIntPacked(&terminator); // empty RepLayout property section

        var fieldPayload = new FNetBitWriter(Connection.PackageMap!, 64);
        writeParams(fieldPayload);

        var fieldIndex = (uint) field.FieldNetIndex;
        payload.SerializeInt(&fieldIndex, (uint) (classCache.GetMaxIndex() + 1));
        var fieldBits = (uint) fieldPayload.GetNumBits();
        payload.SerializeIntPacked(&fieldBits);
        var fieldData = fieldPayload.GetData();
        fixed (byte* p = fieldData) payload.SerializeBits(p, fieldPayload.GetNumBits());

        bunch.WriteBit(true); // bHasRepLayout
        bunch.WriteBit(true); // bIsActor

        var numPayloadBits = (uint) payload.GetNumBits();
        bunch.SerializeIntPacked(&numPayloadBits);
        var payloadData = payload.GetData();
        // Reliable RPCs here are one-shot handshake steps worth always seeing; unreliable ones
        // (ClientAckGoodMove) repeat at gameplay rate and would bury everything else.
        if (reliable || NetDebugLog.VerboseEnabled) Console.WriteLine($"SendRpc: {fieldName} fieldIndex={fieldIndex} numPayloadBits={numPayloadBits} payloadHex={Convert.ToHexString(payloadData, 0, (int) payload.GetNumBytes())}");
        fixed (byte* p = payloadData) bunch.SerializeBits(p, payload.GetNumBits());

        SendBunch(bunch, false);
    }

    public override void Tick() {
        base.Tick();
        // TODO: ProcessQueuedBunches
    }

    public override bool CanStopTicking() {
        return base.CanStopTicking() /* PendingGuidResolves / QueuedBunches */;
    }

    /// <summary>
    ///     Simplified port of UActorChannel::ReceivedBunch. A bunch can contain several back-to-back
    ///     "content blocks" (one per replicated object). Each one starts with a 2-bit header
    ///     (bHasRepLayout, bIsActor) followed by a packed bit count and that many payload bits.
    ///     A content block's payload can itself contain several fields (property updates / RPC
    ///     calls) back-to-back - see ReadContentBlockFields.
    /// </summary>
    protected override unsafe void ReceivedBunch(FInBunch bunch) {
        while (!bunch.AtEnd() && !bunch.IsError()) {
            var bHasRepLayout = bunch.ReadBit();
            if (bunch.IsError()) break;

            var bIsActor = bunch.ReadBit();
            if (bunch.IsError()) break;

            // UActorChannel::ReadContentBlockHeader (DataChannel.cpp:3293). bIsActor=1 means "this
            // block is for the channel's own actor", and the reader can go straight to the payload.
            // Anything else is a SUB-OBJECT - a replicated component - and carries its own object
            // reference first. Every GAS RPC arrives this way (UAbilitySystemComponent is a
            // component, not an actor), so until this was read rather than bailed on, pulling the
            // trigger threw away the rest of the bunch without saying what was in it.
            UObject? subObject = null;
            var subObjectPath = string.Empty;

            if (!bIsActor) {
                subObject = ((UPackageMapClient) Connection!.PackageMap!)
                    .SerializeObjectRead(bunch, out var subObjectGuid, out subObjectPath);

                if (bunch.IsError()) {
                    Console.WriteLine($"UActorChannel.ReceivedBunch: content block header decode failed on ChIndex={ChIndex} " +
                                      $"Actor={Actor?.GetFName()} - dropping the rest of the bunch");
                    return;
                }

                Console.WriteLine($"UActorChannel.ReceivedBunch: SUB-OBJECT content block ChIndex={ChIndex} " +
                                  $"Actor={Actor?.GetFName()} guid={subObjectGuid} " +
                                  $"path='{(subObjectPath.Length > 0 ? subObjectPath : "(none - referenced by id)")}' " +
                                  $"resolved={(subObject != null ? subObject.GetFName().ToString() : "NULL")}");
            }

            uint numPayloadBits = 0;
            bunch.SerializeIntPacked(&numPayloadBits);
            if (bunch.IsError()) break;

            var payloadStart = bunch.Pos;
            var payloadEnd = payloadStart + Math.Min((long) numPayloadBits, bunch.GetBitsLeft());

            var rawHex = HexDumpBits(bunch, payloadStart, Math.Min(payloadEnd, payloadStart + 512));
            var truncated = payloadEnd - payloadStart > 512 ? "..." : "";
            if (NetDebugLog.VerboseEnabled) Console.WriteLine($"UActorChannel.ReceivedBunch: content block ChIndex={ChIndex} Actor={Actor?.GetFName()} bHasRepLayout={bHasRepLayout} numPayloadBits={numPayloadBits} raw={rawHex}{truncated}");

            // A sub-object this server cannot resolve has no ClassNetCache either, so its field
            // indices cannot be decoded - but NumPayloadBits below still resyncs the bunch, so the
            // NEXT block (and every later one) survives. That is the whole difference from before:
            // one unreadable component block used to cost the entire remainder of the bunch.
            if (!bIsActor) {
                // A component we could not resolve has no ClassNetCache either, so its field indices
                // cannot be decoded - but NumPayloadBits below still resyncs the bunch, so the rest
                // survives either way.
                if (subObject is UFortAbilitySystemComponent) {
                    try {
                        ReadContentBlockFields(bunch, NativeClassNetCache.FortAbilitySystemComponentCache,
                            payloadEnd, subObject);
                    } catch (Exception ex) {
                        Console.WriteLine($"UActorChannel.ReceivedBunch: sub-object field decode threw on ChIndex={ChIndex}: {ex}");
                    }
                } else {
                    Console.WriteLine($"UActorChannel.ReceivedBunch: skipping {numPayloadBits} bits from unresolved " +
                                      $"sub-object '{subObjectPath}' - the rest of the bunch is still read");
                }
            } else if (Actor != null) {
                try {
                    ReadContentBlockFields(bunch, NativeClassNetCache.Get(Actor), payloadEnd);
                } catch (Exception ex) {
                    // A ClassNetCache gap (an incompletely ground-truthed class - see
                    // NativeClassNetCache's own comments for which ones still are) desyncs the
                    // bounded-int field-index read and can throw (e.g. FBitReader::SerializeInt
                    // overflowing). The outer NumPayloadBits resync right below is ground truth
                    // regardless, so this is recoverable - better to lose this one content block
                    // than crash the whole process.
                    Console.WriteLine($"UActorChannel.ReceivedBunch: ReadContentBlockFields threw on ChIndex={ChIndex} Actor={Actor.GetFName()}: {ex}");
                }
            }

            // The outer NumPayloadBits is ground truth regardless of how the inner field decode
            // above went (it relies on a best-effort, unverified ClassNetCache guess - see
            // NativeClassNetCache) - always resync here so a bad guess can never desync the bunch.
            bunch.Pos = payloadEnd;
        }
    }

    /// <summary>
    ///     Walks the individual fields (property updates / RPC calls) packed into one actor content
    ///     block's payload, per UActorChannel::ReadFieldHeaderAndPayload: each field is a bounded-int
    ///     RepIndex (range = ClassNetCache->GetMaxIndex()+1, real UE's FBitReader::ReadInt encoding,
    ///     NOT SerializeIntPacked), a packed bit count, then that many payload bits. A field that
    ///     names a registered RPC (see NativeRpcHandlers) gets its parameters decoded and its
    ///     handler invoked; anything else is just logged and skipped, same as before - the field's
    ///     own declared bit count always resyncs bunch.Pos at the end of the loop body regardless of
    ///     what a handler actually consumed, so a handler bug can't desync the rest of the bunch.
    /// </summary>
    private unsafe void ReadContentBlockFields(FInBunch bunch, FClassNetCache classCache, long payloadEnd,
                                               UObject? subObject = null) {
        var maxIndex = classCache.GetMaxIndex();
        var target = subObject ?? Actor;
        var rpcTable = subObject != null
            ? NativeRpcHandlers.GetForSubObject(subObject)
            : Actor != null ? NativeRpcHandlers.Get(Actor) : null;

        while (bunch.Pos < payloadEnd && !bunch.IsError()) {
            var repIndex = (int) bunch.ReadInt((uint) (maxIndex + 1));
            if (bunch.IsError()) break;

            uint fieldNumBits = 0;
            bunch.SerializeIntPacked(&fieldNumBits);
            if (bunch.IsError()) break;

            var fieldStart = bunch.Pos;
            var fieldEnd = Math.Min(fieldStart + (long) fieldNumBits, payloadEnd);
            var fieldName = classCache.GetFromIndex(repIndex)?.Name ?? "?";

            if (rpcTable != null && rpcTable.TryGetValue(fieldName, out var rpcDef)) {
                try {
                    var values = FRpcReader.ReadParams(bunch, rpcDef.Params);

                    // The one check available on an RPC layout that was derived rather than probed.
                    // A field carries no internal framing, so a decode that is off by a few bits
                    // yields plausible values and no error at all; the only thing that gives it away
                    // is finishing somewhere other than the field's own declared end.
                    if (rpcDef.ExpectsFullDecode && !bunch.IsError()) {
                        var leftover = fieldEnd - bunch.Pos;
                        if (leftover != 0) {
                            Console.WriteLine($"UActorChannel.ReceivedBunch: {fieldName} decoded {bunch.Pos - fieldStart} " +
                                              $"of {fieldNumBits} bits - {leftover} left over. The declared parameter " +
                                              "layout does not match the wire; treat the decoded values as suspect.");
                        }
                    }

                    rpcDef.Invoke(target as AActor ?? Actor!, values);
                } catch (Exception ex) {
                    Console.WriteLine($"UActorChannel.ReceivedBunch: RPC {fieldName} failed to decode on ChIndex={ChIndex} Actor={Actor?.GetFName()}: {ex}");
                }
            } else if (subObject != null) {
                // Not gated on verbose: a component field arrives only when the player actually does
                // something, and each unrecognised one names the next thing worth implementing.
                Console.WriteLine($"UActorChannel.ReceivedBunch:   SUB-OBJECT field[{repIndex}]={fieldName} " +
                                  $"on {subObject.GetFName()} ({fieldNumBits} payload bits) - no handler");
            } else {
                // NOT gated on verbose, for the same reason the sub-object case above isn't: a
                // top-level field the client sent that names no known RPC is exactly as informative
                // as an unrecognised sub-object one - each names the next thing worth implementing,
                // and staying silent here is how a genuine building-placement attempt (e.g.
                // ServerCreateBuildingActor, which ClassNetCache can name but this project has never
                // had a handler for) could arrive and leave no trace at all. Previously gated on
                // NET_VERBOSE, which meant every ordinary test run - including every past attempt to
                // confirm "the client never even tries to build" - could not actually tell an
                // unattempted RPC apart from an attempted-but-silently-dropped one.
                // ServerUpdateCamera specifically: sent every tick regardless of what the player is
                // doing, so it never names a new gate the way the rest of this branch's discoveries
                // did (ServerCreateBuildingActor, ServerSetPlayerBuildableClass, ...) - pure log
                // volume with nothing left to learn from it. Everything else stays unconditional.
                if (fieldName != "ServerUpdateCamera") {
                    Console.WriteLine($"UActorChannel.ReceivedBunch:   field[{repIndex}]={fieldName} on ChIndex={ChIndex} Actor={Actor?.GetFName()} ({fieldNumBits} payload bits) - no handler");
                }
            }

            // Both of these are about the channel's ACTOR; a component's fields never mean either.
            if (subObject == null) {
                HandlePossessionRpc(fieldName);

                // Any accepted client move leaves a timestamp owed back to the client; this is the
                // only place we learn one arrived. Throttled inside - see SendClientAckGoodMove.
                if (fieldName.StartsWith("ServerMove", StringComparison.Ordinal)) SendClientAckGoodMove();
            }

            bunch.Pos = fieldEnd;
        }
    }

    /// <summary>Side-effect-free hex dump of [startBit, endBit) - does not touch bunch.Pos.</summary>
    private static string HexDumpBits(FInBunch bunch, long startBit, long endBit) {
        var numBits = endBit - startBit;
        var bytes = new byte[(numBits + 7) / 8];
        for (long i = 0; i < numBits; i++) {
            if (bunch.BufferBits[(int) (startBit + i)]) bytes[i >> 3] |= (byte) (1 << (int) (i & 7));
        }

        return Convert.ToHexString(bytes);
    }
}
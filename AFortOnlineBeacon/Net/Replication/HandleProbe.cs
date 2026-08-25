namespace AFortOnlineBeacon.Net.Replication;

/// <summary>
///     TEMPORARY diagnostic-only helper. Identifies which real property occupies a given FRepLayout
///     wire handle on a live Fortnite 10.40 client, by making the client's own read of that property
///     overflow so it logs the property's name. Driven by the REPLAYOUT_PROBE_HANDLE environment
///     variable, hooked in UActorChannel.ReplicateActor; delete both once
///     NativeRepLayouts.PlayerControllerProps is correct.
///
///     Currently in use to find AFortPlayerController::WorldInventory's real handle. What is known:
///     handle 50 is a 1-bit Bool (proven by hand-decoding a real failing payload - see
///     WriteTruncatedNameProbe below), so WorldInventory is NOT at 50, and the SDK/NetFields.txt
///     declaration-order estimate has been wrong every time it has been checked against a live
///     client. Note this leaves an unresolved contradiction worth re-testing before trusting either
///     end of the range: FRepLayout::InitFromClass walks properties in declaration order, so handles
///     are monotonic in NetFields.txt order, yet WorldInventory is declared between
///     DelayedQuickBarActions and OverriddenBackpackSize - previously believed to be handles 49 and
///     52 - which leaves only 50 and 51 for it. Since 49 and 52 were identified with the deleted,
///     now-discredited value-feeding probe, re-probe both with this one before building on them.
/// </summary>
internal static class HandleProbe {
    /// <summary>
    ///     TRUNCATED NAME PROBE (2026-08-25), driven by REPLAYOUT_PROBE_HANDLE. Derived by reading
    ///     real UE 4.23's FRepLayout::ReceiveProperties_r (RepLayout.cpp:2746-2892) directly rather
    ///     than guessing. It replaces an earlier "send a 1-bit, then a 0xFF, value at the target
    ///     handle" probe that lived here and has now been deleted outright: that technique could
    ///     never name a handle (see below), its results were read backwards, and its handle&lt;23
    ///     guard threw an unhandled exception that took the whole server down mid-session.
    ///
    ///     Concretely, the old probe's headline finding was inverted. Its own comment recorded that a
    ///     1-bit probe at handles 50 and 51 "came back silent even though something real is there",
    ///     and concluded the probe was too weak for object-reference handles. The opposite was true:
    ///     silence meant the 1-bit read was EXACTLY right. A bit-level hand-decode of a real failing
    ///     payload later proved the client consumes exactly 1 bit at handle 50, and UBoolProperty is
    ///     the only UE type that reads 1 bit - so handle 50 is a Bool, and WorldInventory is not
    ///     there at all. Every later round built on that inverted reading.
    ///
    ///     Why no value-feeding probe can ever name a handle: ReceiveProperties_r
    ///     NEVER errors on a handle it doesn't recognize - it silently skips every Cmd whose
    ///     CurrentHandle != ReadHandle and just keeps going. The only two ways it ever names a
    ///     property are (a) the array-improperly-terminated warning, and (b) this branch, right after
    ///     the property is received:
    ///
    ///         if (Params.Bunch.IsError()) {
    ///             UE_LOG(LogRep, Error, TEXT("ReceiveProperties_r: Failed to receive property,
    ///                 BunchIsError - Property=%s, Parent=%d, Cmd=%d, ReadHandle=%d"),
    ///                 *Parent.CachedPropertyName.ToString(), Cmd.ParentIndex, CmdIndex, ...);
    ///
    ///     So the reliable way to make the client TELL US what lives at a handle is to make its read
    ///     of that property OVERFLOW - not to feed it wrong-but-well-formed bits (0xFF), which just
    ///     desyncs the stream and cascades into some unrelated downstream handle (that's exactly what
    ///     the old 0xFF probe did at 50/51: it reported the unrelated Handle=63/bDisplayNPCNumbers).
    ///
    ///     This probe therefore writes NOTHING after the target handle - not even the terminator - so
    ///     the payload ends the instant the client tries to read the property's value. Any real Cmd
    ///     needing even 1 bit overflows immediately, FBitReader sets its error flag, and the branch
    ///     above fires with the property's real name, its Parent index and its Cmd index. It works
    ///     for every Cmd type, including DynamicArray (whose ArrayNum read overflows the same way and
    ///     falls through to the same IsError check).
    ///
    ///     Reading the result:
    ///       - "BunchIsError - Property=X, Parent=P, Cmd=C" => handle H really is property X on the
    ///         client. This is the answer we want. Parent/Cmd are indices into the client's own
    ///         FRepParentCmd/FRepLayoutCmd arrays; Parents are built in the class's replicated
    ///         property declaration order, so P cross-references straight into NetFields.txt.
    ///       - "Invalid property terminator handle - Handle=H" (our own H echoed back) => the client's
    ///         layout has FEWER top-level Cmds than H, so nothing ever matched. That bounds the real
    ///         handle count from above and means the whole 23-51 block estimate is too large.
    ///       - no error at all => handle H exists and its read consumed zero bits, which shouldn't be
    ///         possible for a real Cmd - treat that as a bug in this probe, not a result.
    ///
    ///     A leading RemoteRole (handle 5) push is included purely as an alignment anchor: it's
    ///     confirmed-good, so if the client misreads even that, the problem is upstream of this probe.
    /// </summary>
    public static unsafe void WriteTruncatedNameProbe(FNetBitWriter payload, AActor actor, uint targetHandle) {
        // Same leading bDoChecksum bit FRepLayout.WriteChangedProperties writes - Fortnite's client
        // build has ENABLE_PROPERTY_CHECKSUMS on, and ReceiveProperties reads this bit before the
        // first handle. Always false; we never send checksums.
        payload.WriteBit(false);

        uint remoteRoleHandle = 5;
        payload.SerializeIntPacked(&remoteRoleHandle);
        var remoteRole = (byte) actor.RemoteRole;
        // UByteProperty::NetSerializeItem writes CeilLogTwo(Enum->GetMaxEnumValue()) bits - ROLE_MAX
        // is 4, so 2 bits, matching FRepLayout.WriteChangedProperties' ByteEnum path.
        payload.SerializeBits(&remoteRole, (int) Math.Ceiling(Math.Log2((int) ENetRole.ROLE_MAX)));

        // The probe handle itself - and then nothing. No value bits, no terminator.
        payload.SerializeIntPacked(&targetHandle);
    }

}

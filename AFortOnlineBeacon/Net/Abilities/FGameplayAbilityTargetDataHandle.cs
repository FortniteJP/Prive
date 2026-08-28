namespace AFortOnlineBeacon.Net.Abilities;

/// <summary>
///     One entry of an <see cref="FGameplayAbilityTargetDataHandle"/> - a polymorphic
///     FGameplayAbilityTargetData, identified on the wire by the UScriptStruct it names.
/// </summary>
public class FGameplayAbilityTargetData {
    /// <summary>
    ///     The exported path of the UScriptStruct this entry really is, e.g.
    ///     "/Script/GameplayAbilities.GameplayAbilityTargetData_SingleTargetHit". This arrives as a
    ///     full path on every single call, not just the first: only the authority can assign a
    ///     NetGUID, so a client naming a struct type has nothing but the default guid to send.
    /// </summary>
    public string ScriptStructPath = string.Empty;

    /// <summary>
    ///     The NetGUID the struct type arrived as. Only interesting when the reference carried no
    ///     path: the client is then naming a type by an id the SERVER exported to it earlier, and
    ///     this is all there is to go on.
    /// </summary>
    public uint ScriptStructGuid;

    /// <summary>The struct's short name - what the dispatch below is keyed on.</summary>
    public string StructName =>
        ScriptStructPath[(ScriptStructPath.LastIndexOf('.') + 1)..];

    /// <summary>Set for the SingleTargetHit variants; null for a shape we do not decode.</summary>
    public FHitResult? HitResult;

    /// <summary>
    ///     FFortGameplayAbilityTargetData_SingleTargetHit::CartridgeID - which bullet of a burst this
    ///     was. Fortnite's own addition to the vanilla struct; absent on the vanilla shape.
    /// </summary>
    public int? CartridgeId;

    /// <summary>FFortGameplayAbilityTargetData_SingleTargetHit::WeaponInfo.</summary>
    public int? WeaponInfo;

    /// <summary>
    ///     True when the struct type was one this reader has no layout for. Everything after it in
    ///     the same handle is then unreadable too - the entries are packed back to back with no
    ///     length prefix - so decoding stops rather than guessing.
    /// </summary>
    public bool bUnknownShape;

    public override string ToString() {
        if (bUnknownShape) {
            return StructName.Length > 0
                ? $"{StructName} (shape unknown)"
                : $"(struct guid {ScriptStructGuid}, no path and no name for it)";
        }

        var tail = CartridgeId is { } cartridge ? $" Cartridge={cartridge} WeaponInfo={WeaponInfo}" : string.Empty;
        return HitResult != null ? $"{StructName}: {HitResult}{tail}" : StructName;
    }
}

/// <summary>
///     GameplayAbilities.FGameplayAbilityTargetDataHandle - "what the client says it hit".
///
///     Wire format is FGameplayAbilityTargetDataHandle::NetSerialize
///     (GameplayAbilityTargetTypes.cpp:137): a uint8 UniqueId, a uint8 count, then that many
///     entries, each one a UScriptStruct reference followed by whatever THAT struct's own native
///     NetSerialize writes. It is a tagged union with the tag sent as a full object path, which is
///     why a single reported shot is on the order of 350 bytes.
///
///     Polymorphism is the whole difficulty. There is no length prefix per entry, so an entry whose
///     struct type this reader does not know ends the decode: the alternative is to keep reading at
///     the wrong offset and hand a handler a plausible-looking hit that never happened. Every field
///     is resynchronised by the caller anyway (UActorChannel.ReadContentBlockFields uses the field's
///     own declared bit count), so stopping early is free.
///
///     10.40 uses TWO shapes here. Vanilla FGameplayAbilityTargetData_SingleTargetHit is just an
///     FHitResult; Fortnite's own FFortGameplayAbilityTargetData_SingleTargetHit derives from it and
///     adds CartridgeID and WeaponInfo. Fortnite's NetSerialize override is native code inside an
///     encrypted .text section (see the project's notes on why static disassembly is out), so the
///     encoding of that tail is not known - but it comes AFTER the inherited FHitResult, so the hit
///     itself reads correctly either way and the leftover bit count is reported for whoever wants to
///     work the tail out from a live capture.
/// </summary>
public class FGameplayAbilityTargetDataHandle {
    public byte UniqueId;

    public List<FGameplayAbilityTargetData> Data { get; } = new();

    /// <summary>The first hit in the handle, which for a single bullet or pickaxe swing is the only one.</summary>
    public FHitResult? FirstHit => Data.Select(entry => entry.HitResult).FirstOrDefault(hit => hit != null);

    /// <summary>True if any entry had a struct type this reader could not decode past.</summary>
    public bool bStoppedEarly => bDecodeFailed || Data.Any(entry => entry.bUnknownShape);

    /// <summary>Set when the read ran off the end of the field rather than finishing cleanly.</summary>
    public bool bDecodeFailed;

    /// <summary>
    ///     <paramref name="resolveByGuid"/> turns a NetGUID into a path when the reference did
    ///     not carry one. A live server does not need it - it has a package map - but the offline
    ///     capture decoder has only its own record of the export bunches it saw, and without this
    ///     roughly one entry in eight is a type it cannot name.
    /// </summary>
    public static FGameplayAbilityTargetDataHandle NetSerializeRead(FArchive ar,
                                                                    Func<uint, string?>? resolveByGuid = null) {
        var handle = new FGameplayAbilityTargetDataHandle();

        try {
            ReadInto(handle, ar, resolveByGuid);
        } catch (Exception ex) {
            // An overrun here is a decode bug, not a protocol error, and the caller's own resync
            // makes it survivable - so say what happened and hand back what was understood rather
            // than letting it reach a handler that has real work to do.
            handle.bDecodeFailed = true;
            Console.WriteLine($"FGameplayAbilityTargetDataHandle: decode failed after {handle.Data.Count} " +
                              $"entr{(handle.Data.Count == 1 ? "y" : "ies")}: {ex.Message}");
        }

        return handle;
    }

    private static void ReadInto(FGameplayAbilityTargetDataHandle handle, FArchive ar,
                                 Func<uint, string?>? resolveByGuid) {
        handle.UniqueId = ar.ReadByte();

        var dataNum = ar.ReadByte();
        if (ar.IsError()) return;

        for (var i = 0; i < dataNum && !ar.IsError(); i++) {
            // TCheckedObjPtr<UScriptStruct> - Archive.h:199 unwraps straight to `Ar << UObject*`,
            // so on the wire this is an ordinary object reference with no wrapper of its own.
            var resolved = UPackageMapClient.ReadObjectRef(ar, out var structGuid, out var structPath);
            if (ar.IsError()) return;

            // Usually a full path, because a client cannot assign a NetGUID and so has nothing else
            // to send. But if the SERVER ever exported this struct type's id, the client uses the id
            // and sends no path at all - that is the shape a Project-Reboot-3.0 capture shows for
            // about one entry in eight, and it decoded as a nameless "unknown shape" until the
            // fallbacks below were added.
            if (structPath.Length == 0) {
                structPath = resolved?.GetFName().ToString()
                             ?? resolveByGuid?.Invoke(structGuid.Value)
                             ?? string.Empty;
            }

            var entry = new FGameplayAbilityTargetData {
                ScriptStructPath = structPath,
                ScriptStructGuid = structGuid.Value
            };

            handle.Data.Add(entry);

            switch (entry.StructName) {
                // Both variants START with the inherited FHitResult (a C++ base's members precede
                // the derived struct's, and Fortnite's override calls up before writing its own).
                case "GameplayAbilityTargetData_SingleTargetHit":
                    entry.HitResult = FHitResult.NetSerializeRead(ar, resolveByGuid);
                    break;

                // Fortnite's own subclass. Its NetSerialize is native code, and the shipped .text is
                // encrypted, so the tail could not be READ out of the binary - it was MEASURED, by
                // decoding real Project-Reboot-3.0 batches and finding exactly 64 bits left over
                // where the inherited FHitResult ended.
                //
                // What that proves: 64 bits, raw - nothing quantized or packed, or the total would
                // not be a round two words. The FRAMING is therefore settled, and nothing downstream
                // can desync on it.
                //
                // What it does NOT prove is which value is which. Two int32 members in declaration
                // order is the overwhelming default and the SDK types agree, and the first field
                // behaves like a CartridgeID should (11 captured shots, all under 2^15, no pattern),
                // but the second is only known to increase monotonically - which reads as equally
                // coherent whether it is an int32 counter or a float, so the capture cannot separate
                // them. Settling that means disassembling this function out of a live process dump
                // (the .text is decrypted in memory - see the project's RE notes). Worth doing when
                // something actually depends on these two values; nothing does yet.
                case "FortGameplayAbilityTargetData_SingleTargetHit":
                    entry.HitResult = FHitResult.NetSerializeRead(ar, resolveByGuid);
                    entry.CartridgeId = ar.ReadInt32();
                    entry.WeaponInfo = ar.ReadInt32();
                    break;

                default:
                    entry.bUnknownShape = true;
                    return;
            }
        }
    }

    public override string ToString() =>
        Data.Count == 0 ? "(no targets)" : string.Join("; ", Data);
}

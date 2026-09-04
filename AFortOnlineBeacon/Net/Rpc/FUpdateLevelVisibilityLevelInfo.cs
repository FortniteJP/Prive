namespace AFortOnlineBeacon.Net.Rpc;

/// <summary>
///     Engine.UpdateLevelVisibilityLevelInfo - the parameter of
///     APlayerController::ServerUpdateLevelVisibility, and the element type of
///     ServerUpdateMultipleLevelsVisibility's array. The client uses these to tell the server which
///     streaming levels it has finished loading and which it has thrown away.
///
///     THE 10.40 SDK AND THE 4.23 SOURCE DISAGREE HERE, AND THE SDK WINS. Stock UE 4.23 declares
///     `ServerUpdateLevelVisibility(FName PackageName, bool bIsVisible)` and gives this struct two
///     members; 10.40's SDK shows BOTH RPCs taking the struct, and the struct carrying a third
///     member, `Filename`, that stock 4.23 does not have. Fortnite is ahead of the engine version it
///     nominally is, which is worth remembering for any other RPC signature taken from that source.
///
///     WIRE FORMAT - DERIVED FROM TWO REAL PAYLOADS, not from the header. The first attempt assumed
///     the struct had no native serializer and was written member by member (PackageName, Filename,
///     bIsVisible). That was wrong twice over, and two raw dumps
///     (UActorChannel.DumpRawRpcPayload) settled it with every bit accounted for:
///
///         bit 0   bFileNameIsPackageName - 1 means FileName is NOT on the wire at all
///         bit 1   bIsVisible
///         then    PackageName - FName: one bHardcoded bit, an FString, an int32 Number
///         then    FileName, in the SAME shape, ONLY when bit 0 is 0
///
///     The bools come BEFORE the names, which no member-by-member fallback produces, so this struct
///     has a hand-written NetSerialize - as later stock UE versions also give it.
///
///     THE TWO SAMPLES DISAGREE IN EXACTLY ONE BIT, WHICH IS WHAT PINS IT:
///
///       * ServerUpdateLevelVisibility, 933 bits: flags 0,1 and TWO names -
///         "/Temp/Game/Athena/Maps/POI/Athena_POI_Lobby_004_3f5ab45c" (an instanced package) and
///         "/Game/Athena/Maps/POI/Athena_POI_Lobby_004" (the file it came from). They differ, so the
///         FileName has to be sent, and bit 0 is 0.
///       * ServerUpdateMultipleLevelsVisibility, 7826 bits: nineteen elements, every one flags 1,1
///         and a SINGLE name - "/Game/Athena/Maps/Landscape/Athena_Terrain_LS_00" and friends. These
///         are ordinary sublevels whose package and file names are identical, so the FileName is
///         omitted and bit 0 is 1.
///
///     The ORDER of the two bits is forced by that pair: reading them the other way round would make
///     the first sample say "FileName omitted" while a FileName is plainly there. Sizes close exactly
///     under this reading - the gap between elements in the array is always 35 bits, which is one
///     int32 Number plus the next element's three leading bits, and both payloads end precisely on a
///     Number.
///
///     An FName is UPackageMap::StaticSerializeName (CoreNet.cpp): one bHardcoded bit, then either a
///     packed EName index or an FString plus an int32 Number. Confirmed by the sample - the gap
///     between the two strings is exactly 33 bits, which is one Number plus the next name's
///     bHardcoded bit, and the payload ends exactly on the second Number.
///
public sealed record FUpdateLevelVisibilityLevelInfo(string PackageName, string Filename, bool bIsVisible) {

    /// <summary>
    ///     UPackageMap::StaticSerializeName. Returns the name as a plain string; the FName Number
    ///     suffix is read to stay in sync but folded in only when non-zero, the way FName prints it.
    ///
    ///     BOUNDS-CHECKED, AND THAT IS NOT DEFENSIVE PROGRAMMING FOR ITS OWN SAKE. This decode is a
    ///     HYPOTHESIS about the wire format (see the class comment), and a wrong hypothesis about a
    ///     length-prefixed string does not read a wrong string - it reads a length of a million and
    ///     walks off the end of the bunch. Returning null instead of throwing keeps that a diagnosis
    ///     rather than an exception per RPC, and this one arrives thousands of times a session.
    /// </summary>
    public static string? ReadName(FArchive ar) {
        if (ar.AtEnd()) return null;

        if (ar.ReadBit()) {
            // A hardcoded EName index. UnrealNames.h's MAX_NETWORKED_HARDCODED_NAME is 410, so this
            // is a small number and there is no table here to look it up in - the index itself is
            // the honest answer.
            return ar.AtEnd() ? null : $"EName({ar.ReadUInt32Packed()})";
        }

        // A length that cannot fit in what is left is proof the framing is wrong, and reading it
        // would be the overrun. 8 bits per ANSI character is the cheapest possible encoding, so
        // anything longer than the remaining bits allow is impossible.
        var remaining = ar is FBitReader reader ? reader.GetBitsLeft() : int.MaxValue;
        if (remaining < 32) return null;

        var text = ar.ReadString();
        if (ar.AtEnd() || text.Length * 8 > remaining) return null;

        var number = ar.ReadInt32();

        return number == 0 ? text : $"{text}_{number - 1}";
    }

    /// <summary>Null when the decode ran out of room, which means the format hypothesis is wrong.</summary>
    public static FUpdateLevelVisibilityLevelInfo? NetSerializeRead(FArchive ar) {
        if (ar.AtEnd()) return null;

        // 1 = "the FileName is the PackageName", and nothing follows for it.
        var fileNameIsPackageName = ar.ReadBit();
        if (ar.AtEnd()) return null;
        var isVisible = ar.ReadBit();

        if (ReadName(ar) is not { } packageName) return null;

        if (fileNameIsPackageName) return new FUpdateLevelVisibilityLevelInfo(packageName, packageName, isVisible);

        return ReadName(ar) is { } fileName
            ? new FUpdateLevelVisibilityLevelInfo(packageName, fileName, isVisible)
            : null;
    }

    /// <summary>
    ///     The TArray form, for ServerUpdateMultipleLevelsVisibility - the client batching several
    ///     level reports into one call rather than sending one RPC each.
    ///
    ///     THE LENGTH IS A uint16, and that is from the engine rather than guessed. RPC parameters do
    ///     NOT go through UArrayProperty::NetSerializeItem - that path is dead code in 4.23 and
    ///     fatals if reached - they go through FRepLayout::ReceivePropertiesForRPC, which spends one
    ///     presence bit per non-bool parameter (`Cast&lt;UBoolProperty&gt;(...) || Reader.ReadBit()`,
    ///     RepLayout.cpp:5723 - which is also the confirmation that FRpcReader's model is right) and
    ///     then walks the RepLayout Cmds. A DynamicArray cmd lands in
    ///     SerializeProperties_DynamicArray_r, whose first act is `uint16 OutArrayNum; Ar &lt;&lt; OutArrayNum;`.
    ///
    ///     There is NO presence bit per element - that is a per-PARAMETER thing, and the whole array
    ///     is one parameter.
    ///
    ///     Bounded at MaxRepArraySize, the same 2048 the engine refuses to exceed. A count read under
    ///     a wrong framing is a huge number, and the bound turns that into a null instead of an
    ///     attempt to allocate it.
    /// </summary>
    public static List<FUpdateLevelVisibilityLevelInfo>? NetSerializeReadArray(FArchive ar) {
        if (ar.AtEnd()) return null;

        var count = ar.ReadUInt16();
        if (count > 2048) return null;

        var levels = new List<FUpdateLevelVisibilityLevelInfo>(count);

        for (var i = 0; i < count; i++) {
            if (NetSerializeRead(ar) is not { } level) return null;
            levels.Add(level);
        }

        return levels;
    }

    /// <summary>
    ///     Does this look like something the client could actually have sent? A package name is a
    ///     path, so it is printable and starts with a slash. Used to tell a correct decode from a
    ///     framing error instead of trusting one.
    /// </summary>
    public bool LooksSane =>
        PackageName.Length is > 1 and < 512
        && PackageName.StartsWith('/')
        && PackageName.All(c => c is >= ' ' and < (char) 127);

    public override string ToString() => $"{PackageName} (visible={bIsVisible})";
}

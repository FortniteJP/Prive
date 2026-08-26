namespace AFortOnlineBeacon.Net;


public class FUniqueNetIdRepl {
    private const int TypeHashOther = 31;

    /// <summary>Fortnite's only online subsystem - the "MCP" in the client's own "MCP:5d56...".</summary>
    private const string DefaultSubsystemName = "MCP";
    
    public FUniqueNetId? UniqueNetId { get; private set; }

    public bool IsValid() => UniqueNetId != null && UniqueNetId.IsValid();

    public string ToDebugString() => IsValid() ? $"{UniqueNetId!.Type}:{UniqueNetId!.Contents}" : "INVALID";
    
    public static void Write(FArchive ar, FUniqueNetIdRepl value) => Serialize(ar, value);

    public static FUniqueNetIdRepl Read(FArchive ar) {
        var result = new FUniqueNetIdRepl();
        Serialize(ar, result);
        return result;
    }

    /// <summary>Wraps an already-known id, e.g. the one a client sent us in NMT_Login.</summary>
    public static FUniqueNetIdRepl FromId(FUniqueNetId id) => new() { UniqueNetId = id };

    /// <summary>
    ///     Port of FUniqueNetIdRepl::MakeReplicationData + the IsSaving half of NetSerialize
    ///     (OnlineReplStructs.cpp). The result is a raw byte blob written straight into the archive
    ///     with NO length prefix - the reader consumes it field by field, which is why the layout
    ///     below has to match exactly.
    ///
    ///     The type is always written as a STRING via TypeHash_Other (31) rather than as a real
    ///     replication hash. Real UE resolves a small hash through
    ///     UOnlineEngineInterface::GetReplicationHashForSubsystem, a registry this project has no
    ///     equivalent of; TypeHash_Other exists precisely for "I know the name but not the hash"
    ///     and the client's reader turns the string straight back into an FName.
    /// </summary>
    private void NetSerializeSave(FArchive ar) {
        if (!IsValid()) {
            // Empty/invalid: one byte and nothing else.
            ar.WriteByte((byte) (EUniqueIdEncodingFlags.IsEncoded | EUniqueIdEncodingFlags.IsEmpty));
            return;
        }

        var contents = UniqueNetId!.Contents;
        var length = contents.Length;
        var encodedSize32 = (length + 1) / 2;
        var isNumeric = length > 0 && contents.All(char.IsAsciiDigit);

        var encoded = isNumeric || (length % 2 == 0 && encodedSize32 < byte.MaxValue);

        // HexToBytes loses case, so anything that is not all-lowercase hex has to go unencoded.
        if (encoded && !isNumeric) {
            foreach (var c in contents) {
                if (!char.IsAsciiHexDigit(c) || char.IsUpper(c)) {
                    encoded = false;
                    break;
                }
            }
        }

        var flags = (byte) ((TypeHashOther << 3) | (encoded ? (byte) EUniqueIdEncodingFlags.IsEncoded : (byte) 0));
        ar.WriteByte(flags);
        ar.WriteString(UniqueNetId.Type.ToString()); // TypeHash_Other always carries the name

        if (encoded) {
            ar.WriteByte((byte) encodedSize32);
            ar.Serialize(Convert.FromHexString(contents), encodedSize32);
        } else {
            ar.WriteString(contents);
        }
    }

    private void NetSerialize(FArchive ar, UPackageMap? packageMap, out bool bOutSuccess) {
        // TODO: Get back to this later when we understand FName / FNamePool and UOnlineEngineInterface better.
        // FUniqueNetIdRepl::NetSerialize
        
        bOutSuccess = false;
        
        if (ar.IsSaving()) {
            NetSerializeSave(ar);
            bOutSuccess = IsValid();
        } else if (ar.IsLoading()) {
            UniqueNetId = null;
            
            var encodingFlags = (EUniqueIdEncodingFlags) ar.ReadByte();

            if (!ar.IsError()) {
                if ((encodingFlags & EUniqueIdEncodingFlags.IsEncoded) != 0) {
                    if ((encodingFlags & EUniqueIdEncodingFlags.IsEmpty) == 0) {
                        // Non empty and hex encoded
                        var typeHash = GetTypeHashFromEncoding(encodingFlags);

                        var bValidTypeHash = true;
                        FName type;

                        if (typeHash == TypeHashOther) {
                            var typeString = ar.ReadString();
                            type = new FName(typeString);
                            if (ar.IsError() || type == EName.None) bValidTypeHash = false;
                        } else {
                            // This project has no online-subsystem registry to resolve a real
                            // replication hash against. Fortnite ships exactly one subsystem and the
                            // client prints its own id as "MCP:<32 hex>", so naming it here is
                            // strictly better than the FName(None) this used to produce - None made
                            // IsValid() false and threw away every id a real client ever sent.
                            type = new FName(DefaultSubsystemName);
                        }

                        if (bValidTypeHash) {
                            var encodedSize = ar.ReadByte();

                            if (!ar.IsError()) {
                                if (encodedSize > 0) {
                                    var encodedBytes = new byte[encodedSize];
                                    ar.Serialize(encodedBytes, encodedSize);

                                    if (!ar.IsError()) {
                                        var contents = Convert.ToHexStringLower(encodedBytes);

                                        if (contents.Length > 0 && type != EName.None) UniqueNetId = new FUniqueNetId(type, contents);
                                    }
                                }

                                bOutSuccess = encodedSize == 0 || IsValid();
                            }
                        }
                    } else bOutSuccess = true;
                } else {
                    // Original FString serialization goes here
                    var typeHash = GetTypeHashFromEncoding(encodingFlags);

                    // Same reasoning as the encoded branch above: no subsystem registry here, and
                    // Fortnite only has one. A zero hash means "the default subsystem", which is
                    // also MCP - this used to throw NotImplementedException and take the connection
                    // down.
                    var type = typeHash == TypeHashOther
                        ? new FName(ar.ReadString())
                        : new FName(DefaultSubsystemName);

                    const bool bValidTypeHash = true;

                    if (bValidTypeHash) {
                        var contents = ar.ReadString();
                        if (!ar.IsError()) {
                            // TODO: Check if type != none
                            UniqueNetId = new FUniqueNetId(type, contents);
                            bOutSuccess = true;
                        }
                    } else {
                        // Logger.Warning("Error with encoded type hash");
                    }
                }
            } else {
                // Logger.Warning("Error serializing unique id");
            }
        }
    }
    
    private static void Serialize(FArchive ar, FUniqueNetIdRepl uniqueNetId)
    {
        if (!ar.IsPersistent() || ar._ArIsNetArchive) uniqueNetId.NetSerialize(ar, null, out _);
        else {
            throw new NotSupportedException();
        }
    }

    private static byte GetTypeHashFromEncoding(EUniqueIdEncodingFlags inFlags) {
        var typeHash = (byte) ((byte) (inFlags & EUniqueIdEncodingFlags.TypeMask) >> 3);
        return (byte)(typeHash < 32 ? typeHash : 0);
    }
}
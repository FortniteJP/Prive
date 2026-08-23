namespace AFortOnlineBeacon.Net;

public class FUniqueNetIdRepl {
    private const int TypeHashOther = 31;
    
    public FUniqueNetId? UniqueNetId { get; private set; }

    public bool IsValid() => UniqueNetId != null && UniqueNetId.IsValid();

    public string ToDebugString() => IsValid() ? $"${UniqueNetId!.Type}:{UniqueNetId!.Contents}" : "INVALID";
    
    public static void Write(FArchive ar, FUniqueNetIdRepl value) => Serialize(ar, value);

    public static FUniqueNetIdRepl Read(FArchive ar) {
        var result = new FUniqueNetIdRepl();
        Serialize(ar, result);
        return result;
    }

    private void NetSerialize(FArchive ar, UPackageMap? packageMap, out bool bOutSuccess) {
        // TODO: Get back to this later when we understand FName / FNamePool and UOnlineEngineInterface better.
        // FUniqueNetIdRepl::NetSerialize
        
        bOutSuccess = false;
        
        if (ar.IsSaving()) {
            throw new NotImplementedException();
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
                            // No online subsystem registry to resolve TypeHash -> subsystem name against, so this
                            // just records "some hardcoded subsystem type" rather than the real one.
                            type = new FName(EName.None);
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
                    if (typeHash == 0) {
                        // If no type was encoded, assume default
                        throw new NotImplementedException();
                        // TypeHash = UOnlineEngineInterface::Get()->GetReplicationHashForSubsystem(UOnlineEngineInterface::Get()->GetDefaultOnlineSubsystemName());
                    }

                    FName type;
                    
                    var bValidTypeHash = typeHash != 0;
                    if (typeHash == TypeHashOther) type = new FName(ar.ReadString());
                    else {
                        type = new FName(EName.None);
                        // TODO: Type = UOnlineEngineInterface::Get()->GetSubsystemFromReplicationHash(TypeHash);
                    }

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
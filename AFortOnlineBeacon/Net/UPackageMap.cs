namespace AFortOnlineBeacon.Net;

public class UPackageMap {
    public static unsafe bool StaticSerializeName(FArchive ar, ref FName? name) {
        if (ar.IsLoading()) {
            name = null;
        
            bool bHardcoded;
            ar.SerializeBits(&bHardcoded, 1);
            
            if (bHardcoded) {
                uint nameIndex;

                if (ar.EngineNetVer() < EEngineNetworkVersionHistory.HISTORY_CHANNEL_NAMES) ar.SerializeInt(&nameIndex, UnrealNames.MaxNetworkedHardcodedName);
                else ar.SerializeIntPacked(&nameIndex);

                if (nameIndex < UnrealNames.MaxHardcodedNameIndex) name = new FName((EName) nameIndex);
                else ar.SetError();
            } else {
                var inString = FString.Deserialize(ar);
                var inNumber = ar.ReadInt32();
                name = new FName(inString, inNumber);
            }
        } else {
            if (name == null) throw new UnrealException("Name should not be null when saving");
            
            var inEName = name.Value.ToEName();
            var bHardcoded = inEName.HasValue && ShouldReplicateAsInteger(inEName.Value) ? 1 : 0;
            ar.SerializeBits(&bHardcoded, 1); // 25
            if (bHardcoded != 0) ar.SerializeIntPacked((uint)inEName!.Value);
            else {
                var outString = name.Value.GetPlainNameString();
                var outNumber = name.Value.GetNumber();
                
                ar.WriteString(outString);
                ar.WriteInt32(outNumber);
            }
        }

        return true;
    }

    private static bool ShouldReplicateAsInteger(EName name) => (int)name <= UnrealNames.MaxNetworkedHardcodedName;

    public void NotifyBunchCommit(int bunchPacketId, FOutBunch bunch) => throw new NotImplementedException();

    public void ReceivedAck(int ackPacketId) {
        // TODO: Implement
    }

    public void ReceivedNak(int nakPacketId) {
        // TODO: Implement
    }
}
namespace AFortOnlineBeacon.Core.Properties;

public class UStruct : UField {
    private UStruct? _SuperStruct;
    
    public bool IsChildOf(UStruct? someBase) {
        if (someBase == null) return false;

        var bOldResult = false;
        
        for (var tempStruct = this; tempStruct != null; tempStruct = tempStruct.GetSuperStruct()) {
            if (tempStruct == someBase) {
                bOldResult = true;
                break;
            }
        }

        return bOldResult;
    }

    public UStruct? GetSuperStruct() => _SuperStruct;

    public virtual void SetSuperStruct(UStruct newSuperStruct) => _SuperStruct = newSuperStruct;
}
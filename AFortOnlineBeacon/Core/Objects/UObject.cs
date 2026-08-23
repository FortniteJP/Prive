namespace AFortOnlineBeacon.Core.Objects;

public class UObject : UObjectBaseUtility {
    public bool CheckDefaultSubobjects(bool bForceCheck = false) {
        // TODO: Implement
        return true;
    }

    /// <summary>
    ///     The object this one was constructed from. We don't track per-instance templates, so this
    ///     is always the class default object (matches UObject::GetArchetype()'s fallback behavior).
    /// </summary>
    public virtual UObject GetArchetype() => GetClass().GetDefaultObject();
}
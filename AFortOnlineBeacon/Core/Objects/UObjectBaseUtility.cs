using System.Runtime.CompilerServices;

namespace AFortOnlineBeacon.Core.Objects;

public class UObjectBaseUtility : UObjectBase {
    /*
     * Flags
     */
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetFlags(EObjectFlags newFlags) => SetFlagsTo(GetFlags() | newFlags);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearFlags(EObjectFlags newFlags) => SetFlagsTo(GetFlags() | ~newFlags);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasAnyFlags(EObjectFlags flagsToCheck) => (GetFlags() & flagsToCheck) != 0;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasAllFlags(EObjectFlags flagsToCheck) => (GetFlags() & flagsToCheck) == flagsToCheck;
    
    /*
     * Marks
     */
    public bool IsUnreachable() {
        // TODO: GUObjectArray
        return false;
    }

    public bool IsPendingKill() {
        // TODO: GUObjectArray
        return false;
    }
    
    /*
     * Outer & Package
     */
    public UObject? GetTypedOuter(UClass target) {
        UObject? result = null;
        for (var nextOuter = GetOuter(); result == null && nextOuter != null; nextOuter = nextOuter.GetOuter()) {
            if (nextOuter.IsA(target)) result = nextOuter;
        }
        return result;
    }
    
    public T? GetTypedOuter<T>() where T : UObject => (T?)GetTypedOuter(GUClassArray.StaticClass<T>());
    
    /*
     * Class
     */
    public bool IsChildOfWorkaround(UClass objClass, UClass testClass) => objClass.IsChildOf(testClass);
    
    public bool IsA(UClass someBase) {
        var someBaseClass = someBase;
        var thisClass = GetClass();

        return IsChildOfWorkaround(thisClass, someBaseClass);
    }

    /*
     * Networking - simplified port of UObject::IsNameStableForNetworking / IsFullNameStableForNetworking.
     * True for objects the client can resolve by path (CDOs, classes, packages); false for anything
     * spawned at runtime (actors, etc), which get dynamic GUIDs instead.
     */
    // RF_WasLoaded covers plain on-disk assets (see UAssetRegistry) - real UE's rule is exactly
    // HasAnyFlags(RF_WasLoaded | RF_DefaultSubObject) || IsNative() || IsDefaultSubobject()
    // (Obj.cpp:4515); the CDO/archetype/UClass/UPackage cases below stand in for IsNative().
    public virtual bool IsNameStableForNetworking() => HasAnyFlags(EObjectFlags.RF_WasLoaded | EObjectFlags.RF_ClassDefaultObject | EObjectFlags.RF_ArchetypeObject | EObjectFlags.RF_DefaultSubObject) || this is UPackage || this is UClass;

    /// <summary>
    ///     True for objects the server can hand a fresh (dynamic) NetGUID to and have the client spawn
    ///     on demand - i.e. actors. False by default; only overridden where UE itself overrides it.
    /// </summary>
    public virtual bool IsSupportedForNetworking() => false;

    public bool IsFullNameStableForNetworking() {
        if (!IsNameStableForNetworking()) return false;

        var outer = GetOuter();
        return outer == null || outer.IsFullNameStableForNetworking();
    }
}
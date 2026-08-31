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

    /// <summary>
    ///     UObjectBaseUtility::GetOutermost - walks to the top of the outer chain, which is by
    ///     construction the object's UPackage. Returns this object when it has no outer (a package
    ///     itself), matching the real implementation.
    /// </summary>
    public UObject GetOutermost() {
        var top = (UObject) this;
        for (var next = GetOuter(); next != null; next = next.GetOuter()) top = next;
        return top;
    }
    
    /*
     * Class
     */
    public bool IsChildOfWorkaround(UClass objClass, UClass testClass) => objClass.IsChildOf(testClass);
    
    public bool IsA(UClass someBase) {
        var someBaseClass = someBase;
        var thisClass = GetClass();

        // A null class is a real state here, not a "can't happen" - UAssetRegistry/UPackageRegistry
        // build plain UObject stand-ins (a level's synthetic Package/World/PersistentLevel chain) for
        // path-exporting a reference, and none of them are ever given a UClass. GetTypedOuter walks
        // straight through that chain via IsA, so without this it NREs the first time anything calls
        // GetLevel()/GetWorld() on an actor whose outer chain passes through one - which real UE
        // never has to answer, since IsChildOf itself assumes objClass is never null.
        if (thisClass == null) return false;

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
    /// <summary>
    ///     UObject::IsSupportedForNetworking (Obj.cpp:4532) - `return IsFullNameStableForNetworking()`.
    ///     Objects this returns false for can never be given a NetGUID at all
    ///     (FNetGUIDCache::SupportsObject), so a reference to one goes out as the invalid guid 0.
    ///     Subclasses that ARE networkable despite an unstable path have to say so: AActor does,
    ///     and so does any replicated component (UActorComponent::IsSupportedForNetworking is
    ///     `GetIsReplicated() || IsNameStableForNetworking()`).
    /// </summary>
    public virtual bool IsSupportedForNetworking() => IsFullNameStableForNetworking();

    public bool IsFullNameStableForNetworking() {
        if (!IsNameStableForNetworking()) return false;

        var outer = GetOuter();
        return outer == null || outer.IsFullNameStableForNetworking();
    }
}
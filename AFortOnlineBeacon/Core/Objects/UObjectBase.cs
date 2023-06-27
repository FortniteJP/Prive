using System.Runtime.CompilerServices;

namespace AFortOnlineBeacon.Core.Objects;

public class UObjectBase {
    /// <summary>
    ///     Flags used to track and report various object states.
    /// </summary>
    private EObjectFlags _ObjectFlags;

    /// <summary>
    ///     Index into GObjectArray...very private.
    /// </summary>
    private int _InternalIndex;

    /// <summary>
    ///     Class the object belongs to.
    /// </summary>
    private UClass _ClassPrivate;

    /// <summary>
    ///     Name of this object.
    /// </summary>
    private FName _NamePrivate;

    /// <summary>
    ///     Object this object resides in.
    /// </summary>
    private UObject? _OuterPrivate;

    public UObjectBase() {}

    /// <summary>
    ///     Returns the unique ID of the object...these are reused so it is only unique while the object is alive.
    ///     Useful as a tag.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetUniqueID() => (uint)_InternalIndex;

    /// <summary>
    ///     Returns the UClass that defines the fields of this object.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public UClass GetClass() => _ClassPrivate;

    /// <summary>
    ///     Returns the UObject this object resides in.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public UObject? GetOuter() => _OuterPrivate;

    /// <summary>
    ///     Returns the logical name of this object.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FName GetFName() => _NamePrivate;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void SetFlagsTo(EObjectFlags newFlags) => _ObjectFlags = newFlags;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EObjectFlags GetFlags() => _ObjectFlags;
}
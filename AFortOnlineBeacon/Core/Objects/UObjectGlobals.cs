namespace AFortOnlineBeacon.Core.Objects;

public class UObjectGlobals {
    // Simplified port of MakeUniqueObjectName: real UE scopes uniqueness by (Outer, Class) and
    // searches for the first unused name; we don't have a name registry to search against yet,
    // so a monotonic per-(Outer, Class) counter is good enough to avoid collisions in practice.
    private static readonly Dictionary<(UObject?, UClass), int> _uniqueNameCounters = new();

    private static FName MakeUniqueObjectName(UObject? outer, UClass clazz) {
        var key = (outer, clazz);
        int counter;
        // Locked: every world spawns through here, and a Dictionary written from two threads is
        // corrupted rather than merely raced.
        lock (_uniqueNameCounters) {
            _uniqueNameCounters.TryGetValue(key, out counter);
            _uniqueNameCounters[key] = counter + 1;
        }

        return new FName(clazz.GetFName().GetPlainNameString(), counter);
    }

    public static T? NewObject<T>(UObject outer,
        UClass clazz,
        FName? name = null,
        EObjectFlags flags = EObjectFlags.RF_NoFlags,
        UObject? template = null,
        bool bCopyTransientsFromClassDefaults = false,
        FObjectInstancingGraph? inInstanceGraph = null,
        UPackage? externalPackage = null) where T : UObject {
        name ??= EName.None;

        // NewObject
        // StaticConstructObject_Internal
        // StaticAllocateObject

        var objectName = name.Value == EName.None ? MakeUniqueObjectName(outer, clazz) : name.Value;

        var obj = (UObject?) Activator.CreateInstance(clazz.Type);
        if (obj == null) throw new UnrealException("Failed to create object.");

        obj.InitializeObjectProperties(outer, objectName, clazz);
        obj.SetFlags(flags);

        return (T?) obj;
    }
}
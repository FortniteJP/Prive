namespace AFortOnlineBeacon.Core.Objects;

public class GUClassArray {
    private static readonly Dictionary<Type, UClass> Classes = new();

    // Maps our C# actor classes to a REAL, always-loaded native UE class path the real client can
    // resolve via StaticFindObject (e.g. "/Script/Engine.PlayerController"). We don't have Fortnite's
    // own gameplay class paths yet (needs SDK dumps), so only classes with a native engine equivalent
    // can currently be replicated to a real client - see UClass.NativePackagePath.
    private static readonly Dictionary<Type, string> NativePackagePaths = new() {
        [typeof(APlayerController)] = "/Script/Engine.PlayerController",
        [typeof(APawn)] = "/Script/Engine.Pawn"
    };

    public static UClass StaticClass<T>() => StaticClass(typeof(T));

    public static UClass StaticClass(Type type) {
        if (Classes.TryGetValue(type, out var existing)) return existing;

        var uClass = new UClass(type);
        if (NativePackagePaths.TryGetValue(type, out var nativePath)) uClass.NativePackagePath = nativePath;

        Classes[type] = uClass;
        return uClass;
    }
}

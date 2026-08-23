namespace AFortOnlineBeacon.Core.Objects;

public class UClass : UStruct {
    public UClass(Type type) => Type = type;

    public Type Type { get; }

    /// <summary>
    ///     The real, native UE class path this maps to (e.g. "/Script/Engine.PlayerController"), used
    ///     so the class default object we hand to the real client is something it can actually resolve.
    ///     Null for classes we never replicate (GameMode, GameSession, ...).
    /// </summary>
    public string? NativePackagePath { get; set; }

    private UObject? _defaultObject;

    public UObject GetDefaultObject<T>() where T : UObject => GetDefaultObject(true);

    public UObject GetDefaultObject(bool bCreateIfNeeded = true) {
        if (_defaultObject == null && bCreateIfNeeded) _defaultObject = CreateDefaultObject();
        return _defaultObject!;
    }

    public UObject CreateDefaultObject() {
        var obj = (UObject) Activator.CreateInstance(Type)!;

        if (NativePackagePath != null) {
            var dot = NativePackagePath.LastIndexOf('.');
            var packagePath = NativePackagePath[..dot];
            var className = NativePackagePath[(dot + 1)..];

            var package = UPackageRegistry.GetOrCreate(packagePath);

            // Give this UClass instance itself a resolvable identity too, even though the CDO doesn't
            // reference it directly (see below) - nothing currently needs it, but it keeps GetClass()
            // consistent for anything that inspects the class object itself later.
            InitializeObjectProperties(package, new FName(className));

            // A native class's CDO is a *sibling* of the class, not nested under it - both are direct
            // children of the package (UClass::CreateDefaultObject uses GetOuter(), i.e. the package,
            // not the class itself). Getting this wrong is why the real client couldn't resolve
            // "Default__PlayerController" - it was looking for it directly under the package, not
            // under /Script/Engine.PlayerController.
            obj.InitializeObjectProperties(package, new FName($"Default__{className}"));
        } else {
            obj.InitializeObjectProperties(null, new FName($"Default__{Type.Name}"));
        }

        obj.SetFlags(EObjectFlags.RF_Public | EObjectFlags.RF_ClassDefaultObject | EObjectFlags.RF_ArchetypeObject | EObjectFlags.RF_Transient);

        return obj;
    }
}

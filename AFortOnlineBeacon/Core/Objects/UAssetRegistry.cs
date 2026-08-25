namespace AFortOnlineBeacon.Core.Objects;

/// <summary>
///     Models a plain, already-loaded on-disk asset (a UObject inside a /Game package) so it can be
///     referenced over the network by path - e.g. AFortPlayerState::HeroType, which points at a
///     UFortHeroType asset rather than at anything this server spawns.
///
///     Every object reference this project sent before this was either a dynamically-spawned actor
///     (fresh NetGUID, no path) or a class default object (UClass.CreateDefaultObject). An asset is
///     the third case: it needs a NetGUID exported WITH its path, which
///     UPackageMapClient.ExportNetGUID / InternalWriteObject's bHasPath branch already does - the
///     only thing missing was an object to hand it. One instance per path, because FNetGUIDCache's
///     lookup is keyed by object identity.
///
///     The RF_WasLoaded flag is what makes these name-stable: real UE's
///     UObject::IsNameStableForNetworking (Obj.cpp:4515) is
///     HasAnyFlags(RF_WasLoaded | RF_DefaultSubObject) || IsNative() || IsDefaultSubobject(),
///     and a loaded asset is precisely the RF_WasLoaded case.
///
///     Paths are the usual "package.object" form, e.g.
///     "/Game/Athena/Heroes/HID_001_Athena_Commando_F.HID_001_Athena_Commando_F". Note the client
///     must ALREADY have the asset loaded - this exports a reference, it does not stream content -
///     and an unresolvable path simply leaves the property unmapped client-side rather than
///     erroring, so a wrong path shows up as a silently missing value, not a disconnect.
/// </summary>
internal static class UAssetRegistry {
    private static readonly Dictionary<string, UObject> Assets = new();

    public static UObject GetOrCreate(string path) {
        if (Assets.TryGetValue(path, out var existing)) return existing;

        var dot = path.LastIndexOf('.');
        if (dot < 0) throw new ArgumentException($"UAssetRegistry: '{path}' is not a package.object path", nameof(path));

        var asset = new UObject();
        asset.InitializeObjectProperties(UPackageRegistry.GetOrCreate(path[..dot]), new FName(path[(dot + 1)..]));
        asset.SetFlags(EObjectFlags.RF_Public | EObjectFlags.RF_WasLoaded);

        Assets[path] = asset;
        return asset;
    }
}

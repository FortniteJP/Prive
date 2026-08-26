namespace AFortOnlineBeacon.Core;

public class UPackage : UObject {
    /// <summary>
    ///     UPackage::ContainsMap - true when this package holds a UWorld. Real UE reads it out of the
    ///     package summary it loaded off disk; nothing here ever loads a package, so
    ///     UAssetRegistry infers it from the shape of the path instead.
    ///
    ///     The one thing this drives is FNetGUIDCache.CanClientLoadObject. An object that lives
    ///     inside a map must NOT be announced to the client as a "must be mapped" GUID, because the
    ///     client cannot stream a map on demand - those GUIDs resolve only once it has travelled to
    ///     and loaded that map itself. Telling it to wait for one would stall the channel forever.
    /// </summary>
    public bool bContainsMap { get; set; }
}

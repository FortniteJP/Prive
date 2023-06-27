namespace AFortOnlineBeacon.Net;

public class FUniqueNetId {
    public FUniqueNetId(FName type, string contents) {
        Type = type;
        Contents = contents;
    }

    public FName Type { get; }
    public string Contents { get; }

    public bool IsValid() => true;
}